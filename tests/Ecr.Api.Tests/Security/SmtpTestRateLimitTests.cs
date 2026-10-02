// tests/Ecr.Api.Tests/Security/SmtpTestRateLimitTests.cs

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>Межа частоти проб транспорту: 5/хв на користувача, 30/год на систему (D-263).</summary>
[Collection("SqlServer")]
public sealed class SmtpTestRateLimitTests(SqlServerFixture sql)
{
    private const string Permission = "System.ManageNotifications";

    /// <summary>Межа — ЛІТЕРАЛОМ: константа продукту рухалася б разом із перевіркою.</summary>
    private const int PermitPerMinute = 5;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task П_ять_проб_проходять_шоста_за_хвилину_дає_429_ECR_REQ_0429()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, Permission).ConfigureAwait(true);

        for (var i = 1; i <= PermitPerMinute; i++)
        {
            using var allowed = await ProbeAsync(client).ConfigureAwait(true);
            Assert.True(allowed.StatusCode != HttpStatusCode.TooManyRequests, $"проба №{i}: {allowed.StatusCode}");
        }

        using var rejected = await ProbeAsync(client).ConfigureAwait(true);

        // Мутація: прибрати `[EnableRateLimiting]` із `SmtpSettingsController.Test` — падає тут.
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(rejected.Headers.RetryAfter);

        var json = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal("ECR-REQ-0429", json.GetProperty("errorCode").GetString());
        Assert.Contains("test message", json.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Проба_каналу_ділить_межу_з_пробою_налаштувань_а_інший_користувач_вільний()
    {
        using var app = new EcrApiFactory(sql);
        using var greedy = await SystemHealthControllerTests.SignedInAsync(sql, app, Permission).ConfigureAwait(true);
        using var other = await SystemHealthControllerTests.SignedInAsync(sql, app, Permission).ConfigureAwait(true);

        for (var i = 0; i < PermitPerMinute; i++)
        {
            using var _ = await ProbeChannelAsync(greedy).ConfigureAwait(true);
        }

        // Мутація: прибрати атрибут з `NotificationChannelsController.Test` — падає тут.
        using var blocked = await ProbeChannelAsync(greedy).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);

        // Мутація: сталий ключ розділу в `SmtpTestRateLimitPolicy.GetPartition` — падає тут.
        using var allowed = await ProbeAsync(other).ConfigureAwait(true);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Системна_межа_різе_і_різних_користувачів()
    {
        using var baseApp = new EcrApiFactory(sql);
        using var app = baseApp.WithWebHostBuilder(
            b => b.UseSetting("Security:RateLimit:SmtpTestSystemPermitPerHour", "2"));
        using var first = await SystemHealthControllerTests.SignedInAsync(sql, app, Permission).ConfigureAwait(true);
        using var second = await SystemHealthControllerTests.SignedInAsync(sql, app, Permission).ConfigureAwait(true);

        using var one = await ProbeAsync(first).ConfigureAwait(true);
        using var two = await ProbeAsync(second).ConfigureAwait(true);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, one.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, two.StatusCode);

        // Мутація: прибрати гілку `IsSmtpTest` із глобального обмежувача — падає тут.
        using var three = await ProbeAsync(first).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.TooManyRequests, three.StatusCode);

        var json = JsonDocument.Parse(await three.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal("ECR-REQ-0429", json.GetProperty("errorCode").GetString());
    }

    private static Task<HttpResponseMessage> ProbeAsync(HttpClient client)
        => client.PostAsJsonAsync(
            new Uri("/api/v1/notifications/smtp/test", UriKind.Relative), new { to = "probe@example.com" });

    private static Task<HttpResponseMessage> ProbeChannelAsync(HttpClient client)
        => client.PostAsync(new Uri("/api/v1/notifications/channels/999999/test", UriKind.Relative), null);
}
