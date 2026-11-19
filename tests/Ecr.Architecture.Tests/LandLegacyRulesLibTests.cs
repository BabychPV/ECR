using System.Diagnostics;
using System.Text;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// <c>tools/land/LandLegacyRules.Lib.ps1</c> (D-556, коло 4): правило «уся таблиця» отримує таблицю-обмеження (колонка статусу
/// ЦІЄЇ таблиці = Running), а не `{}`. СИНТЕТИЧНІ таблиці й правила (вигадані коди), без мережі й БД.
/// </summary>
/// <remarks>
/// Мутаційні докази (лише цей клас): у <c>Convert-LandLegacyRule</c> прибрати блок <c>$scopedCatchAll</c> (завжди `{}`) — червоніють
/// <c>Уся_таблиця_отримує_колонку_статусу_своєї_таблиці</c>, <c>Колонка_статусу_Status_теж_підходить</c> і
/// <c>План_ставить_уся_таблиця_після_умовних_правил_і_не_зливає_таблиці</c>; прибрати умову <c>-and $null -ne $StatusEntryId</c> —
/// червоніє <c>Без_фільтра_Running_або_без_колонки_статусу_лишається_порожній_предикат</c>; у плані замінити пріоритет
/// скопованих (<c>($k + $n) * 10</c>) на <c>$k * 10</c> — червоніє <c>План_ставить_уся_таблиця_після_умовних_правил_і_не_зливає_таблиці</c>.
/// </remarks>
public sealed class LandLegacyRulesLibTests
{
    private static string Lib => Path.Combine(SourceTree.Root, "tools", "land", "LandLegacyRules.Lib.ps1");

    private static (int Exit, string Output) RunPowerShell(string body)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var script = "$ErrorActionPreference = 'Stop'\n. '" + Lib.Replace("'", "''", StringComparison.Ordinal) + "'\n" + body;
        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-rules-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell не запустився");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(120_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("PowerShell не завершився за 120 с.");
            }

            return (process.ExitCode, stdout + stderr);
        }
        finally
        {
            File.Delete(file);
        }
    }

    // Вигадані таблиці: TA (колонка статусу Land_Status), TB (колонка статусу Status), TC (без колонки статусу).
    private const string Fixture = """
        $tables = @{
            'TA' = [pscustomobject]@{ code = 'TA'; columns = @(
                [pscustomobject]@{ code = 'Land_Status'; dataType = 'Lookup'; registry = 'R_STATUS' },
                [pscustomobject]@{ code = 'Kind'; dataType = 'String'; registry = $null }) }
            'TB' = [pscustomobject]@{ code = 'TB'; columns = @(
                [pscustomobject]@{ code = 'Status'; dataType = 'Lookup'; registry = 'R_STATUS' },
                [pscustomobject]@{ code = 'Kind'; dataType = 'String'; registry = $null }) }
            'TC' = [pscustomobject]@{ code = 'TC'; columns = @([pscustomobject]@{ code = 'Kind'; dataType = 'String'; registry = $null }) }
        }
        $tmap = @{ 'AF_A' = 'TA'; 'AF_B' = 'TB'; 'AF_C' = 'TC' }
        $regs = @{ 'R_STATUS' = [pscustomobject]@{ entries = @() } }
        $ids = @{ 'TA|Land_Status' = 101; 'TA|Kind' = 102; 'TB|Status' = 201; 'TB|Kind' = 202; 'TC|Kind' = 302 }
        $colId = { param($t, $c) $ids["$t|$c"] }
        $entryId = { param($r, $e) 1 }
        function Rule($code, $table, $conds, $catchAll) { [pscustomobject]@{ code = $code; table = $table; conditions = @($conds); catchAll = $catchAll; isAvailable = $true } }
        function Conv($rule, $status) { Convert-LandLegacyRule -Rule $rule -TableMap $tmap -Tables $tables -Registries $regs -ColumnId $colId -EntryId $entryId -StatusEntryId $status }
        $cond = [pscustomobject]@{ parameter = 'Kind'; value = 'x' }
        """;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Уся_таблиця_отримує_колонку_статусу_своєї_таблиці()
    {
        var (exit, output) = RunPowerShell(Fixture + """

            $r = Conv (Rule 'R1' 'AF_A' @() $true) 777
            "$($r.MatchJson)|$($r.CatchAll)|$($r.TableScopedCatchAll)"
            """);
        Assert.Equal(0, exit);
        Assert.Contains("{\"101\":\"777\"}|False|True", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Колонка_статусу_Status_теж_підходить()
    {
        var (exit, output) = RunPowerShell(Fixture + """

            $r = Conv (Rule 'R1' 'AF_B' @() $true) 777
            "$($r.MatchJson)|$($r.TableScopedCatchAll)"
            """);
        Assert.Equal(0, exit);
        Assert.Contains("{\"201\":\"777\"}|True", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Без_фільтра_Running_або_без_колонки_статусу_лишається_порожній_предикат()
    {
        var (exit, output) = RunPowerShell(Fixture + """

            $a = Conv (Rule 'R1' 'AF_A' @() $true) $null
            $b = Conv (Rule 'R2' 'AF_C' @() $true) 777
            "A=$($a.MatchJson)|$($a.CatchAll)|$($a.TableScopedCatchAll);B=$($b.MatchJson)|$($b.CatchAll)|$($b.TableScopedCatchAll)"
            """);
        Assert.Equal(0, exit);
        Assert.Contains("A={}|True|False;B={}|True|False", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void План_ставить_уся_таблиця_після_умовних_правил_і_не_зливає_таблиці()
    {
        var (exit, output) = RunPowerShell(Fixture + """

            $rC1 = Rule 'C1' 'AF_A' @() $true
            $rK1 = Rule 'K1' 'AF_C' @($cond) $false
            $rC2 = Rule 'C2' 'AF_B' @() $true
            $rU1 = Rule 'U1' 'AF_C' @() $true
            $conv = @(
                [pscustomobject]@{ Rule = $rC1; Result = (Conv $rC1 777) },
                [pscustomobject]@{ Rule = $rK1; Result = (Conv $rK1 777) },
                [pscustomobject]@{ Rule = $rC2; Result = (Conv $rC2 777) },
                [pscustomobject]@{ Rule = $rU1; Result = (Conv $rU1 777) })
            $plan = Get-LandLegacyRulePlan -Converted $conv
            ($plan.Rules | Sort-Object Priority | ForEach-Object { "$($_.Code)=$($_.Priority):$($_.MatchJson)" }) -join ';'
            "merged=$($plan.Merged)"
            """);
        Assert.Equal(0, exit);

        // Умовне K1 (позиція 2) - 20; «уся таблиця» TA і TB (не злиті) - після умовних; `{}` (TC без статусу) - останнє.
        Assert.Contains("K1=20:{\"302\":\"x\"};C1=50:{\"101\":\"777\"};C2=60:{\"201\":\"777\"};U1=70:{}", output, StringComparison.Ordinal);
        Assert.Contains("merged=0", output, StringComparison.Ordinal);
    }
}
