// tests/Ecr.Api.Tests/Health/HttpsTransportHealthTests.cs
using Ecr.Api.Health;
using Ecr.Api.Startup;
using Ecr.Application.Common;
using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests.Health;

/// <summary>
/// D14-08: <c>transport</c> жовтіє, коли Production працює по HTTP із cookie без <c>Secure</c>, або
/// коли сертифікат HTTPS спливає / прострочений; в інших станах — зелений і ніколи не 503.
/// </summary>
public sealed class HttpsTransportHealthTests
{
    private static readonly HealthCheckContext Context = new()
    {
        Registration = new HealthCheckRegistration("transport", Substitute.For<IHealthCheck>(), null, null),
    };

    /// <remarks>
    /// ⛔ Головний випадок (<c>-AllowHttp</c>): Production і <c>Auth:RequireHttps = false</c> — Degraded,
    /// не Unhealthy. Мутація (прогнано): у перевірці прибрати гілку <c>!requireHttps &amp;&amp; IsProduction</c> →
    /// зелений, тест червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Production_по_HTTP_без_Secure_cookie_жовтий()
    {
        var result = await CheckAsync("false", Environments.Production, TransportState.NoCertificate);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal(false, result.Data["requireHttps"]);
        Assert.Contains("not Secure", result.Description, StringComparison.Ordinal);
    }

    /// <remarks>
    /// ⚠ Тести й Development вимикають RequireHttps усюди: жовтий там світився б завжди.
    /// Мутація (прогнано): прибрати <c>IsProduction()</c> з умови → цей тест червоний.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task Поза_Production_RequireHttps_false_не_жовтить(string environment)
    {
        var result = await CheckAsync("false", environment, TransportState.NoCertificate);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    /// <remarks>
    /// Значення за замовчуванням (ключа немає) = <c>true</c>, і це зелений: типовий Production, а також
    /// режим «за зворотним проксі» (<c>-BehindHttpsProxy</c>), де <c>RequireHttps</c> не чіпають.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [InlineData(null)]
    [InlineData("true")]
    public async Task Production_із_RequireHttps_true_зелений(string? requireHttps)
    {
        var result = await CheckAsync(requireHttps, Environments.Production, TransportState.NoCertificate);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Сертифікат_на_рік_наперед_зелений()
    {
        var state = new TransportState(true, DateTimeOffset.UtcNow.AddDays(200));

        var result = await CheckAsync("true", Environments.Production, state);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(true, result.Data["httpsCertificate"]);
    }

    /// <remarks>
    /// Мутація (прогнано): у <c>HttpsTransport.ExpiryWarningDays</c> поставити 0 → «через 5 днів» зелений, червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Сертифікат_що_спливає_за_кілька_днів_жовтий_із_числом_днів()
    {
        var state = new TransportState(true, DateTimeOffset.UtcNow.AddDays(5).AddHours(1));

        var result = await CheckAsync("true", Environments.Production, state);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal(5, result.Data["certificateDaysLeft"]);
        Assert.Contains("expires in 5 day(s)", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Прострочений_сертифікат_жовтий_а_не_503()
    {
        var state = new TransportState(true, DateTimeOffset.UtcNow.AddDays(-2));

        var result = await CheckAsync("true", Environments.Production, state);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("expired on", result.Description, StringComparison.Ordinal);
    }

    private static Task<HealthCheckResult> CheckAsync(string? requireHttps, string environmentName, TransportState state)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:RequireHttps"] = requireHttps })
            .Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        var user = Substitute.For<ICurrentUser>();
        user.Language.Returns("en");

        return new TransportHealthCheck(configuration, environment, state, new FakeUiStringCatalog(), user)
            .CheckHealthAsync(Context, CancellationToken.None);
    }
}
