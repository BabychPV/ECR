using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// D14-08/R-01: <c>deploy-ecr.ps1</c> не ставить HTTP мовчки — транспорт обирається явно, а сертифікат
/// HTTPS перевіряється до будь-якої зміни системи.
/// </summary>
/// <remarks>
/// ⛔ Предмет — справжні функції скрипта (<c>Resolve-TransportConfig</c>, <c>Get-HttpsCertificateProblem</c>),
/// вирізані парсером PowerShell і виконані (<see cref="DeployScriptHarness"/>); сховище сертифікатів
/// підмінено ін'єктованим провайдером. Реєстру, служби й справжнього сертифіката тут немає — це
/// перевіряється лише на живому сервері.
/// </remarks>
public sealed class DeployTransportTests
{
    private const string Thumbprint = "0123456789ABCDEF0123456789ABCDEF01234567";

    private const string Body = """
        function Out-Transport([string] $key, $v) {
            $set = $v.Set
            "t.$key.mode=$($v.Mode)"
            "t.$key.code=$($v.Code)"
            "t.$key.problem=$([bool] $v.Problem)"
            "t.$key.urls=$(if ($set.Contains('ASPNETCORE_URLS')) { $set['ASPNETCORE_URLS'] } else { '-' })"
            "t.$key.require=$(if ($set.Contains('ECR_Auth__RequireHttps')) { $set['ECR_Auth__RequireHttps'] } else { '-' })"
            "t.$key.thumbSet=$($set.Contains('ECR_Transport__Https__CertificateThumbprint'))"
            "t.$key.port=$(if ($set.Contains('ECR_Transport__Https__Port')) { $set['ECR_Transport__Https__Port'] } else { '-' })"
            "t.$key.remove=$(@($v.Remove) -join ',')"
            "t.$key.warnings=$(@($v.Warnings).Count)"
            "t.$key.scheme=$($v.ProbeScheme)"
        }
        $tp = '0123456789ABCDEF0123456789ABCDEF01234567'
        Out-Transport 'none'       (Resolve-TransportConfig -AppPort 5000)
        Out-Transport 'https'      (Resolve-TransportConfig -HttpsThumbprint $tp -AppPort 443)
        Out-Transport 'redirect'   (Resolve-TransportConfig -HttpsThumbprint $tp -AppPort 443 -HttpRedirectPort 80)
        Out-Transport 'proxy'      (Resolve-TransportConfig -BehindHttpsProxy -AppPort 5000)
        Out-Transport 'http'       (Resolve-TransportConfig -AllowHttp -AppPort 5000)
        Out-Transport 'httpsHttp'  (Resolve-TransportConfig -HttpsThumbprint $tp -AllowHttp -AppPort 443)
        Out-Transport 'proxyHttp'  (Resolve-TransportConfig -BehindHttpsProxy -AllowHttp -AppPort 5000)
        Out-Transport 'redirNoTls' (Resolve-TransportConfig -AllowHttp -AppPort 5000 -HttpRedirectPort 80)
        Out-Transport 'redirSame'  (Resolve-TransportConfig -HttpsThumbprint $tp -AppPort 443 -HttpRedirectPort 443)
        Out-Transport 'badPort'    (Resolve-TransportConfig -AllowHttp -AppPort 70000)

        $global:calls = 0
        $now = [datetime]'2026-09-30T12:00:00'
        function New-Cert($key, $after, $before) { [pscustomobject]@{ HasPrivateKey = $key; NotAfter = [datetime]$after; NotBefore = [datetime]$before; Subject = 'CN=ecr-test' } }
        function Out-Cert([string] $key, [string] $thumbprint, $cert) {
            $global:calls = 0
            $provider = { param($t) $global:calls++; $cert }.GetNewClosure()
            $v = Get-HttpsCertificateProblem -Thumbprint $thumbprint -Provider $provider -Now $now
            "c.$key.code=$($v.Code)"
            "c.$key.problem=$([bool] $v.Problem)"
            "c.$key.warning=$([bool] $v.Warning)"
            "c.$key.calls=$($global:calls)"
        }
        Out-Cert 'ok'        $tp (New-Cert $true  '2027-06-01' '2026-01-01')
        Out-Cert 'lower'     $tp.ToLower() (New-Cert $true  '2027-06-01' '2026-01-01')
        Out-Cert 'missing'   $tp $null
        Out-Cert 'noKey'     $tp (New-Cert $false '2027-06-01' '2026-01-01')
        Out-Cert 'expired'   $tp (New-Cert $true  '2026-09-01' '2025-01-01')
        Out-Cert 'notYet'    $tp (New-Cert $true  '2028-01-01' '2026-10-05')
        Out-Cert 'soon'      $tp (New-Cert $true  '2026-10-10' '2026-01-01')
        Out-Cert 'short'     'ABCD' (New-Cert $true  '2027-06-01' '2026-01-01')
        Out-Cert 'notHex'    ('Z' * 40) (New-Cert $true  '2027-06-01' '2026-01-01')
        Out-Cert 'tooLong'   ($tp + '0') (New-Cert $true  '2027-06-01' '2026-01-01')
        """;

    private static readonly Lazy<Dictionary<string, string>> Results = new(() => DeployScriptHarness.Run(
        ["Resolve-TransportConfig", "Get-HttpsCertificateProblem"], Body));

    /// <remarks>
    /// ⛔ Головне: жодного транспорту — відмова, а не HTTP. Мутація (прогнано): у <c>Resolve-TransportConfig</c>
    /// прибрати гілку <c>$chosen -eq 0</c> → <c>t.none.code</c> червоний, і без параметра скрипт мовчки
    /// ставив би службу, у яку не можна увійти з іншої машини.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("none", "code", "NoTransport")]
    [InlineData("none", "problem", "True")]
    [InlineData("httpsHttp", "code", "Ambiguous")]
    [InlineData("proxyHttp", "code", "Ambiguous")]
    [InlineData("redirNoTls", "code", "RedirectWithoutHttps")]
    [InlineData("redirSame", "code", "RedirectPortEqualsAppPort")]
    [InlineData("badPort", "code", "BadPort")]
    public void Транспорт_обирається_явно_і_суперечливі_параметри_відхиляються(string scenario, string field, string expected)
        => Assert.Equal(expected, DeployScriptHarness.Value(Results.Value, $"t.{scenario}.{field}"));

    /// <remarks>
    /// HTTPS: адреса https на порту застосунку, відбиток у змінній, RequireHttps = true, http-порту немає.
    /// Мутація (прогнано): у гілці Https писати <c>http://+:</c> → <c>t.https.urls</c> червоний.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("mode", "Https")]
    [InlineData("urls", "https://+:443")]
    [InlineData("require", "true")]
    [InlineData("thumbSet", "True")]
    [InlineData("port", "-")]
    [InlineData("remove", "ECR_Transport__Https__Port")]
    [InlineData("warnings", "0")]
    [InlineData("scheme", "https")]
    public void Https_пише_https_адресу_відбиток_і_RequireHttps_true(string field, string expected)
        => Assert.Equal(expected, DeployScriptHarness.Value(Results.Value, $"t.https.{field}"));

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("urls", "https://+:443;http://+:80")]
    [InlineData("port", "443")]
    [InlineData("remove", "")]
    [InlineData("require", "true")]
    [InlineData("warnings", "1")]
    public void Перенаправлення_додає_http_порт_і_порт_HTTPS_для_застосунку(string field, string expected)
        => Assert.Equal(expected, DeployScriptHarness.Value(Results.Value, $"t.redirect.{field}"));

    /// <remarks>
    /// За проксі політика Secure-cookie лишається (<c>true</c>): браузер говорить із проксі по HTTPS.
    /// Мутація (прогнано): у гілці Proxy писати <c>false</c> → <c>t.proxy.require</c> червоний — саме так
    /// тихо послаблювалася б політика, якої ніхто не просив.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("mode", "Proxy")]
    [InlineData("urls", "http://+:5000")]
    [InlineData("require", "true")]
    [InlineData("thumbSet", "False")]
    [InlineData("remove", "ECR_Transport__Https__CertificateThumbprint,ECR_Transport__Https__Port")]
    [InlineData("warnings", "1")]
    [InlineData("scheme", "http")]
    public void За_проксі_http_а_RequireHttps_лишається_true(string field, string expected)
        => Assert.Equal(expected, DeployScriptHarness.Value(Results.Value, $"t.proxy.{field}"));

    /// <remarks>
    /// ⛔ <c>RequireHttps = false</c> пишеться ЛИШЕ з -AllowHttp і завжди з попередженням. Мутація
    /// (прогнано): прибрати попередження з гілки Http → <c>t.http.warnings</c> червоний.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("mode", "Http")]
    [InlineData("urls", "http://+:5000")]
    [InlineData("require", "false")]
    [InlineData("thumbSet", "False")]
    [InlineData("warnings", "1")]
    [InlineData("scheme", "http")]
    public void AllowHttp_пише_RequireHttps_false_із_попередженням(string field, string expected)
        => Assert.Equal(expected, DeployScriptHarness.Value(Results.Value, $"t.http.{field}"));

    /// <remarks>
    /// RequireHttps = false не з'являється в жодному режимі, крім Http (на всіх режимах, що пройшли
    /// перевірку). Мутація (прогнано): писати <c>false</c> в гілці Https → <c>t.https.require</c> червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void RequireHttps_false_лише_у_режимі_Http()
    {
        foreach (var scenario in new[] { "https", "redirect", "proxy" })
        {
            Assert.Equal("true", DeployScriptHarness.Value(Results.Value, $"t.{scenario}.require"));
        }

        Assert.Equal("false", DeployScriptHarness.Value(Results.Value, "t.http.require"));
    }

    /// <remarks>
    /// Мутація (прогнано): у <c>Get-HttpsCertificateProblem</c> прибрати перевірку <c>HasPrivateKey</c> →
    /// <c>c.noKey.code</c> червоний. Відмова формату — БЕЗ звернення до провайдера (<c>calls</c> = 0).
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("ok", "Ok", "False", "False", "1")]
    [InlineData("lower", "Ok", "False", "False", "1")]
    [InlineData("missing", "NotFound", "True", "False", "1")]
    [InlineData("noKey", "NoPrivateKey", "True", "False", "1")]
    [InlineData("expired", "Expired", "True", "False", "1")]
    [InlineData("notYet", "NotYetValid", "True", "False", "1")]
    [InlineData("soon", "Ok", "False", "True", "1")]
    [InlineData("short", "BadFormat", "True", "False", "0")]
    [InlineData("notHex", "BadFormat", "True", "False", "0")]
    [InlineData("tooLong", "BadFormat", "True", "False", "0")]
    public void Сертифікат_HTTPS_перевіряється_до_запису_конфігурації(
        string scenario, string code, string problem, string warning, string calls)
    {
        Assert.Equal(code, DeployScriptHarness.Value(Results.Value, $"c.{scenario}.code"));
        Assert.Equal(problem, DeployScriptHarness.Value(Results.Value, $"c.{scenario}.problem"));
        Assert.Equal(warning, DeployScriptHarness.Value(Results.Value, $"c.{scenario}.warning"));
        Assert.Equal(calls, DeployScriptHarness.Value(Results.Value, $"c.{scenario}.calls"));
    }

    /// <remarks>
    /// ⛔ Порядок у тілі скрипта: рішення про транспорт і перевірка сертифіката — на кроці 1, ДО msiexec
    /// (відмова не має лишати наполовину встановлену службу); запис Environment — на кроці 4. Відбиток у
    /// <c>Write-Host</c>/<c>Write-Warning</c> не друкується.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Транспорт_вирішується_до_MSI_а_відбиток_не_друкується()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));

        var decide = script.IndexOf("$transport = Resolve-TransportConfig", StringComparison.Ordinal);
        var certificate = script.IndexOf("Get-HttpsCertificateProblem -Thumbprint $HttpsThumbprint", StringComparison.Ordinal);
        var msi = script.IndexOf("Start-Process msiexec", StringComparison.Ordinal);
        var write = script.IndexOf("foreach ($name in $transport.Set.Keys)", StringComparison.Ordinal);

        Assert.True(decide > 0 && certificate > decide && msi > certificate, "транспорт і сертифікат мають вирішуватись до msiexec");
        Assert.True(write > msi, "Environment пишеться після msiexec (крок 4)");

        var printsThumbprint = script.Split('\n').Where(l =>
            l.Contains("$HttpsThumbprint", StringComparison.Ordinal)
            && (l.Contains("Write-Host", StringComparison.Ordinal) || l.Contains("Write-Warning", StringComparison.Ordinal)
                || l.Contains("throw", StringComparison.Ordinal)));
        Assert.Empty(printsThumbprint);
    }
}
