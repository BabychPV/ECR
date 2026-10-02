// tests/Ecr.Infrastructure.Tests/Notifications/TeamsWebhookRedirectTests.cs
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ecr.Infrastructure.Notifications;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Notifications;

/// <summary>S9: клієнт вебхука Teams не йде за редиректами (allowlist не перевірявся б повторно).</summary>
public sealed class TeamsWebhookRedirectTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Відповідь_302_на_чужий_хост_не_слідується()
    {
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
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer);
            var reply = $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{targetPort}/internal\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply));
        });

        using var client = new HttpClient(TeamsWebhookSender.CreateHandler());
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri($"http://127.0.0.1:{originPort}/hook"), content);
        await serve;

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var winner = await Task.WhenAny(targetAccept, Task.Delay(500));
        Assert.NotSame(targetAccept, winner);
    }
}