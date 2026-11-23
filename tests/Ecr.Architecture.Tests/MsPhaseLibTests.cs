using System.Diagnostics;
using System.Text;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// ✎ D-571 (P-1123-4): <c>tools/Ecr.MethodologyScriptMigration/window/MsPhase.Lib.ps1</c> — чиста логіка фази W15z: які накладки
/// одиниць/мосту ще потрібні, коли шаблон Land 1.0.20.0 уже їх несе, і хто перемикається на Script за яким доказом. СИНТЕТИЧНІ дані.
/// </summary>
/// <remarks>
/// Мутаційні докази (лише цей клас): прибрати <c>ECW_C09_02_01</c> з <c>$MsPermanentExclusions</c> — червоніє
/// <c>Вода_не_перемикається_навіть_із_доказом</c>; у <c>Get-MsSwitchPlan</c> не звіряти хеш live — червоніє
/// <c>Змінений_після_паритету_текст_не_перемикається</c>; у <c>Get-MsUnitsStillNeeded</c> вважати аргумент виконаним без звірки
/// одиниці — червоніє <c>Одиниця_аргументу_пропускається_лише_коли_шаблон_має_саме_її</c>.
/// </remarks>
public sealed class MsPhaseLibTests
{
    private static string Lib => Path.Combine(SourceTree.Root, "tools", "Ecr.MethodologyScriptMigration", "window", "MsPhase.Lib.ps1");

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

        var script = "$ErrorActionPreference = 'Stop'\nSet-StrictMode -Version 2.0\n. '" + Lib.Replace("'", "''", StringComparison.Ordinal) + "'\n" + body;
        var file = Path.Combine(Path.GetTempPath(), $"ecr-ms-phase-{Guid.NewGuid():N}.ps1");
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

    private const string Units = """
        $u = '{"formatVersion":1,"methodologies":{"M_A":{"arguments":{"Rho":{"unit":"kg_per_Sm3"},"Heat":{"unit":"MJ_per_Sm3"}}},"M_B":{"constants":{"Q_B":{"unit":"MJ_per_kg"}}},"M_C":{"argumentSources":{"Use":{"alternative":"UseSm3"}}}}}' | ConvertFrom-Json
        """;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Одиниця_аргументу_пропускається_лише_коли_шаблон_має_саме_її()
    {
        var (exit, output) = RunPowerShell(Units + """

            $r = Get-MsUnitsStillNeeded -Units $u -Facts @{
                ColumnUnits = @{ 'M_A|Rho' = @('kg_per_Sm3'); 'M_A|Heat' = @('MJ_per_Sm3', 'one') }
                ArgumentColumns = @{}; ConstantUnits = @{} }
            'SKIP=' + ($r.Skipped -join ';')
            'KEEP=' + ($r.Units | ConvertTo-Json -Depth 6 -Compress)
            """);

        Assert.True(exit == 0, output);
        Assert.Contains("SKIP=arguments M_A|Rho", output, StringComparison.Ordinal);
        Assert.Contains("\"Heat\"", output, StringComparison.Ordinal);       // одна з колонок без одиниці — накладка лишається
        Assert.DoesNotContain("\"Rho\"", output, StringComparison.Ordinal);
        Assert.Contains("\"Q_B\"", output, StringComparison.Ordinal);        // константа без одиниці в БД — лишається
        Assert.Contains("\"Use\"", output, StringComparison.Ordinal);        // мосту немає — підміна лишається
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Шаблон_1_0_20_з_одиницями_й_мостом_і_константа_з_одиницею_знімають_усі_накладки()
    {
        var (exit, output) = RunPowerShell(Units + """

            $r = Get-MsUnitsStillNeeded -Units $u -Facts @{
                ColumnUnits = @{ 'M_A|Rho' = @('kg_per_Sm3'); 'M_A|Heat' = @('MJ_per_Sm3') }
                ArgumentColumns = @{ 'M_C|Use' = $true }; ConstantUnits = @{ 'M_B|Q_B' = 'MJ_per_kg' } }
            'SKIPPED=' + $r.Skipped.Count
            'LEFT=' + @($r.Units.methodologies.PSObject.Properties).Count
            """);

        Assert.True(exit == 0, output);
        Assert.Contains("SKIPPED=4", output, StringComparison.Ordinal);
        Assert.Contains("LEFT=0", output, StringComparison.Ordinal);
    }

    private const string Candidates = """
        $c = @(
            @{ VersionId = 1; Code = 'M_LIVE'; Version = 'V1'; DbHash = 'h1'; GoldenExpectations = 'ok'; Engine = 0 },
            @{ VersionId = 2; Code = 'M_REF'; Version = 'V1'; DbHash = 'h2'; GoldenExpectations = 'ok'; Engine = 0 },
            @{ VersionId = 3; Code = 'M_GOLD'; Version = 'V1'; DbHash = 'h3'; GoldenExpectations = 'ok'; Engine = 0 },
            @{ VersionId = 4; Code = 'M_EMPTY'; Version = 'V1'; DbHash = 'h4'; GoldenExpectations = 'empty'; Engine = 0 },
            @{ VersionId = 5; Code = 'ECW_C09_02_01'; Version = 'V1'; DbHash = 'h5'; GoldenExpectations = 'ok'; Engine = 0 },
            @{ VersionId = 6; Code = 'M_CHANGED'; Version = 'V1'; DbHash = 'NEW'; GoldenExpectations = 'ok'; Engine = 0 },
            @{ VersionId = 7; Code = 'M_DONE'; Version = 'V1'; DbHash = 'h7'; GoldenExpectations = 'ok'; Engine = 1 },
            @{ VersionId = 8; Code = 'M_BAD'; Version = 'V1'; DbHash = 'h8'; GoldenExpectations = 'ok'; Engine = 0 },
            @{ VersionId = 9; Code = 'M_NONE'; Version = 'V1'; DbHash = 'h9'; GoldenExpectations = 'none'; Engine = 0 },
            @{ VersionId = 10; Code = 'M_NODOC'; Version = 'V1'; DbHash = 'h10'; GoldenExpectations = 'invalid'; Engine = 0 })
        $p = Get-MsSwitchPlan -Candidates $c -Live @{ 1 = 'h1'; 5 = 'h5'; 6 = 'old'; 10 = 'h10' } -Reference @{ 2 = 'h2'; 4 = 'h4' } -Mismatched @(8)
        'LIVE=' + ($p.Live -join ',')
        'REF=' + ($p.Reference -join ',')
        'GOLD=' + ($p.Golden -join ',')
        'ALREADY=' + ($p.Already -join ',')
        foreach ($s in $p.Stay) { 'STAY ' + $s.VersionId + ' ' + $s.Reason }
        """;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void План_перемикання_групує_за_джерелом_доказу()
    {
        var (exit, output) = RunPowerShell(Candidates);

        Assert.True(exit == 0, output);
        Assert.Matches(@"LIVE=1\s", output);
        Assert.Contains("REF=2", output, StringComparison.Ordinal);
        Assert.Contains("GOLD=3", output, StringComparison.Ordinal);
        Assert.Contains("ALREADY=7", output, StringComparison.Ordinal);
        Assert.Contains("STAY 4 golden set without expectations", output, StringComparison.Ordinal);
        Assert.Contains("STAY 8 parity mismatch", output, StringComparison.Ordinal);
        Assert.Contains("STAY 9 no golden set", output, StringComparison.Ordinal);
        Assert.Contains("STAY 10 golden set refers to a document", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Вода_не_перемикається_навіть_із_доказом()
    {
        var (exit, output) = RunPowerShell(Candidates);

        Assert.True(exit == 0, output);
        Assert.Contains("STAY 5 permanent exclusion", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Змінений_після_паритету_текст_не_перемикається()
    {
        var (exit, output) = RunPowerShell(Candidates);

        Assert.True(exit == 0, output);
        Assert.Contains("STAY 6 script changed after parity", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Правило_старої_системи_відбирає_рядок_за_всіма_умовами_а_зірка_всю_таблицю()
    {
        var (exit, output) = RunPowerShell("""
            $two = ConvertTo-MsLegacyRule -Methodology 'M_X' -Table 'AF_T' -Parameters 'Inv;Nae;' -Values 'A-1;0640;'
            $all = ConvertTo-MsLegacyRule -Methodology 'M_Y' -Table 'AF_T' -Parameters '*' -Values '*'
            $one = ConvertTo-MsLegacyRule -Methodology 'M_Z' -Table 'AF_T' -Parameters 'Nae;' -Values '0646;'
            'BOTH=' + (Test-MsLegacyRule -Rule $two -Row @{ Inv = 'A-1'; Nae = '0640' })
            'HALF=' + (Test-MsLegacyRule -Rule $two -Row @{ Inv = 'A-2'; Nae = '0640' })
            'MISSING=' + (Test-MsLegacyRule -Rule $one -Row @{ Inv = 'A-1' })
            'ALL=' + (Test-MsLegacyRule -Rule $all -Row @{ Other = 1 })
            'ONE=' + (Test-MsLegacyRule -Rule $one -Row @{ Nae = '0646' })
            """);

        Assert.True(exit == 0, output);
        Assert.Contains("BOTH=True", output, StringComparison.Ordinal);
        Assert.Contains("HALF=False", output, StringComparison.Ordinal);
        Assert.Contains("MISSING=False", output, StringComparison.Ordinal);
        Assert.Contains("ALL=True", output, StringComparison.Ordinal);
        Assert.Contains("ONE=True", output, StringComparison.Ordinal);
    }
}