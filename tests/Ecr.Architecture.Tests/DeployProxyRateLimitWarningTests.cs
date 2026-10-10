using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// S2-02 (залишок, аудит 2026-10-09b): <c>-BehindHttpsProxy</c> не пише <c>TrustForwardedFor</c>/<c>KnownProxies</c>
/// (адресу проксі знає лише той, хто його налаштував — рішення людини), а MSI стирає <c>Environment</c> служби.
/// Тому попередження режиму проксі прямо називає обидві змінні й те, що їх треба виставляти знову після кожного
/// оновлення, — замість мовчазної «одної межі входу на всіх клієнтів».
/// </summary>
/// <remarks>
/// Предмет — справжня функція скрипта (<see cref="DeployScriptHarness"/>). Тести/мутація — CI.
/// </remarks>
public sealed class DeployProxyRateLimitWarningTests
{
    private const string Body = """
        $v = Resolve-TransportConfig -BehindHttpsProxy -AppPort 5000
        $text = @($v.Warnings) -join ' '
        "proxy.count=$(@($v.Warnings).Count)"
        "proxy.trust=$($text.Contains('ECR_Security__RateLimit__TrustForwardedFor'))"
        "proxy.known=$($text.Contains('ECR_Security__RateLimit__KnownProxies'))"
        "proxy.notWritten=$(-not $v.Set.Contains('ECR_Security__RateLimit__TrustForwardedFor') -and -not $v.Set.Contains('ECR_Security__RateLimit__KnownProxies'))"
        $w = Resolve-TransportConfig -AllowHttp -AppPort 5000
        "http.known=$((@($w.Warnings) -join ' ').Contains('KnownProxies'))"
        """;

    private static readonly Lazy<Dictionary<string, string>> Results =
        new(() => DeployScriptHarness.Run(["Resolve-TransportConfig"], Body));

    /// <remarks>
    /// Мутація (CI): прибрати речення S2-02 з попередження → червоні <c>proxy.trust</c>/<c>proxy.known</c>.
    /// Режим не пише змінних сам (<c>proxy.notWritten</c>): запис без адреси проксі — рішення людини, не скрипта.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("proxy.count", "1")]
    [InlineData("proxy.trust", "True")]
    [InlineData("proxy.known", "True")]
    [InlineData("proxy.notWritten", "True")]
    [InlineData("http.known", "False")]
    public void Попередження_режиму_проксі_називає_змінні_межі_входу(string key, string expected)
    {
        Assert.Equal(expected, DeployScriptHarness.Value(Results.Value, key));
    }
}
