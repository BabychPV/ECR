using Ecr.Setup;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// R9-F5/F5-02 (аудит 2026-10-10): MSI порт не пам'ятає, а крок 4 <c>deploy-ecr.ps1</c> перезаписує
/// <c>ASPNETCORE_URLS</c> з <c>-AppPort</c> (типово 5000). Оновлення служби на 443 без <c>-AppPort</c> (runbook
/// §10.3) чи майстром із полем на 5000 переносило її на 5000, а крок 7 опитував уже новий порт і казав «Готово».
/// Тепер скрипт без явного <c>-AppPort</c> відмовляє на кроці 1, а майстер бере поточний порт служби як типовий.
/// </summary>
/// <remarks>Тести/мутація — CI, локально не запускались.</remarks>
public sealed class DeployAppPortGuardTests
{
    private const string Body = """
        function Out-Port([string] $key, [string[]] $envs, [int] $port, [bool] $bound) {
            $p = Get-AppPortChangeProblem -CurrentEnvironment $envs -AppPort $port -AppPortBound $bound
            "port.$key.ok=$($null -eq $p)"
        }
        Out-Port 'differs'    @('X=1', 'ASPNETCORE_URLS=https://+:443') 5000 $false
        Out-Port 'redirect'   @('ASPNETCORE_URLS=https://+:443;http://+:80') 5000 $false
        Out-Port 'same'       @('ASPNETCORE_URLS=https://+:5000') 5000 $false
        Out-Port 'caseName'   @('aspnetcore_urls=http://+:5000') 5000 $false
        Out-Port 'explicit'   @('ASPNETCORE_URLS=https://+:443') 5000 $true
        Out-Port 'noService'  @() 5000 $false
        Out-Port 'noUrls'     @('ECR_ConnectionStrings__Ecr=x') 5000 $false
        Out-Port 'garbage'    @('ASPNETCORE_URLS=https://+') 5000 $false
        "url.ipv6=$(Get-UrlsPrimaryPort -Urls 'http://[::]:8080/')"
        "url.none=$($null -eq (Get-UrlsPrimaryPort -Urls ''))"
        """;

    private static readonly Lazy<Dictionary<string, string>> Results =
        new(() => DeployScriptHarness.Run(["Get-UrlsPrimaryPort", "Get-AppPortChangeProblem"], Body));

    /// <remarks>
    /// Мутації (CI): пропускати будь-яку зміну порту → червоні <c>differs</c>/<c>redirect</c>; вважати
    /// нерозпізнану адресу «тим самим портом» → червоний <c>garbage</c>; відмовляти при явному
    /// <c>-AppPort</c> чи без служби → червоні <c>explicit</c>/<c>noService</c>.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("differs", "False")]
    [InlineData("redirect", "False")]
    [InlineData("same", "True")]
    [InlineData("caseName", "True")]
    [InlineData("explicit", "True")]
    [InlineData("noService", "True")]
    [InlineData("noUrls", "True")]
    [InlineData("garbage", "False")]
    public void Оновлення_без_AppPort_не_змінює_порт_служби(string key, string ok)
    {
        Assert.Equal(ok, DeployScriptHarness.Value(Results.Value, $"port.{key}.ok"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Порт_першої_адреси_розпізнається()
    {
        Assert.Equal("8080", DeployScriptHarness.Value(Results.Value, "url.ipv6"));
        Assert.Equal("True", DeployScriptHarness.Value(Results.Value, "url.none"));
    }

    /// <remarks>Мутація: перенести перевірку після кроку 2 / msiexec або прибрати → червоний.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Перевірка_порту_стоїть_на_кроці_1_і_враховує_явний_параметр()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));

        var step1 = script.IndexOf("Write-Step \"Крок 1/7: передумови\"", StringComparison.Ordinal);
        var guard = script.IndexOf("-AppPortBound $PSBoundParameters.ContainsKey('AppPort')", StringComparison.Ordinal);
        var step2 = script.IndexOf("Write-Step \"Крок 2/7", StringComparison.Ordinal);
        Assert.True(step1 > 0 && guard > step1 && step2 > guard, "перевірка порту має стояти на кроці 1, до схеми й MSI");
        Assert.Contains("if ($portProblem) { throw $portProblem }", script, StringComparison.Ordinal);
    }

    /// <remarks>Мутація: повернути типові 5000 у полі порту майстра без читання служби → червоний.</remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData(new[] { "X=1", "ASPNETCORE_URLS=https://+:443" }, 443)]
    [InlineData(new[] { "ASPNETCORE_URLS=https://+:443;http://+:80" }, 443)]
    [InlineData(new[] { "aspnetcore_urls=http://localhost:8080/" }, 8080)]
    [InlineData(new[] { "ASPNETCORE_URLS=http://[::]:5001" }, 5001)]
    [InlineData(new[] { "ASPNETCORE_URLS=https://+" }, null)]
    [InlineData(new[] { "ASPNETCORE_URLS=https://+:70000" }, null)]
    [InlineData(new[] { "ECR_X=1" }, null)]
    [InlineData(null, null)]
    public void Майстер_бере_поточний_порт_служби(string[]? environment, int? expected)
    {
        Assert.Equal(expected, WizardState.PortFromServiceEnvironment(environment));

        var step = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "Ecr.Setup", "Steps", "AccountAndNetworkStep.cs"));
        Assert.Contains("WizardState.PortFromServiceEnvironment(DeployRunner.ReadApiServiceEnvironment())", step, StringComparison.Ordinal);
    }

    /// <remarks>Мутація: прибрати <c>-AppPort</c> з команди runbook §10.3 → червоний.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Команда_повтору_після_MSI_в_runbook_передає_порт()
    {
        var runbook = File.ReadAllText(Path.Combine(SourceTree.Root, "docs", "admin", "operations-runbook.md"));
        var start = runbook.IndexOf("**після кожного\r\nоновлення MSI повторюйте**", StringComparison.Ordinal);
        if (start < 0)
        {
            start = runbook.IndexOf("**після кожного\nоновлення MSI повторюйте**", StringComparison.Ordinal);
        }

        Assert.True(start > 0, "немає абзацу runbook §10.3 «після кожного оновлення MSI повторюйте»");
        var open = runbook.IndexOf("```powershell", start, StringComparison.Ordinal);
        var close = runbook.IndexOf("```", open + 3, StringComparison.Ordinal);
        var block = runbook[open..close];
        Assert.Contains("deploy-ecr.ps1", block, StringComparison.Ordinal);
        Assert.Contains("-AppPort", block, StringComparison.Ordinal);
        Assert.Contains("-ServiceAccount", block, StringComparison.Ordinal);
    }
}
