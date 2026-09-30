// tests/Ecr.Api.Tests/Security/CspHeadersTests.cs

using Ecr.Api.Controllers;
using Ecr.Api.Security;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// Звітна CSP (<c>Report-Only</c>) — сувора, вимірна, і НЕ примусова (<c>S14</c>).
/// </summary>
/// <remarks>
/// ⛔ Рішення релізу-кандидата: політику НЕ переводимо в enforce — браузерний набір
/// (<c>e2e-stand.ps1</c>) під нею не проганявся, а мовчки зламаний застосунок гірший
/// за відсутню директиву. Тому тут два боки: звітна політика повна й веде на
/// приймач, а примусова частина за замовчуванням лишилась байт-у-байт та сама.
///
/// ⚠ Без SQL Server: <see cref="CspTestHost"/> піднімає лише middleware.
/// </remarks>
public sealed class CspHeadersTests
{
    private const string Csp = "Content-Security-Policy";
    private const string ReportOnly = "Content-Security-Policy-Report-Only";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    public async Task Типово_звітний_заголовок_повний_сувора_політика_з_report_uri()
    {
        await using var host = await CspTestHost.StartAsync().ConfigureAwait(true);

        var response = await host.Http.GetAsync(new Uri("/anything", UriKind.Relative)).ConfigureAwait(true);
        var policy = Directives(Single(response, ReportOnly));

        // ⚠ Кожна директива названа ПОВНИМ значенням, а не «містить». `script-src 'self'
        // 'unsafe-inline'` теж «містить script-src 'self'» — і це рівно та поблажка,
        // яку звітна політика має ловити, а не дозволяти.
        Assert.Equal("'self'", policy["default-src"]);
        Assert.Equal("'self'", policy["script-src"]);
        Assert.Equal("'self' 'unsafe-inline'", policy["style-src"]);
        Assert.Equal("'self' data:", policy["img-src"]);
        Assert.Equal("'self'", policy["font-src"]);
        Assert.Equal("'self'", policy["connect-src"]);
        Assert.Equal("'none'", policy["frame-ancestors"]);
        Assert.Equal("'self'", policy["base-uri"]);
        Assert.Equal("'self'", policy["form-action"]);
        Assert.Equal("'none'", policy["object-src"]);

        // ⛔ Мутаційний доказ: прибрати `report-uri` із PolicyPair.For — падає цей
        // рядок. Політика без адресата — рядок у консолі, який ніхто не читає.
        Assert.Equal(CspSettings.DefaultCspReportUri, policy["report-uri"]);

        // ⚠ HTTP: `report-to` немає — Chrome ігнорував би через нього `report-uri`,
        // а Reporting API поза HTTPS не працює, тобто звітів не було б узагалі.
        Assert.False(policy.ContainsKey("report-to"), "report-to поверх HTTP глушить report-uri");
        Assert.False(response.Headers.Contains(SecurityHeadersMiddleware.ReportingEndpointsHeader));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    public async Task На_HTTPS_додається_report_to_і_Reporting_Endpoints()
    {
        await using var host = await CspTestHost.StartAsync().ConfigureAwait(true);

        var response = await host.Https.GetAsync(new Uri("/anything", UriKind.Relative)).ConfigureAwait(true);
        var policy = Directives(Single(response, ReportOnly));

        Assert.Equal(CspSettings.DefaultCspReportUri, policy["report-uri"]);
        Assert.Equal(SecurityHeadersMiddleware.ReportingGroup, policy["report-to"]);
        Assert.Equal(
            $"{SecurityHeadersMiddleware.ReportingGroup}=\"{CspSettings.DefaultCspReportUri}\"",
            Single(response, SecurityHeadersMiddleware.ReportingEndpointsHeader));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    public async Task Типово_примусова_політика_без_script_src_і_без_змін()
    {
        await using var host = await CspTestHost.StartAsync().ConfigureAwait(true);

        var response = await host.Http.GetAsync(new Uri("/anything", UriKind.Relative)).ConfigureAwait(true);
        var enforced = Single(response, Csp);

        // ⛔ Мутаційний доказ: підставити повну політику в `_enforced` без
        // `Enforce = true` — падає. Байт-у-байт: `S14` наявних enforce-директив не міняє.
        Assert.Equal(SecurityHeadersMiddleware.ContentSecurityPolicy, enforced);
        Assert.DoesNotContain("script-src", enforced, StringComparison.Ordinal);
        Assert.DoesNotContain("default-src", enforced, StringComparison.Ordinal);
        Assert.DoesNotContain("report-uri", enforced, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    public async Task Enforce_true_робить_повну_політику_примусовою_і_прибирає_звітну()
    {
        await using var host = await CspTestHost.StartAsync(
            new Dictionary<string, string?> { ["Security:Csp:Enforce"] = "true" }).ConfigureAwait(true);

        var response = await host.Http.GetAsync(new Uri("/anything", UriKind.Relative)).ConfigureAwait(true);
        var enforced = Directives(Single(response, Csp));

        Assert.Equal("'self'", enforced["script-src"]);
        Assert.Equal("'self'", enforced["default-src"]);
        Assert.Equal("'none'", enforced["frame-ancestors"]);
        Assert.Equal(CspSettings.DefaultCspReportUri, enforced["report-uri"]);

        // Той самий текст двічі дав би кожне порушення в двох звітах.
        Assert.False(response.Headers.Contains(ReportOnly), "звітний заголовок лишився поряд із примусовим");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    public async Task ReportOnly_false_прибирає_звітний_заголовок_а_примусовий_лишає()
    {
        await using var host = await CspTestHost.StartAsync(
            new Dictionary<string, string?> { ["Security:Csp:ReportOnly"] = "false" }).ConfigureAwait(true);

        var response = await host.Https.GetAsync(new Uri("/anything", UriKind.Relative)).ConfigureAwait(true);

        Assert.False(response.Headers.Contains(ReportOnly));
        Assert.False(response.Headers.Contains(SecurityHeadersMiddleware.ReportingEndpointsHeader));
        Assert.Equal(SecurityHeadersMiddleware.ContentSecurityPolicy, Single(response, Csp));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    [InlineData("")]
    [InlineData("/x; script-src *")]
    [InlineData("//evil.example/r")]
    [InlineData("/a b")]
    [InlineData("ftp://host/r")]
    public async Task Порожній_чи_недійсний_ReportUri_не_потрапляє_в_заголовок(string reportUri)
    {
        await using var host = await CspTestHost.StartAsync(
            new Dictionary<string, string?> { ["Security:Csp:ReportUri"] = reportUri }).ConfigureAwait(true);

        var response = await host.Https.GetAsync(new Uri("/anything", UriKind.Relative)).ConfigureAwait(true);
        var reportOnly = Single(response, ReportOnly);

        // ⛔ `;` у значенні відкрив би нову директиву: `script-src *` у ЗВІТНІЙ політиці —
        // це вже ослаблення, яке вимірювання не побачило б.
        Assert.DoesNotContain("report-uri", reportOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("report-to", reportOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src *", reportOnly, StringComparison.Ordinal);
        Assert.False(response.Headers.Contains(SecurityHeadersMiddleware.ReportingEndpointsHeader));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    public async Task Абсолютний_ReportUri_приймається()
    {
        await using var host = await CspTestHost.StartAsync(
            new Dictionary<string, string?> { ["Security:Csp:ReportUri"] = "https://collector.example/csp" })
            .ConfigureAwait(true);

        var response = await host.Http.GetAsync(new Uri("/anything", UriKind.Relative)).ConfigureAwait(true);

        Assert.Equal(
            "https://collector.example/csp",
            Directives(Single(response, ReportOnly))["report-uri"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    public void Типова_адреса_звітів_збігається_з_маршрутом_контролера()
    {
        // ⛔ Три місця мусять казати те саме: константа маршруту (обмежувач), атрибут
        // `[Route]` і типова адреса в заголовку та в appsettings. Розбіжність не
        // падає ніде, крім цього тесту: браузер просто стукає в неіснуючий шлях.
        var route = typeof(CspReportController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), inherit: false)
            .Cast<Microsoft.AspNetCore.Mvc.RouteAttribute>()
            .Single()
            .Template;

        Assert.Equal(CspReportController.RoutePath, "/" + route);
        Assert.Equal(CspSettings.DefaultCspReportUri, CspReportController.RoutePath);
    }

    /// <summary>Значення заголовка одним рядком; порожній — якщо його немає.</summary>
    private static string Single(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values)
            ? string.Join(", ", values)
            : string.Empty;

    /// <summary>Політика → «директива → значення».</summary>
    private static Dictionary<string, string> Directives(string policy)
        => policy
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split(' ', 2, StringSplitOptions.TrimEntries))
            .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : string.Empty, StringComparer.Ordinal);
}
