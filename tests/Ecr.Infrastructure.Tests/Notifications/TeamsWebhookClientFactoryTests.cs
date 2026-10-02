// tests/Ecr.Infrastructure.Tests/Notifications/TeamsWebhookClientFactoryTests.cs
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ecr.Application;
using Ecr.Infrastructure;
using Ecr.Infrastructure.Notifications;
using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Infrastructure.Tests.Notifications;

/// <summary>S9: РЕАЛЬНА DI-реєстрація клієнта вебхука Teams не слідує редиректам (а не лише <c>CreateHandler</c>).</summary>
public sealed class TeamsWebhookClientFactoryTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Клієнт_із_контейнера_не_слідує_302_на_чужий_хост()
    {
        // ⛔ МУТАЦІЯ: прибрати `ConfigurePrimaryHttpMessageHandler(TeamsWebhookSender.CreateHandler)` у
        // DependencyInjection.cs → клієнт іде за редиректом, до другого слухача доходить з'єднання, тест червоніє.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Ecr"] = "Server=unused;Database=unused",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEcrApplication();
        services.AddEcrInfrastructure(configuration);
        await using var provider = services.BuildServiceProvider();

        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        var targetAccept = target.AcceptTcpClientAsync();

        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var conn = await origin.AcceptTcpClientAsync();
            var stream = conn.GetStream();
            _ = await stream.ReadAsync(new byte[4096]);
            var reply = $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{targetPort}/internal\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply));
        });

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(TeamsWebhookSender.HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(5);
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri($"http://127.0.0.1:{originPort}/hook"), content);
        await serve;

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.NotSame(targetAccept, await Task.WhenAny(targetAccept, Task.Delay(500)));
    }
}