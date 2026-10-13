using System.Diagnostics;
using System.Text;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// LP-07: <c>tools/land/LandParityRuleArgs.Lib.ps1</c> — чиста логіка етапу LandParity в Build-LandTemplate:
/// аргументи, які читає правило категорії (без них simulate дає <c>categoryRuleFailed</c> «#ARG»), і склад речовин
/// версії з суфіксів категорій констант. Лише ключі й синтетичні рядки; значень констант тут немає.
/// </summary>
public sealed class LandParityRuleArgsTests
{
    private const string RulesFile = "docs/delivery/reference-data/land-template/af-land-category-rules.json";

    private static string LandFile(string name) => Path.Combine(SourceTree.Root, "tools", "land", name);

    private static (int Exit, string Output) RunPowerShell(string script, params string[] args)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-parity-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file }
                         .Concat(args))
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

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Склад_речовин_береться_з_суфіксів_категорій_3_4_цифри_без_повторів()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            $cats = @('6783(002)_sludge_333', '6783(002)_sludge_2754', '6782(002)_sludge_333', '6646_FG', 'FuelGas_1702',
                      '6780(001)_14_1052', 'B4-120-TA-101_1001', 'Common', '', 'Loc_BeforeMR_A', 'X_12', 'X_12345', 'oil_2735')
            'CODES=' + ((Get-LandSubstanceCodes -Categories $cats) -join ',')
            'EMPTY=' + (Get-LandSubstanceCodes -Categories @()).Count
            'NAE=' + ((Get-LandNaeCandidates -Categories $cats) -join '|')
            """;

        var (exit, output) = RunPowerShell(script, LandFile("LandParityRuleArgs.Lib.ps1"));

        Assert.True(exit == 0, output);
        Assert.Contains("CODES=333,1001,1052,1702,2735,2754", output, StringComparison.Ordinal);
        Assert.Contains("EMPTY=0", output, StringComparison.Ordinal);
        Assert.Contains("NAE=6783(002)|6782(002)|6646|6780(001)", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Аргументи_правила_літерал_in_кандидат_NAE_і_злиття_без_перезапису()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            $e = "if(in(@Land_TypeFuelOrStream,'LPG (HMB)','LPG (Internal)'),@Land_NAE+'_LPG',if(in(@Land_TypeFuelOrStream,'Diesel'),@Land_NAE+'_Diesel',''))"
            'NAMES=' + ((Get-LandRuleArgumentNames -Expression $e) -join ',')
            'LITS=' + ((Get-LandRuleArgumentLiterals -Expression $e -Name 'Land_TypeFuelOrStream') -join '|')
            'NOLITS=' + (Get-LandRuleArgumentLiterals -Expression $e -Name 'Land_NAE').Count
            $s = Get-LandRuleSampleArguments -Expression $e -NaeCandidate '6646'
            'SAMPLE=' + (($s.Keys | Sort-Object | ForEach-Object { "$_=$($s[$_])" }) -join ';')
            $g = "if(in(@Land_NAE,'0801','0926'),'A','')"
            $s2 = Get-LandRuleSampleArguments -Expression $g -NaeCandidate '6646'
            'GROUP=' + $s2['Land_NAE']
            $p = Get-LandRuleSampleArguments -Expression '@Land_NAE' -NaeCandidate '6482(003)'
            'PASS=' + $p['Land_NAE']
            $existing = @([pscustomobject]@{ argumentCode = 'Land_NAE'; value = $null; valueString = '9999'; unitId = $null })
            $m = Merge-LandParityArguments -Existing $existing -Needed $s
            'ADDED=' + ($m.Added -join ',')
            'NAEKEPT=' + (@($m.Arguments | Where-Object { $_.argumentCode -eq 'Land_NAE' } | ForEach-Object { $_.valueString }) -join ',')
            'COUNT=' + @($m.Arguments).Count
            'FROMEMPTY=' + (Merge-LandParityArguments -Existing @() -Needed $s).Added.Count
            """;

        var (exit, output) = RunPowerShell(script, LandFile("LandParityRuleArgs.Lib.ps1"));

        Assert.True(exit == 0, output);
        Assert.Contains("NAMES=Land_TypeFuelOrStream,Land_NAE", output, StringComparison.Ordinal);
        Assert.Contains("LITS=LPG (HMB)|LPG (Internal)|Diesel", output, StringComparison.Ordinal);
        Assert.Contains("NOLITS=0", output, StringComparison.Ordinal);
        Assert.Contains("SAMPLE=Land_NAE=6646;Land_TypeFuelOrStream=LPG (HMB)", output, StringComparison.Ordinal);
        Assert.Contains("GROUP=0801", output, StringComparison.Ordinal);
        Assert.Contains("PASS=6482(003)", output, StringComparison.Ordinal);
        Assert.Contains("ADDED=Land_TypeFuelOrStream", output, StringComparison.Ordinal);
        Assert.Contains("NAEKEPT=9999", output, StringComparison.Ordinal);
        Assert.Contains("COUNT=2", output, StringComparison.Ordinal);
        Assert.Contains("FROMEMPTY=2", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void П_ять_правил_файлу_дають_аргументи_які_читає_правило()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            $rules = (Get-Content -LiteralPath $args[1] -Raw -Encoding UTF8 | ConvertFrom-Json).rules
            foreach ($n in 'ECW_C11_01', 'ECW_C11_02', 'ECW_C12_01', 'ECW_C12_04_01', 'ECW_C12_04_02') {
              $r = $rules | Where-Object { $_.methodology -eq $n }
              $s = Get-LandRuleSampleArguments -Expression ([string]$r.expression) -NaeCandidate '6646'
              "$n=" + (($s.Keys | Sort-Object) -join ',')
            }
            """;

        var (exit, output) = RunPowerShell(
            script, LandFile("LandParityRuleArgs.Lib.ps1"), Path.Combine(SourceTree.Root, RulesFile.Replace('/', Path.DirectorySeparatorChar)));

        Assert.True(exit == 0, output);
        Assert.Contains("ECW_C11_01=Land_NAE,Land_TypeFuelOrStream", output, StringComparison.Ordinal);
        Assert.Contains("ECW_C11_02=Land_NAE,Land_TypeFuelOrStream", output, StringComparison.Ordinal);
        Assert.Contains("ECW_C12_01=Land_Streams", output, StringComparison.Ordinal);
        Assert.Contains("ECW_C12_04_01=Land_InvNumEqp", output, StringComparison.Ordinal);
        Assert.Contains("ECW_C12_04_02=Land_TypeStoredMaterial", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Етап_LandParity_є_в_Build_але_не_входить_в_All()
    {
        var text = File.ReadAllText(LandFile("Build-LandTemplate.ps1"));

        Assert.Contains("'LandParity'", text, StringComparison.Ordinal);
        Assert.Contains("if ($Stage -contains 'LandParity') { Stage-LandParity }", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Want 'LandParity'", text, StringComparison.Ordinal);
    }
}
