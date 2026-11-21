using System.Net;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// PSS-28: з налаштованим <c>AllowedHosts</c> чужий <c>Host</c> дає 400, свої імена — проходять; запис із портом
/// службу не запускає (HostFiltering порівнює записи без порту, тож він замикав би всіх).
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНІ ДОКАЗИ: прибрати перевірку порту з <c>EcrConfigurationValidation.AllowedHostsProblem</c> — червоніє тест
/// «запис із портом»; не передавати <c>AllowedHosts</c> у конфігурацію хоста — червоніє 400 на <c>evil.example</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class AllowedHostsApiTests(SqlServerFixture sql)
{
    private static readonly Uri Probe = new("/api/v1/public/bootstrap", UriKind.Relative);

    [Theory]
    [InlineData("evil.example", HttpStatusCode.BadRequest)]
    [InlineData("ecr.example.kz.evil.example", HttpStatusCode.BadRequest)]
    [InlineData("ecr.example.kz", HttpStatusCode.OK)]
    [InlineData("ecr.example.kz:8443", HttpStatusCode.OK)] // порт із заголовка Host відкидається, ім'я збігається
    [InlineData("localhost", HttpStatusCode.OK)]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PSS-28")]
    public async Task З_налаштованим_AllowedHosts_чужий_Host_дає_400_а_свої_імена_проходять(string host, HttpStatusCode expected)
    {
        using var app = new EcrApiFactory(sql);
        using var configured = app.WithWebHostBuilder(
            builder => builder.UseSetting("AllowedHosts", "ecr.example.kz;localhost"));
        using var client = configured.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, Probe);
        request.Headers.Host = host;
        using var response = await client.SendAsync(request).ConfigureAwait(true);

        Assert.True(response.StatusCode == expected, $"Host: {host} -> {(int)response.StatusCode}; {app.ErrorsText}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PSS-28")]
    public void Запис_AllowedHosts_із_портом_службу_не_запускає()
    {
        using var app = new EcrApiFactory(sql);
        using var configured = app.WithWebHostBuilder(
            builder => builder.UseSetting("AllowedHosts", "ecr.example.kz:8443;localhost"));

        var failure = Record.Exception(() => configured.CreateClient());

        Assert.NotNull(failure);
        Assert.Contains("AllowedHosts", Flatten(failure), StringComparison.Ordinal);
    }

    private static string Flatten(Exception root)
    {
        var parts = new List<string>();
        for (var e = root; e is not null; e = e.InnerException!)
        {
            parts.Add(e.Message);
            if (e.InnerException is null)
            {
                break;
            }
        }

        return string.Join(" | ", parts);
    }
}
