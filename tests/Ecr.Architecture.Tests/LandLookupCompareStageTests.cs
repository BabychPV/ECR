// tests/Ecr.Architecture.Tests/LandLookupCompareStageTests.cs
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// LAND-5094 DIAG-A: етап <c>-Stage LookupCompare</c> переписує в ЧЕРНЕТЦІ порівняння аргументу-Lookup з текстом довідника
/// (<c>@Land_Measure_ReleaseColdVent = 'No - Нет'</c>) на <c>REGFIELD(@X, 'NAME') = 'No - Нет'</c>
/// (<c>tools/land/LandLookupCompare.Lib.ps1</c>; перелік аргументів - з колонок Lookup/Formula-над-Lookup моделі).
/// Функції й етап виконуються справжнім PowerShell із підставленими Api/Invoke-Ecr. Значень прода тут немає.
/// </summary>
/// <remarks>
/// Мутаційний доказ (лише цей клас): у <c>Convert-LandLookupCompareExpression</c> прибрати перевірку
/// <c>-cnotcontains</c> (літерал не з довідника) - червоніє <see cref="Літерал_не_з_довідника_і_REGFIELD_лишаються_як_є"/>;
/// прибрати гілку <c>$rx1</c> - <see cref="Порівняння_Lookup_з_текстом_іде_через_REGFIELD_NAME"/>; прибрати
/// <c>Set-LandLookupCompareTestArguments</c> з етапу - <see cref="Етап_переписує_чернетку_і_виставляє_value_у_PARITY_1_повтор_нічого_не_міняє"/>.
/// </remarks>
public sealed class LandLookupCompareStageTests
{
    private static string BuildScript => Path.Combine(SourceTree.Root, "tools", "land", "Build-LandTemplate.ps1");

    private const string LibHarness = """
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $ErrorActionPreference = 'Stop'
        $root = $args[0]
        . (Join-Path $root 'tools\land\LandLookupCompare.Lib.ps1')
        function Out($k, $o) { $j = if ($null -eq $o) { 'null' } else { $o | ConvertTo-Json -Compress -Depth 6 }; Write-Host ($k + '=' + $j) }
        $no = 'No - ' + [char]0x041D + [char]0x0435 + [char]0x0442
        $plan = [ordered]@{ Land_Measure_ReleaseColdVent = [pscustomobject]@{ Registries = @('LAND_COLD2_VENTS_YES_NO'); Names = @(('Yes - ' + [char]0x0414 + [char]0x0430), $no) } }
        $x = '@Land_Measure_ReleaseColdVent'
        $r1 = Convert-LandLookupCompareExpression -Expression ("if ($x = '$no', 0, 1)") -Plan $plan
        Out 'R1' $r1
        Out 'R2' (Convert-LandLookupCompareExpression -Expression $r1.Expression -Plan $plan)
        Out 'R3' (Convert-LandLookupCompareExpression -Expression ("if ('$no' <> ${x}?, 1, 2)") -Plan $plan)
        Out 'R4' (Convert-LandLookupCompareExpression -Expression ("if (in($x, 'Other', '$no'), 0, 1)") -Plan $plan)
        Out 'R5' (Convert-LandLookupCompareExpression -Expression "if ($x = 'Fuel gas', 0, 1)" -Plan $plan)
        Out 'R6' (Convert-LandLookupCompareExpression -Expression "if (@Land_Measure_ReleaseColdVentX = '$no', 0, 1)" -Plan $plan)
        Out 'R7' (Convert-LandLookupCompareExpression -Expression "if (@Land_Other = '$no', 0, 1)" -Plan $plan)
        Out 'R8' (Convert-LandLookupCompareExpression -Expression "if (REGFIELD($x, 'NAME') = '$no', 0, 1)" -Plan $plan)
        Out 'R9' (Convert-LandLookupCompareExpression -Expression "$x * 2" -Plan $plan)
        $e1 = @([pscustomobject]@{ argumentCode = 'Land_Measure_ReleaseColdVent'; value = $null; valueString = $no; unitId = $null },
                [pscustomobject]@{ argumentCode = 'Land_NAE'; value = $null; valueString = '0035'; unitId = $null })
        $res = { param($reg, $name) if ($name -eq $no) { 190 } else { $null } }
        $t1 = Set-LandLookupCompareTestArguments -Existing $e1 -Codes @('Land_Measure_ReleaseColdVent') -Plan $plan -Resolve $res
        Out 'T1' $t1
        Out 'T2' (Set-LandLookupCompareTestArguments -Existing $t1.Arguments -Codes @('Land_Measure_ReleaseColdVent') -Plan $plan -Resolve $res)
        Out 'T3' (Set-LandLookupCompareTestArguments -Existing @($e1[1]) -Codes @('Land_Measure_ReleaseColdVent') -Plan $plan -Resolve $res)
        Out 'T4' (Set-LandLookupCompareTestArguments -Existing $e1 -Codes @('Land_Measure_ReleaseColdVent') -Plan $plan)
        $model = Get-Content (Join-Path $root 'docs\delivery\reference-data\land-template\land-template.model.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $mp = Get-LandLookupCompareArguments -Model $model
        Out 'P1' ([ordered]@{
            keys = @($mp.Keys)
            cold = @($mp['Land_Measure_ReleaseColdVent'].Names)
            coldReg = @($mp['Land_Measure_ReleaseColdVent'].Registries)
            hasRegfieldBridge = $mp.Contains('Land_OverhauledEngines')
            hasDensity = $mp.Contains('Density20') })
        """;

    private const string StageHarness = """
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $ErrorActionPreference = 'Stop'
        $script = $args[0]; $mode = $args[1]; $root = $args[2]
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$null)
        $fn = $ast.Find({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq 'Stage-LookupCompare' }, $true)
        if (-not $fn) { throw 'Stage-LookupCompare не знайдено' }
        $here = Join-Path $root 'tools\land'
        $Mdl = Get-Content (Join-Path $root 'docs\delivery\reference-data\land-template\land-template.model.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $OnlyMethodology = @()
        $no = 'No - ' + [char]0x041D + [char]0x0435 + [char]0x0442
        function Log([string]$m) { Write-Host ('LOG=' + $m) }
        function LoginAs([string]$u) { }
        function Load-Registries { }
        function RegEntryId([string]$reg, [string]$name) { if ($name -eq $no) { return 190 }; throw "no entry $name" }
        function Get-EcrErrorCode($r) { return 'ECR-X' }
        function Items($j) {
            if ($null -eq $j) { return @() }
            if ($j -is [array]) { return $j }
            if ($j.PSObject.Properties['items']) { return @($j.items) }
            return @($j)
        }
        function Expr($mid) {
            if ($mode -eq 'done') { return "if (REGFIELD(@Land_Measure_ReleaseColdVent, 'NAME') = '$no', 0, 1)" }
            return "if (@Land_Measure_ReleaseColdVent = '$no', 0, 1)"
        }
        function Api {
            param([string]$Method, [string]$Path, $Payload = $null)
            if ($Path -eq '/api/v1/methodologies') {
                return @{ Json = @(@{ code = 'ECW_C12_03'; id = 7 }, @{ code = 'ECW_C05_01'; id = 8 }) }
            }
            if ($Path -match '/methodologies/(\d+)/versions$') {
                $st = if ($mode -eq 'published') { 'Published' } else { 'Draft' }
                return @{ Json = @(@{ id = ([int]$Matches[1] * 10); status = $st }) }
            }
            if ($Path -match '/methodologies/(\d+)/versions/(\d+)/formulas$') {
                if ([int]$Matches[1] -eq 8) {
                    return @{ Json = @(@{ code = 'F1'; expression = '@Density20 * 2'; argumentsCsv = '@Density20;'; resultType = 'Number'; outputUnitId = 5; scope = $null }) }
                }
                return @{ Json = @(
                    @{ code = 'ECW_EC_tons_XXX'; expression = (Expr 7); argumentsCsv = '@Land_Measure_ReleaseColdVent;'; resultType = 'Number'; outputUnitId = 5; scope = $null },
                    @{ code = 'ECW_Other'; expression = '@Density20 * 2'; argumentsCsv = '@Density20;'; resultType = 'Number'; outputUnitId = 5; scope = $null }) }
            }
            if ($Path -match '/methodologies/7/versions/(\d+)/tests$') {
                $list = @(@{ argumentCode = 'Land_Measure_ReleaseColdVent'; value = $null; valueString = $no; unitId = $null })
                if ($mode -eq 'done') { $list = @(@{ argumentCode = 'Land_Measure_ReleaseColdVent'; value = 190; valueString = $no; unitId = $null }) }
                $inj = @{ documentId = 1; periodKey = '2026-01'; arguments = $list } | ConvertTo-Json -Compress -Depth 6
                return @{ Json = @(@{ code = 'PARITY_1'; inputJson = $inj; expectedJson = '{}'; tolerance = '0.001' }) }
            }
            throw "Api: неочікуваний шлях $Method $Path"
        }
        function Invoke-Ecr {
            param([string]$Method, [string]$Path, $Body = $null)
            if ($Method -eq 'PUT' -and ($Path -like '*/formulas/*' -or $Path -like '*/tests/PARITY_1')) {
                Write-Host ('PUT=' + $Path + '|' + ($Body | ConvertTo-Json -Compress -Depth 6))
                return @{ Status = 200; Text = ''; Json = $null }
            }
            throw "Invoke-Ecr: неочікуваний виклик $Method $Path"
        }
        . ([scriptblock]::Create($fn.Extent.Text))
        Stage-LookupCompare
        """;

    private static (int Exit, string Output) RunPowerShell(string harness, params string[] arguments)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-lookupcmp-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, harness, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file }
                         .Concat(arguments))
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

    private static Dictionary<string, JsonElement> Lib()
    {
        var (exit, output) = RunPowerShell(LibHarness, SourceTree.Root);
        Assert.True(exit == 0, output);
        var result = new Dictionary<string, JsonElement>();
        foreach (var line in output.Split('\n').Select(l => l.Trim()))
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || !Regex.IsMatch(line[..eq], "^[A-Z0-9_]+$"))
            {
                continue;
            }

            result[line[..eq]] = JsonDocument.Parse(line[(eq + 1)..]).RootElement.Clone();
        }

        return result;
    }

    private static (List<(string Path, JsonElement Body)> Puts, string Output) Stage(string mode)
    {
        var (exit, text) = RunPowerShell(StageHarness, BuildScript, mode, SourceTree.Root);
        Assert.True(exit == 0, text);
        var puts = new List<(string, JsonElement)>();
        foreach (var line in text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("PUT=", StringComparison.Ordinal)))
        {
            var bar = line.IndexOf('|', StringComparison.Ordinal);
            puts.Add((line[4..bar], JsonDocument.Parse(line[(bar + 1)..]).RootElement.Clone()));
        }

        return (puts, text);
    }

    private const string No = "No - Нет";
    private const string Rf = "REGFIELD(@Land_Measure_ReleaseColdVent, 'NAME')";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Порівняння_Lookup_з_текстом_іде_через_REGFIELD_NAME()
    {
        var r = Lib();

        Assert.True(r["R1"].GetProperty("Changed").GetBoolean());
        Assert.Equal($"if ({Rf} = '{No}', 0, 1)", r["R1"].GetProperty("Expression").GetString());
        Assert.Equal(
            ["Land_Measure_ReleaseColdVent"], r["R1"].GetProperty("Replaced").EnumerateArray().Select(e => e.GetString()!).ToArray());

        // літерал зліва, маркер `?` зберігається
        Assert.Equal(
            $"if ('{No}' <> REGFIELD(@Land_Measure_ReleaseColdVent?, 'NAME'), 1, 2)",
            r["R3"].GetProperty("Expression").GetString());

        // in(...) з іменем запису
        Assert.Equal($"if (in({Rf}, 'Other', '{No}'), 0, 1)", r["R4"].GetProperty("Expression").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Повтор_нічого_не_міняє()
    {
        var r = Lib();

        Assert.False(r["R2"].GetProperty("Changed").GetBoolean());
        Assert.Equal($"if ({Rf} = '{No}', 0, 1)", r["R2"].GetProperty("Expression").GetString());
        Assert.False(r["R8"].GetProperty("Changed").GetBoolean());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Літерал_не_з_довідника_і_REGFIELD_лишаються_як_є()
    {
        var r = Lib();

        // 'Fuel gas' - не запис довідника LAND_COLD2_VENTS_YES_NO
        Assert.False(r["R5"].GetProperty("Changed").GetBoolean());
        // довший код аргументу і чужий аргумент не чіпаються
        Assert.False(r["R6"].GetProperty("Changed").GetBoolean());
        Assert.False(r["R7"].GetProperty("Changed").GetBoolean());
        // без порівняння з літералом - без змін
        Assert.False(r["R9"].GetProperty("Changed").GetBoolean());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Тест_кейс_отримує_числове_value_з_довідника_без_вигадування()
    {
        var r = Lib();

        Assert.Equal(
            ["Land_Measure_ReleaseColdVent"], r["T1"].GetProperty("Updated").EnumerateArray().Select(e => e.GetString()!).ToArray());
        var arg = r["T1"].GetProperty("Arguments").EnumerateArray()
            .Single(a => a.GetProperty("argumentCode").GetString() == "Land_Measure_ReleaseColdVent");
        Assert.Equal(190, arg.GetProperty("value").GetDouble());
        Assert.Equal(2, r["T1"].GetProperty("Arguments").GetArrayLength());

        // повтор - 0 оновлень
        Assert.Empty(r["T2"].GetProperty("Updated").EnumerateArray());
        // аргументу в тест-кейсі немає / довідника не дали - Missing, значення не вигадуються
        Assert.Equal(
            ["Land_Measure_ReleaseColdVent"], r["T3"].GetProperty("Missing").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Empty(r["T4"].GetProperty("Updated").EnumerateArray());
        Assert.Equal(
            ["Land_Measure_ReleaseColdVent"], r["T4"].GetProperty("Missing").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Перелік_із_моделі_мост_ReleaseColdVent_є_а_REGFIELD_мости_ні()
    {
        var p = Lib()["P1"];

        var keys = p.GetProperty("keys").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains("Land_Measure_ReleaseColdVent", keys);
        Assert.Equal(
            ["Yes - Да", No], p.GetProperty("cold").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(
            ["LAND_COLD2_VENTS_YES_NO"], p.GetProperty("coldReg").EnumerateArray().Select(e => e.GetString()!).ToArray());
        // Formula-колонка `REGFIELD([XRef], 'NAME')` уже повертає текст - у переліку її немає
        Assert.False(p.GetProperty("hasRegfieldBridge").GetBoolean());
        // Density20 - REGFIELD(REGFIND(...)): число, не id запису
        Assert.False(p.GetProperty("hasDensity").GetBoolean());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Етап_переписує_чернетку_і_виставляє_value_у_PARITY_1_повтор_нічого_не_міняє()
    {
        var (puts, output) = Stage("fresh");

        var formulas = puts.Where(p => p.Path.Contains("/formulas/", StringComparison.Ordinal)).ToArray();
        var tests = puts.Where(p => p.Path.EndsWith("/tests/PARITY_1", StringComparison.Ordinal)).ToArray();
        // лише формула з порівнянням; ECW_Other і методологія без порівняння не чіпаються
        var f = Assert.Single(formulas);
        Assert.EndsWith("/methodologies/7/versions/70/formulas/ECW_EC_tons_XXX", f.Path, StringComparison.Ordinal);
        Assert.Equal($"if ({Rf} = '{No}', 0, 1)", f.Body.GetProperty("expression").GetString());
        Assert.Equal("@Land_Measure_ReleaseColdVent;", f.Body.GetProperty("argumentsCsv").GetString());
        Assert.Equal("Number", f.Body.GetProperty("resultType").GetString());

        var input = JsonDocument.Parse(Assert.Single(tests).Body.GetProperty("inputJson").GetString()!).RootElement;
        Assert.Equal(190, input.GetProperty("arguments")[0].GetProperty("value").GetDouble());

        // повторний запуск на вже переписаній чернетці - 0 змін
        Assert.Empty(Stage("done").Puts);
        Assert.Contains("змінено: 2", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Етап_не_чіпає_опубліковану_версію_і_не_входить_у_All()
    {
        var (puts, output) = Stage("published");

        Assert.Empty(puts);
        Assert.Contains("опублікована", output, StringComparison.Ordinal);

        var source = File.ReadAllText(BuildScript);
        Assert.Matches(@"if \(\$Stage -contains 'LookupCompare'\) \{ Stage-LookupCompare \}", source);
        Assert.DoesNotMatch(@"Want 'LookupCompare'", source);
        Assert.Matches(@"ValidateSet\([^)]*'LookupCompare'", source);
        Assert.True(
            source.IndexOf("Stage-LookupCompare }", StringComparison.Ordinal)
            < source.IndexOf("Stage-OptionalArguments }", StringComparison.Ordinal),
            "LookupCompare має виконуватися перед OptionalArguments");
    }
}
