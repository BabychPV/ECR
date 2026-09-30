// tests/Ecr.Api.Tests/Security/CspReportParserTests.cs

using System.Text;
using Ecr.Api.Options;
using Ecr.Api.Security;
using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// Розбір і очищення тіла звіту CSP, а також перевірка ключів <c>Security:Csp:*</c>
/// (<c>S14</c>). Без SQL Server і без хоста.
/// </summary>
public sealed class CspReportParserTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    [InlineData("https://h.example/a/b?token=1#x", "https://h.example/a/b")]
    [InlineData("https://user:pw@h.example:8443/a?q=1", "https://h.example:8443/a")]
    [InlineData("/relative/path?q=1#f", "/relative/path")]
    [InlineData("inline", "inline")]
    [InlineData("eval", "eval")]
    [InlineData("data:image/png;base64,AAAA", "data")]
    [InlineData("blob:https://h.example/uuid-1", "blob")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Адреса_втрачає_query_fragment_і_userinfo(string? input, string expected)
        => Assert.Equal(expected, CspReportParser.StripUrl(input));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    public void Довжина_обрізається_а_керівні_символи_замінюються()
    {
        var url = "https://h.example/" + new string('a', 1000);
        var stripped = CspReportParser.StripUrl(url);
        Assert.Equal(CspReportParser.MaxFieldLength, stripped.Length);

        // Розщеплення рядка журналу через \n у чужому полі.
        var body = Encoding.UTF8.GetBytes("""{"csp-report":{"blocked-uri":"a\r\nFAKE LOG LINE","violated-directive":"script-src"}}""");
        Assert.True(CspReportParser.TryParse(body, out var violations));

        var blocked = Assert.Single(violations).BlockedUri;
        Assert.DoesNotContain('\n', blocked);
        Assert.DoesNotContain('\r', blocked);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    [InlineData("script-src 'self'", "script-src")]
    [InlineData("STYLE-SRC-ELEM", "style-src-elem")]
    [InlineData("img-src", "img-src")]
    [InlineData("nonsense-src x", "other")]
    [InlineData("", "other")]
    [InlineData(null, "other")]
    public void Тег_директиви_лише_із_закритого_переліку(string? input, string expected)
        => Assert.Equal(expected, CspReportParser.DirectiveName(input));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    public void Рядок_line_number_чи_від_ємне_число_дають_null()
    {
        var body = Encoding.UTF8.GetBytes("""{"csp-report":{"violated-directive":"script-src","line-number":"12"}}""");
        Assert.True(CspReportParser.TryParse(body, out var textual));
        Assert.Null(Assert.Single(textual).LineNumber);

        body = Encoding.UTF8.GetBytes("""{"csp-report":{"violated-directive":"script-src","line-number":-4}}""");
        Assert.True(CspReportParser.TryParse(body, out var negative));
        Assert.Null(Assert.Single(negative).LineNumber);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    public void Занадто_глибокий_JSON_це_400_а_не_виняток()
    {
        var deep = string.Concat(Enumerable.Repeat("[", 200)) + string.Concat(Enumerable.Repeat("]", 200));

        Assert.False(CspReportParser.TryParse(Encoding.UTF8.GetBytes(deep), out _));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    public void Масив_Reporting_API_обмежений_двадцятьма_звітами()
    {
        var item = """{"type":"csp-violation","body":{"effectiveDirective":"img-src","blockedURL":"x"}}""";
        var body = Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat(item, 50)) + "]");

        Assert.True(CspReportParser.TryParse(body, out var violations));
        Assert.Equal(20, violations.Count);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    [InlineData("", true)]
    [InlineData("/api/v1/csp-report", true)]
    [InlineData("https://collector.example/csp", true)]
    [InlineData("/x;y", false)]
    [InlineData("/x,y", false)]
    [InlineData("/x y", false)]
    [InlineData("/x'y", false)]
    [InlineData("//evil.example/r", false)]
    [InlineData("relative/no-slash", false)]
    [InlineData("javascript:alert(1)", false)]
    public void ReportUri_перевіряється_до_дописування_в_заголовок(string value, bool valid)
        => Assert.Equal(valid, CspSettings.IsValidReportUri(value));

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    [InlineData("Security:Csp:ReportOnly", "yes")]
    [InlineData("Security:Csp:Enforce", "1")]
    [InlineData("Security:RateLimit:CspReportPermitPerMinute", "0")]
    [InlineData("Security:RateLimit:CspReportPermitPerMinute", "багато")]
    [InlineData("Security:Csp:ReportUri", "/x; script-src *")]
    public void Недійсний_ключ_CSP_зупиняє_старт_з_іменем_ключа(string key, string value)
    {
        var problem = Assert.Single(EcrConfigurationValidation.Validate(Config((key, value))));

        Assert.StartsWith(key + " = «" + value + "»", problem, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    public void Типові_значення_проходять_а_Enforce_true_дає_попередження_не_помилку()
    {
        Assert.Empty(EcrConfigurationValidation.Validate(Config(
            ("Security:Csp:ReportOnly", "true"),
            ("Security:Csp:Enforce", "false"),
            ("Security:Csp:ReportUri", "/api/v1/csp-report"),
            ("Security:RateLimit:CspReportPermitPerMinute", "120"))));
        Assert.Empty(EcrConfigurationValidation.Validate(Config(("Security:Csp:ReportUri", ""))));
        Assert.Empty(EcrConfigurationValidation.Warnings(Config(("Security:Csp:Enforce", "false"))));

        var enforce = Config(("Security:Csp:Enforce", "true"));
        Assert.Empty(EcrConfigurationValidation.Validate(enforce));

        var warning = Assert.Single(EcrConfigurationValidation.Warnings(enforce));
        Assert.Contains("Security:Csp:Enforce", warning, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    public void Типові_значення_із_appsettings_збігаються_з_дефолтами_коду()
    {
        // ⛔ Два джерела дефолтів: файл і код. Без цього тесту `Security:Csp:Enforce`
        // у файлі міг би стати `true` — і код без файла (ключ стерто) поводився б інакше.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Ecr.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);

        var shipped = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(root!.FullName, "src", "Ecr.Api", "appsettings.json"), optional: false)
            .Build();

        var fromFile = CspSettings.From(shipped);
        var fromCode = CspSettings.From(new ConfigurationBuilder().Build());

        Assert.Equal(fromCode, fromFile);
        Assert.True(fromFile.ReportOnly);
        Assert.False(fromFile.Enforce);
        Assert.Equal(CspSettings.DefaultCspReportUri, fromFile.ReportUri);
    }

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}
