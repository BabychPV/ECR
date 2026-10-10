using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// S2-04 (аудит 2026-10-09b, HU-13 Q3): <c>deploy-ecr.ps1</c> змінює схему лише після свіжої копії
/// бази (<c>-SkipBackupCheck</c> — для автоматизації) і без живого застосунку — EcrWorker, потім EcrApi
/// зупиняються (з обмеженим очікуванням) до першого <c>sqlcmd</c> зі зміною схеми.
/// </summary>
/// <remarks>
/// Предмет — справжні функції скрипта (вирізаються парсером, <see cref="DeployScriptHarness"/>);
/// <c>Get-Service</c>/<c>Stop-Service</c> підмінено функціями сценарію — служб і SQL тут немає.
/// Тести/мутація — CI, локально не запускались.
/// </remarks>
public sealed class DeploySchemaWindowTests
{
    private const string Body = """
        $script:calls = [System.Collections.Generic.List[string]]::new()
        $script:state = @{}
        $script:hang = ''
        function Get-Service {
            [CmdletBinding()] param([string] $Name)
            if (-not $script:state.ContainsKey($Name)) { return $null }
            $svc = [pscustomobject]@{ Name = $Name; Status = $script:state[$Name] }
            $svc | Add-Member -MemberType ScriptMethod -Name WaitForStatus -Value {
                param($status, $timeout)
                $script:calls.Add("wait:$($this.Name):${status}:$($timeout.TotalSeconds)")
                if ($script:hang -eq $this.Name) { throw 'timeout' }
            }
            return $svc
        }
        function Stop-Service {
            [CmdletBinding()] param([string] $Name, [switch] $Force, [switch] $NoWait)
            $script:calls.Add("stop:${Name}:force-${Force}:nowait-${NoWait}")
        }
        function Out-Stop([string] $key, [hashtable] $state, [string] $hang = '', [switch] $WhatIf) {
            $script:calls.Clear(); $script:state = $state; $script:hang = $hang
            try {
                $r = @(Stop-EcrServicesForSchema -TimeoutSeconds 7 -WhatIf:$WhatIf)
                "stop.$key.result=$($r -join ',')"
            }
            catch {
                $who = if ($_.Exception.Message -match 'EcrApi') { 'EcrApi' } elseif ($_.Exception.Message -match 'EcrWorker') { 'EcrWorker' } else { '?' }
                "stop.$key.result=threw:$who"
            }
            "stop.$key.calls=$($script:calls -join ';')"
        }
        Out-Stop 'both'    @{ EcrWorker = 'Running'; EcrApi = 'Running' }
        Out-Stop 'apiOnly' @{ EcrApi = 'Running' }
        Out-Stop 'stopped' @{ EcrWorker = 'Stopped'; EcrApi = 'Stopped' }
        Out-Stop 'none'    @{}
        Out-Stop 'hang'    @{ EcrWorker = 'Running'; EcrApi = 'Running' } 'EcrWorker'
        Out-Stop 'whatIf'  @{ EcrWorker = 'Running'; EcrApi = 'Running' } '' -WhatIf

        function Out-Backup([string] $key, [string] $age, [int] $max) {
            $p = Get-SchemaBackupProblem -AgeMinutes $age -MaxAgeHours $max -Database 'Ecr'
            "backup.$key.ok=$($null -eq $p)"
            "backup.$key.hint=$([bool] ($p -and $p.Contains('-SkipBackupCheck')))"
        }
        Out-Backup 'fresh'    '30'   24
        Out-Backup 'edge'     '1440' 24
        Out-Backup 'stale'    '1441' 24
        Out-Backup 'none'     'none' 24
        Out-Backup 'empty'    ''     24
        Out-Backup 'garbage'  'abc'  24
        Out-Backup 'custom'   '3000' 72
        Out-Backup 'skew'     '-5'   24
        """;

    private static readonly Lazy<Dictionary<string, string>> Results =
        new(() => DeployScriptHarness.Run(["Stop-EcrServicesForSchema", "Get-SchemaBackupProblem"], Body));

    /// <remarks>
    /// Мутації (CI): прибрати <c>Stop-Service</c> → <c>both</c>/<c>apiOnly</c> червоні; поміняти порядок
    /// служб → <c>both</c> червоний; прибрати <c>WaitForStatus</c> → червоні виклики <c>wait:</c>; ковтати
    /// виняток очікування → <c>hang</c> червоний; без <c>ShouldProcess</c> → <c>whatIf</c> червоний.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("both", "EcrWorker,EcrApi",
        "stop:EcrWorker:force-True:nowait-True;wait:EcrWorker:Stopped:7;stop:EcrApi:force-True:nowait-True;wait:EcrApi:Stopped:7")]
    [InlineData("apiOnly", "EcrApi", "stop:EcrApi:force-True:nowait-True;wait:EcrApi:Stopped:7")]
    [InlineData("stopped", "", "")]
    [InlineData("none", "", "")]
    [InlineData("hang", "threw:EcrWorker", "stop:EcrWorker:force-True:nowait-True;wait:EcrWorker:Stopped:7")]
    [InlineData("whatIf", "", "")]
    public void Служби_зупиняються_воркер_першим_з_очікуванням_до_зміни_схеми(string key, string result, string calls)
    {
        Assert.Equal(result, DeployScriptHarness.Value(Results.Value, $"stop.{key}.result"));
        Assert.Equal(calls, DeployScriptHarness.Value(Results.Value, $"stop.{key}.calls"));
    }

    /// <remarks>
    /// Мутації (CI): прийняти відсутню копію (<c>none</c>) чи порівнювати з годинами замість хвилин →
    /// червоні <c>none</c>/<c>stale</c>/<c>custom</c>; прибрати підказку <c>-SkipBackupCheck</c> → червоні <c>hint</c>.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("fresh", "True", "False")]
    [InlineData("edge", "True", "False")]
    [InlineData("stale", "False", "True")]
    [InlineData("none", "False", "True")]
    [InlineData("empty", "False", "True")]
    [InlineData("garbage", "False", "True")]
    [InlineData("custom", "True", "False")]
    [InlineData("skew", "True", "False")]
    public void Схема_змінюється_лише_після_свіжої_копії_бази(string key, string ok, string hint)
    {
        Assert.Equal(ok, DeployScriptHarness.Value(Results.Value, $"backup.{key}.ok"));
        Assert.Equal(hint, DeployScriptHarness.Value(Results.Value, $"backup.{key}.hint"));
    }

    /// <remarks>
    /// ⛔ Порядок у тексті скрипта: «Крок 2/7» → перевірка копії (з обходом лише через
    /// <c>-FirstDeployment</c>/<c>-SkipBackupCheck</c>) → зупинка служб → перший <c>sqlcmd -i</c> → перезапуск
    /// на кроці 6. Мутація: перенести зупинку після першого <c>Invoke-DeploySql -File</c> → червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Копія_і_зупинка_служб_стоять_перед_першим_скриптом_схеми()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));

        var skipped = script.IndexOf("Write-Step \"Крок 2/7: схема — ПРОПУЩЕНО", StringComparison.Ordinal);
        var step2 = script.IndexOf("Write-Step \"Крок 2/7: схема ($Database", StringComparison.Ordinal);
        var skipCheck = script.IndexOf("elseif ($SkipBackupCheck)", StringComparison.Ordinal);
        var query = script.IndexOf("FROM msdb.dbo.backupset WHERE database_name", StringComparison.Ordinal);
        var verdict = script.IndexOf("Get-SchemaBackupProblem -AgeMinutes", StringComparison.Ordinal);
        var stop = script.IndexOf("$stoppedForSchema = @(Stop-EcrServicesForSchema)", StringComparison.Ordinal);
        var firstFile = script.IndexOf("Invoke-DeploySql -TargetDb $Database -File", StringComparison.Ordinal);
        var restart = script.IndexOf("Restart-Service -Name EcrApi -Force", StringComparison.Ordinal);

        Assert.True(skipped > 0 && step2 > skipped, "гілки -SkipSchema / кроку 2 не знайдено");
        Assert.True(skipCheck > step2, "обходу -SkipBackupCheck у кроці 2 немає");
        Assert.True(query > skipCheck && verdict > query, "перевірки копії через msdb.dbo.backupset немає після обходу");
        Assert.True(stop > verdict, "служби зупиняються раніше, ніж перевірено копію (простій без копії)");
        Assert.True(firstFile > stop, "перший скрипт схеми виконується до зупинки служб");
        Assert.True(restart > firstFile, "служби не піднімаються після зміни схеми");

        var queryLine = script[query..script.IndexOf('\n', query)];
        Assert.Contains("type IN ('D', 'I')", queryLine, StringComparison.Ordinal);
    }
}
