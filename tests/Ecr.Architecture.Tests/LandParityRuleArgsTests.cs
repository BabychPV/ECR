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

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Склад_речовин_форми_A_береться_зі_словника_а_з_суфіксами_лишається_незмінним()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            . $args[1]
            $sa = Get-Content -LiteralPath $args[2] -Raw -Encoding UTF8 | ConvertFrom-Json
            function Show($tag, $cats, $name, $force) {
              $suffix = Get-LandSubstanceCodes -Categories $cats
              $e = $sa.methodologies.PSObject.Properties[$name]
              $codes = if ($null -ne $e) { $e.Value.codes } else { $null }
              $r = Resolve-LandMethodologySubstanceCodes -SuffixCodes $suffix -ApplicabilityCodes $codes -FromApplicability $force
              "$tag=$($r.Source):" + ($r.Codes -join ',')
            }
            # форма A: категорії без суфікса (ключ-група або NAE) -> словник застосовності методології
            Show 'C1110' @('0220', '0340', '0647', '0884', '0960', '0970', 'Common') 'ECW_C11_10' $false
            Show 'C111105' @('0480', '0481', '6482(001)') 'ECW_C11_11_05' $false
            # форма B із суфіксами: склад - суфікси, словник не підмішується
            Show 'B_C111103' @('0782(001)_333', '0782(001)_415', '0804_1702', 'Common') 'ECW_C11_11_03' $false
            Show 'B_C1201' @('FuelGas_1702', 'Diesel_333') 'ECW_C12_01' $false
            # явна позначка правила - завжди словник, навіть із суфіксами (C12_03)
            Show 'FLAG' @('0026_333') 'ECW_C12_03' $true
            # форма A, але словника немає - склад не задається
            Show 'NODICT' @('0647') 'ECW_C11_04' $false
            Show 'NOCATS' @() 'ECW_C11_10' $false
            """;

        var (exit, output) = RunPowerShell(
            script,
            LandFile("LandParityRuleArgs.Lib.ps1"),
            LandFile("LandSubstanceApplicability.Lib.ps1"),
            Path.Combine(SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-substance-applicability.json"));

        Assert.True(exit == 0, output);
        const string all15 = "333,334,370,415,416,602,616,621,627,1702,1707,1715,1720,1728,2754";
        Assert.Contains("C1110=ApplicabilityFallback:" + all15, output, StringComparison.Ordinal);
        Assert.Contains("C111105=ApplicabilityFallback:330,333", output, StringComparison.Ordinal);
        Assert.Contains("B_C111103=Suffix:333,415,1702", output, StringComparison.Ordinal);
        Assert.Contains("B_C1201=Suffix:333,1702", output, StringComparison.Ordinal);
        Assert.Contains("FLAG=Applicability:" + all15, output, StringComparison.Ordinal);
        Assert.Contains("NODICT=None:", output, StringComparison.Ordinal);
        Assert.Contains("NOCATS=ApplicabilityFallback:" + all15, output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Форма_A_у_файлах_моделі_це_рівно_C11_10_і_C11_11_05_а_решта_словникових_методологій_мають_суфікс()
    {
        // Методологія зі словником застосовності й правилом категорії без позначки «form B» у note - форма A:
        // константи за ключем без коду. Перелік зафіксовано: нова методологія форми A мусить бути названа свідомо
        // (і мати склад із словника), а не випасти з складу речовин мовчки.
        var dir = Path.Combine(SourceTree.Root, "docs", "delivery", "reference-data", "land-template");
        using var rules = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "af-land-category-rules.json")));
        using var dictionary = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(dir, "land-substance-applicability.json")));
        var withDictionary = dictionary.RootElement.GetProperty("methodologies").EnumerateObject()
            .Where(m => m.Value.GetProperty("codes").EnumerateObject()
                .Any(k => k.Value.EnumerateArray().Any()))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        var formA = rules.RootElement.GetProperty("rules").EnumerateArray()
            .Where(r => withDictionary.Contains(r.GetProperty("methodology").GetString()!))
            .Where(r => !r.GetProperty("note").GetString()!.Contains("form B", StringComparison.Ordinal)
                        && !(r.TryGetProperty("substancesFromApplicability", out var flag) && flag.GetBoolean()))
            .Select(r => r.GetProperty("methodology").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["ECW_C11_10", "ECW_C11_11_05"], formA);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Етап_LandParity_бере_склад_через_Resolve_LandMethodologySubstanceCodes()
    {
        var build = File.ReadAllText(LandFile("Build-LandTemplate.ps1"));
        var parity = build.IndexOf("function Stage-LandParity", StringComparison.Ordinal);
        var next = build.IndexOf("function Stage-MassFraction", parity, StringComparison.Ordinal);
        Assert.True(parity > 0 && next > parity);
        var stage = build[parity..next];

        Assert.Contains("Resolve-LandMethodologySubstanceCodes", stage, StringComparison.Ordinal);
        Assert.Contains("Get-LandSubstanceCodes -Categories $cats", stage, StringComparison.Ordinal);
        Assert.Contains("substancesFromApplicability", stage, StringComparison.Ordinal);
        // Правило 2а: розширення складу лише з обгорткою SUBSTANCE_APPLICABLE у моделі (інакше ECR_ONLY по чужих речовинах).
        Assert.Contains("Test-LandMixedFuelStreamCategories -Categories $cats", stage, StringComparison.Ordinal);
        Assert.Contains("$saWrapper -and", stage, StringComparison.Ordinal);
        Assert.Contains("-MixedFuelStreamCategories $mixed", stage, StringComparison.Ordinal);
    }

    private const string Union19 = "330,333,334,370,415,416,602,616,621,627,1052,1702,1707,1715,1720,1728,1852,1880,2754";

    private const string Union16 = "333,334,370,415,416,602,616,621,627,1052,1702,1707,1715,1720,1728,2754";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Змішані_категорії_FG_LPG_без_суфікса_і_суфікси_речовин_дають_суфікси_об_єднані_зі_словником()
    {
        // K-п'ятірка (LAND-5094): у C11_01 суфікси давали 7 кодів, а FG/LPG-набір словника відкидався правилом 2.
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            . $args[1]
            $sa = Get-Content -LiteralPath $args[2] -Raw -Encoding UTF8 | ConvertFrom-Json
            function Show($tag, $cats, $name, $wrapper) {
              $suffix = Get-LandSubstanceCodes -Categories $cats
              $mixed = $wrapper -and (Test-LandMixedFuelStreamCategories -Categories $cats)
              $e = $sa.methodologies.PSObject.Properties[$name]
              $r = Resolve-LandMethodologySubstanceCodes -SuffixCodes $suffix -ApplicabilityCodes $e.Value.codes `
                  -FromApplicability $false -MixedFuelStreamCategories $mixed
              "$tag=$($r.Source):" + ($r.Codes -join ',')
            }
            $c01 = @('Wwater_1052', '6362_452_1852', '6362_452_1880', '6362_452_330', '6362_452_333', '401_1715', 'sludge_2754',
                     '6646_FG', '6646_LPG', '6647_FG')
            $c02 = @('sludge_1052', 'sludge_333', 'caustic_2754', '6646_FG', '6646_LPG')
            Show 'C01' $c01 'ECW_C11_01' $true
            Show 'C02' $c02 'ECW_C11_02' $true
            # без обгортки застосовності склад не розширюється (ECR_ONLY по чужих речовинах)
            Show 'C01NOWRAP' $c01 'ECW_C11_01' $false
            # лише суфікси / «Common» / NAE-потік без FG|LPG - без змін
            Show 'C01PURE' @('Wwater_1052', '401_1715', 'sludge_2754') 'ECW_C11_01' $true
            Show 'C1103' @('0782(001)_333', '0804_1702', 'Common') 'ECW_C11_11_03' $true
            Show 'C1201' @('FuelGas_1702', 'Diesel_333', 'Common') 'ECW_C12_01' $true
            # форма A (без суфіксів) лишається за правилом 3
            Show 'C1110' @('0220', '0340', 'Common') 'ECW_C11_10' $true
            # кожна методологія словника з чистими суфіксами: до/після однакові
            $changed = @()
            foreach ($p in $sa.methodologies.PSObject.Properties) {
              $codes = @($p.Value.codes.PSObject.Properties | ForEach-Object { $_.Value } | ForEach-Object { $_ } | Select-Object -Unique)
              $cats = @($codes | ForEach-Object { "grp_$_" })
              $suffix = Get-LandSubstanceCodes -Categories $cats
              $before = Resolve-LandMethodologySubstanceCodes -SuffixCodes $suffix -ApplicabilityCodes $p.Value.codes -MixedFuelStreamCategories $false
              $after = Resolve-LandMethodologySubstanceCodes -SuffixCodes $suffix -ApplicabilityCodes $p.Value.codes `
                  -MixedFuelStreamCategories (Test-LandMixedFuelStreamCategories -Categories $cats)
              if (($before.Codes -join ',') -ne ($after.Codes -join ',') -or $before.Source -ne $after.Source) { $changed += $p.Name }
            }
            "CHANGEDPURE=" + ($changed -join ',')
            "METHODS=" + (($sa.methodologies.PSObject.Properties | ForEach-Object Name) -join ',')
            """;

        var (exit, output) = RunPowerShell(
            script,
            LandFile("LandParityRuleArgs.Lib.ps1"),
            LandFile("LandSubstanceApplicability.Lib.ps1"),
            Path.Combine(SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-substance-applicability.json"));

        Assert.True(exit == 0, output);
        Assert.Contains("C01=SuffixUnionApplicability:" + Union19, output, StringComparison.Ordinal);
        Assert.Contains("C02=SuffixUnionApplicability:" + Union16, output, StringComparison.Ordinal);
        Assert.Contains("C01NOWRAP=Suffix:330,333,1052,1715,1852,1880,2754", output, StringComparison.Ordinal);
        Assert.Contains("C01PURE=Suffix:1052,1715,2754", output, StringComparison.Ordinal);
        Assert.Contains("C1103=Suffix:333,1702", output, StringComparison.Ordinal);
        Assert.Contains("C1201=Suffix:333,1702", output, StringComparison.Ordinal);
        Assert.Contains("C1110=ApplicabilityFallback:", output, StringComparison.Ordinal);
        // до/після: чисті суфікси не змінюють склад жодної методології словника
        Assert.Contains("CHANGEDPURE=" + Environment.NewLine, output + Environment.NewLine, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Обгортка_застосовності_для_змішаних_C11_01_і_C11_02_є_в_моделі_а_виходи_tons_описані()
    {
        var dir = Path.Combine(SourceTree.Root, "docs", "delivery", "reference-data", "land-template");
        using var model = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "land-template.model.json")));
        var wrapped = model.RootElement.GetProperty("substanceApplicability").GetProperty("methodologies");
        foreach (var name in new[] { "ECW_C11_01", "ECW_C11_02" })
        {
            Assert.True(wrapped.TryGetProperty(name, out var entry), $"{name}: немає обгортки застосовності в моделі");
            Assert.Equal("Stream", entry.GetProperty("kind").GetString());
        }

        using var outputs = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "land-result-outputs.json")));
        foreach (var (name, expected) in new[]
                 {
                     ("ECW_C11_01", new[] { "ECW_EC_tons_XXX_type1_1", "ECW_EC_tons_XXX_type1_2", "ECW_EC_tons_XXX_type1_3", "ECW_EC_tons_XXX_type2" }),
                     ("ECW_C11_02", new[] { "ECW_EC_tons_XXX_type1", "ECW_EC_tons_XXX_type2" }),
                 })
        {
            var declared = outputs.RootElement.GetProperty("methodologies").GetProperty(name).GetProperty("outputs")
                .EnumerateArray().Select(o => o.GetString()!).ToHashSet(StringComparer.Ordinal);
            Assert.All(expected, e => Assert.Contains(e, declared));
        }
    }
}