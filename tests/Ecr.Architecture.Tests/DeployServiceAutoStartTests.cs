using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// R6-X4/X4-04: зупинена служба з типом запуску <c>Automatic</c> піднімається після перезавантаження у вікні
/// обслуговування — стара версія на вже новій схемі. Тому крок 2 <c>deploy-ecr.ps1</c> після зупинки вимикає
/// автозапуск (лише служб, що були <c>Automatic</c>), крок 6 повертає його до старту, а runbook §8 велить те
/// саме на інших вузлах.
/// </summary>
/// <remarks>
/// Предмет — справжні функції скрипта (<see cref="DeployScriptHarness"/>); <c>Get-Service</c>/<c>Set-Service</c>
/// підмінено функціями сценарію — служб тут немає. Тести/мутація — CI, локально не запускались.
/// </remarks>
public sealed class DeployServiceAutoStartTests
{
    private const string Body = """
        $script:calls = [System.Collections.Generic.List[string]]::new()
        $script:state = @{}
        function Get-Service {
            [CmdletBinding()] param([string] $Name)
            if (-not $script:state.ContainsKey($Name)) { return $null }
            return [pscustomobject]@{ Name = $Name; StartType = $script:state[$Name] }
        }
        function Set-Service {
            [CmdletBinding()] param([string] $Name, [string] $StartupType)
            $script:calls.Add("set:${Name}:${StartupType}")
        }
        function Out-Disable([string] $key, [hashtable] $state, [switch] $WhatIf) {
            $script:calls.Clear(); $script:state = $state
            $r = @(Disable-EcrServicesAutoStart -WhatIf:$WhatIf)
            "disable.$key.result=$($r -join ',')"
            "disable.$key.calls=$($script:calls -join ';')"
        }
        function Out-Restore([string] $key, [hashtable] $state, [string[]] $names) {
            $script:calls.Clear(); $script:state = $state
            $r = @(Restore-EcrServicesAutoStart -Names $names)
            "restore.$key.result=$($r -join ',')"
            "restore.$key.calls=$($script:calls -join ';')"
        }
        Out-Disable 'both'    @{ EcrWorker = 'Automatic'; EcrApi = 'Automatic' }
        Out-Disable 'manual'  @{ EcrWorker = 'Manual'; EcrApi = 'Automatic' }
        Out-Disable 'already' @{ EcrWorker = 'Disabled'; EcrApi = 'Disabled' }
        Out-Disable 'none'    @{}
        Out-Disable 'whatIf'  @{ EcrWorker = 'Automatic'; EcrApi = 'Automatic' } -WhatIf
        Out-Restore 'msiDone' @{ EcrWorker = 'Automatic'; EcrApi = 'Automatic' } @('EcrWorker', 'EcrApi')
        Out-Restore 'still'   @{ EcrWorker = 'Disabled'; EcrApi = 'Disabled' } @('EcrWorker', 'EcrApi')
        Out-Restore 'notOurs' @{ EcrWorker = 'Disabled'; EcrApi = 'Disabled' } @('EcrApi')
        Out-Restore 'empty'   @{ EcrWorker = 'Disabled' } @()
        Out-Restore 'gone'    @{} @('EcrWorker')
        """;

    private static readonly Lazy<Dictionary<string, string>> Results =
        new(() => DeployScriptHarness.Run(["Disable-EcrServicesAutoStart", "Restore-EcrServicesAutoStart"], Body));

    /// <remarks>
    /// Мутації (CI): прибрати <c>Set-Service … Disabled</c> → червоні <c>both</c>/<c>manual</c>; вимикати й Manual →
    /// червоний <c>manual</c>; без <c>ShouldProcess</c> → червоний <c>whatIf</c>.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("both", "EcrWorker,EcrApi", "set:EcrWorker:Disabled;set:EcrApi:Disabled")]
    [InlineData("manual", "EcrApi", "set:EcrApi:Disabled")]
    [InlineData("already", "", "")]
    [InlineData("none", "", "")]
    [InlineData("whatIf", "", "")]
    public void Крок_2_вимикає_автозапуск_лише_служб_Automatic(string key, string result, string calls)
    {
        Assert.Equal(result, DeployScriptHarness.Value(Results.Value, $"disable.{key}.result"));
        Assert.Equal(calls, DeployScriptHarness.Value(Results.Value, $"disable.{key}.calls"));
    }

    /// <remarks>
    /// Мутації (CI): повертати й ті, що MSI вже повернув → червоний <c>msiDone</c>; повертати не лише вимкнені
    /// кроком 2 → червоний <c>notOurs</c>; не повертати → червоний <c>still</c>.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("msiDone", "", "")]
    [InlineData("still", "EcrWorker,EcrApi", "set:EcrWorker:Automatic;set:EcrApi:Automatic")]
    [InlineData("notOurs", "EcrApi", "set:EcrApi:Automatic")]
    [InlineData("empty", "", "")]
    [InlineData("gone", "", "")]
    public void Крок_6_повертає_автозапуск_вимкнений_кроком_2(string key, string result, string calls)
    {
        Assert.Equal(result, DeployScriptHarness.Value(Results.Value, $"restore.{key}.result"));
        Assert.Equal(calls, DeployScriptHarness.Value(Results.Value, $"restore.{key}.calls"));
    }

    /// <remarks>
    /// ⛔ Порядок у тексті скрипта: вимкнення — після зупинки служб і до першого <c>sqlcmd -i</c>; повернення —
    /// на кроці 6 до <c>Restart-Service</c>. Runbook §8 велить вимкнути автозапуск і на інших вузлах.
    /// Мутації: прибрати виклик або перенести повернення після старту → червоний; прибрати
    /// <c>Set-Service … Disabled</c> з runbook → червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Автозапуск_вимикається_до_схеми_і_повертається_до_старту()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));

        var stop = script.IndexOf("$stoppedForSchema = @(Stop-EcrServicesForSchema)", StringComparison.Ordinal);
        var disable = script.IndexOf("$disabledForSchema = @(Disable-EcrServicesAutoStart)", StringComparison.Ordinal);
        var firstFile = script.IndexOf("Invoke-DeploySql -TargetDb $Database -File", StringComparison.Ordinal);
        var step6 = script.IndexOf("Write-Step \"Крок 6/7: старт служби\"", StringComparison.Ordinal);
        var restore = script.IndexOf("Restore-EcrServicesAutoStart -Names $disabledForSchema", StringComparison.Ordinal);
        var restart = script.IndexOf("Restart-Service -Name EcrApi -Force", StringComparison.Ordinal);

        Assert.True(stop > 0 && disable > stop && firstFile > disable, "автозапуск не вимикається між зупинкою служб і схемою");
        Assert.True(step6 > firstFile && restore > step6 && restart > restore, "автозапуск не повертається на кроці 6 до старту");

        var runbook = File.ReadAllText(Path.Combine(SourceTree.Root, "docs", "admin", "operations-runbook.md"));
        Assert.Contains("Set-Service EcrApi -StartupType Disabled", runbook, StringComparison.Ordinal);
        Assert.Contains("Set-Service EcrWorker -StartupType Disabled", runbook, StringComparison.Ordinal);
    }
}
