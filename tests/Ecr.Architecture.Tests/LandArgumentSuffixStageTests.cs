// tests/Ecr.Architecture.Tests/LandArgumentSuffixStageTests.cs
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// K1 (D-8, клас K): етап <c>-Stage ArgumentSuffix</c> підміняє в ЧЕРНЕТЦІ токен аргументу на суфіксований
/// (<c>tools/land/LandArgumentSuffix.Lib.ps1</c>; перелік - <c>argumentBridges.argumentSuffixes</c> моделі) і додає
/// суфіксований аргумент у PARITY_1. Функції й етап виконуються справжнім PowerShell із підставленими Api/Invoke-Ecr.
/// Значень прода тут немає. Колонки-мости перевіряє <see cref="LandArgumentSuffixModelTests"/>.
/// </summary>
/// <remarks>
/// Мутаційний доказ (лише цей клас): у <c>Convert-LandArgumentSuffixFormula</c> прибрати <c>(?![A-Za-z0-9_])</c> -
/// червоніє <see cref="Підміна_не_чіпає_префікс_довшого_імені_і_є_ідемпотентною"/> (або повтор дає
/// <c>__ECW_C11_01__ECW_C11_01</c>); прибрати збереження кінцевого <c>?</c> -
/// <see cref="Підміна_зберігає_кінцевий_знак_питання_чужі_елементи_і_null"/>; прибрати додавання аргументу в PARITY_1 -
/// <see cref="Етап_підміняє_формули_чернетки_і_додає_аргумент_у_PARITY_1_повтор_нічого_не_міняє"/>.
/// </remarks>
public sealed class LandArgumentSuffixStageTests
{
    private static string BuildScript => Path.Combine(SourceTree.Root, "tools", "land", "Build-LandTemplate.ps1");

    private const string LibHarness = """
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $ErrorActionPreference = 'Stop'
        $root = $args[0]
        . (Join-Path $root 'tools\land\LandArgumentSuffix.Lib.ps1')
        function Out($k, $o) { $j = if ($null -eq $o) { 'null' } else { $o | ConvertTo-Json -Compress -Depth 6 }; Write-Host ($k + '=' + $j) }
        $col = 'Land_Measure_OperatingTime__ECW_C11_01'
        $sfx = @([pscustomobject]@{ Argument = 'Land_Measure_OperatingTime'; Column = $col })
        $r1 = Convert-LandArgumentSuffixFormula -Expression '@Land_Measure_OperatingTime * @Density20' -ArgumentsCsv '@Land_Measure_OperatingTime;@Density20;' -Suffixes $sfx
        Out 'R1' $r1
        Out 'R2' (Convert-LandArgumentSuffixFormula -Expression $r1.Expression -ArgumentsCsv $r1.ArgumentsCsv -Suffixes $sfx)
        Out 'R3' (Convert-LandArgumentSuffixFormula -Expression '@Land_Measure_OperatingTime' -ArgumentsCsv '@Land_Measure_OperatingTime?;@Density20;' -Suffixes $sfx)
        Out 'R4' (Convert-LandArgumentSuffixFormula -Expression '@Land_Measure_OperatingTimeDays + @Land_Measure_OperatingTime_X' -ArgumentsCsv '@Land_Measure_OperatingTimeDays;@Land_Measure_OperatingTime_X;' -Suffixes $sfx)
        Out 'R5' (Convert-LandArgumentSuffixFormula -Expression '@Land_Measure_OperatingTime * 2' -ArgumentsCsv $null -Suffixes $sfx)
        Out 'R6' (Convert-LandArgumentSuffixFormula -Expression '1' -ArgumentsCsv '' -Suffixes $sfx)
        Out 'R7' (Convert-LandArgumentSuffixFormula -Expression '1' -ArgumentsCsv ' land.measure.operatingtime ; Other' -Suffixes $sfx)
        Out 'R8' (Convert-LandArgumentSuffixFormula -Expression '@Land_Measure_OperatingTime' -ArgumentsCsv ('@Land_Measure_OperatingTime;@' + $col + ';') -Suffixes $sfx)
        $existing = @([pscustomobject]@{ argumentCode = 'Land_Measure_OperatingTime'; value = 12.5; valueString = $null; unitId = 5 },
                      [pscustomobject]@{ argumentCode = 'Land_NAE'; value = $null; valueString = '0035'; unitId = $null })
        $t1 = Add-LandArgumentSuffixTestArguments -Existing $existing -Suffixes $sfx
        Out 'T1' $t1
        Out 'T2' (Add-LandArgumentSuffixTestArguments -Existing $t1.Arguments -Suffixes $sfx)
        Out 'T3' (Add-LandArgumentSuffixTestArguments -Existing @($existing[1]) -Suffixes $sfx)
        $model = Get-Content (Join-Path $root 'docs\delivery\reference-data\land-template\land-template.model.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $plan = Get-LandArgumentSuffixes -Model $model
        Out 'P1' ([ordered]@{ keys = @($plan.Keys); c1101 = $plan['ECW_C11_01'][0].Column; c1111 = $plan['ECW_C11_11_02'][0].Column })
        """;

    private const string StageHarness = """
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $ErrorActionPreference = 'Stop'
        $script = $args[0]; $mode = $args[1]; $root = $args[2]
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$null)
        $fn = $ast.Find({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq 'Stage-ArgumentSuffix' }, $true)
        if (-not $fn) { throw 'Stage-ArgumentSuffix не знайдено' }
        $here = Join-Path $root 'tools\land'
        $Mdl = Get-Content (Join-Path $root 'docs\delivery\reference-data\land-template\land-template.model.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        function Log([string]$m) { Write-Host ('LOG=' + $m) }
        function LoginAs([string]$u) { }
        function Get-EcrErrorCode($r) { return 'ECR-X' }
        function Items($j) {
            if ($null -eq $j) { return @() }
            if ($j -is [array]) { return $j }
            if ($j.PSObject.Properties['items']) { return @($j.items) }
            return @($j)
        }
        $codes = @{ 7 = 'ECW_C11_01'; 8 = 'ECW_C11_02'; 9 = 'ECW_C11_11_02' }
        function ArgOf($vid) { if ($vid -eq 90) { 'Land_Measure_SubstanceConsumption' } else { 'Land_Measure_OperatingTime' } }
        function Col($vid) { (ArgOf $vid) + '__' + $codes[[int]($vid / 10)] }
        function Formula($code, $expr, $args2) { return @{ code = $code; expression = $expr; argumentsCsv = $args2; resultType = 'Number'; outputUnitId = 5; scope = $null } }
        function Api {
            param([string]$Method, [string]$Path, $Payload = $null)
            if ($Path -eq '/api/v1/methodologies') {
                return @{ Json = @(@{ code = 'ECW_C11_01'; id = 7 }, @{ code = 'ECW_C11_02'; id = 8 }, @{ code = 'ECW_C11_11_02'; id = 9 }, @{ code = 'ECW_C11_10'; id = 10 }) }
            }
            if ($Path -like '*/methodologies/10/versions*') { throw 'C11_10 не з переліку: не мав читатися' }
            if ($Path -match '/methodologies/(\d+)/versions$') {
                $st = if ($mode -eq 'published') { 'Published' } else { 'Draft' }
                return @{ Json = @(@{ id = ([int]$Matches[1] * 10); status = $st }) }
            }
            if ($Path -match '/versions/(\d+)/formulas$') {
                $vid = [int]$Matches[1]
                $name = if ($mode -eq 'done') { Col $vid } else { ArgOf $vid }
                return @{ Json = @(
                    (Formula 'ECW_EC_tons_301' ('@' + $name + ' * @Density20') ('@' + $name + '?;@Density20;')),
                    (Formula 'ECW_Category' "'A'" $null),
                    (Formula 'ECW_Other' '@Density20 * 2' '@Density20;')) }
            }
            if ($Path -match '/versions/(\d+)/tests$') {
                $vid = [int]$Matches[1]
                $list = @(@{ argumentCode = (ArgOf $vid); value = 12.5; valueString = $null; unitId = 5 }, @{ argumentCode = 'Land_NAE'; value = $null; valueString = '0035'; unitId = $null })
                if ($mode -eq 'done') { $list += @{ argumentCode = (Col $vid); value = 12.5; valueString = $null; unitId = 5 } }
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
        Stage-ArgumentSuffix
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

        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-argsuffix-{Guid.NewGuid():N}.ps1");
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

    private const string Col = "Land_Measure_OperatingTime__ECW_C11_01";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Підміна_не_чіпає_префікс_довшого_імені_і_є_ідемпотентною()
    {
        var r = Lib();

        Assert.True(r["R1"].GetProperty("Changed").GetBoolean());
        Assert.Equal($"@{Col} * @Density20", r["R1"].GetProperty("Expression").GetString());
        Assert.Equal($"@{Col};@Density20;", r["R1"].GetProperty("ArgumentsCsv").GetString());

        // Повтор на вже підміненому - 0 змін (суфіксоване ім'я продовжується `_`, межа імені його не збігає).
        Assert.False(r["R2"].GetProperty("Changed").GetBoolean());
        Assert.Equal($"@{Col} * @Density20", r["R2"].GetProperty("Expression").GetString());
        Assert.Equal($"@{Col};@Density20;", r["R2"].GetProperty("ArgumentsCsv").GetString());

        // Префікс довшого імені не підміняється ні у виразі, ні у списку.
        Assert.False(r["R4"].GetProperty("Changed").GetBoolean());

        // Суфіксоване ім'я вже було в списку поряд з оригіналом: дубля не лишається.
        Assert.True(r["R8"].GetProperty("Changed").GetBoolean());
        Assert.Equal($"@{Col};", r["R8"].GetProperty("ArgumentsCsv").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Підміна_зберігає_кінцевий_знак_питання_чужі_елементи_і_null()
    {
        var r = Lib();

        Assert.Equal($"@{Col};@Density20;", r["R1"].GetProperty("ArgumentsCsv").GetString());
        Assert.Equal($"@{Col}?;@Density20;", r["R3"].GetProperty("ArgumentsCsv").GetString());

        // Вираз без списку: $null лишається $null, змінюється лише вираз.
        Assert.True(r["R5"].GetProperty("Changed").GetBoolean());
        Assert.Equal($"@{Col} * 2", r["R5"].GetProperty("Expression").GetString());
        Assert.Equal(JsonValueKind.Null, r["R5"].GetProperty("ArgumentsCsv").ValueKind);

        // Порожній рядок - «нуль аргументів» (ECR-CALC-0432), не змінюється.
        Assert.False(r["R6"].GetProperty("Changed").GetBoolean());
        Assert.Equal(string.Empty, r["R6"].GetProperty("ArgumentsCsv").GetString());

        // Елемент без `@`, інший регістр, `.` = `_`; чужий елемент лишається як є.
        Assert.True(r["R7"].GetProperty("Changed").GetBoolean());
        Assert.Equal($"{Col};Other;", r["R7"].GetProperty("ArgumentsCsv").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Тест_кейс_отримує_суфіксований_аргумент_з_тим_самим_значенням_без_вигадування()
    {
        var r = Lib();

        var added = r["T1"].GetProperty("Added").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal([Col], added);
        var args = r["T1"].GetProperty("Arguments").EnumerateArray().ToArray();
        Assert.Equal(3, args.Length);
        var copy = args.Single(a => a.GetProperty("argumentCode").GetString() == Col);
        Assert.Equal(12.5, copy.GetProperty("value").GetDouble());
        Assert.Equal(5, copy.GetProperty("unitId").GetInt32());
        // оригінал лишається
        Assert.Contains(args, a => a.GetProperty("argumentCode").GetString() == "Land_Measure_OperatingTime");

        // повтор - 0 доданих
        Assert.Empty(r["T2"].GetProperty("Added").EnumerateArray());
        // оригіналу в тест-кейсі немає - не вигадуємо значення, називаємо як Missing
        Assert.Empty(r["T3"].GetProperty("Added").EnumerateArray());
        Assert.Equal(
            ["Land_Measure_OperatingTime"], r["T3"].GetProperty("Missing").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Перелік_із_моделі_три_методології()
    {
        var p = Lib()["P1"];

        Assert.Equal(
            ["ECW_C11_01", "ECW_C11_02", "ECW_C11_11_02"],
            p.GetProperty("keys").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(Col, p.GetProperty("c1101").GetString());
        Assert.Equal("Land_Measure_SubstanceConsumption__ECW_C11_11_02", p.GetProperty("c1111").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Етап_підміняє_формули_чернетки_і_додає_аргумент_у_PARITY_1_повтор_нічого_не_міняє()
    {
        var (puts, output) = Stage("fresh");

        // Три методології × (одна формула з аргументом + PARITY_1); ECW_Category і ECW_Other не чіпаються;
        // ECW_C11_10 не з переліку: не читається.
        var formulas = puts.Where(p => p.Path.Contains("/formulas/", StringComparison.Ordinal)).ToArray();
        var tests = puts.Where(p => p.Path.EndsWith("/tests/PARITY_1", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, formulas.Length);
        Assert.Equal(3, tests.Length);
        Assert.All(formulas, f => Assert.EndsWith("/formulas/ECW_EC_tons_301", f.Path, StringComparison.Ordinal));
        Assert.DoesNotContain("ECW_C11_10", output, StringComparison.Ordinal);

        var f70 = formulas.Single(f => f.Path.Contains("/versions/70/", StringComparison.Ordinal)).Body;
        Assert.Equal($"@{Col} * @Density20", f70.GetProperty("expression").GetString());
        Assert.Equal($"@{Col}?;@Density20;", f70.GetProperty("argumentsCsv").GetString());

        var f90 = formulas.Single(f => f.Path.Contains("/versions/90/", StringComparison.Ordinal)).Body;
        Assert.Equal(
            "@Land_Measure_SubstanceConsumption__ECW_C11_11_02 * @Density20", f90.GetProperty("expression").GetString());

        var input = JsonDocument.Parse(
            tests.Single(t => t.Path.Contains("/versions/70/", StringComparison.Ordinal)).Body.GetProperty("inputJson").GetString()!)
            .RootElement;
        var codes = input.GetProperty("arguments").EnumerateArray().Select(a => a.GetProperty("argumentCode").GetString()!).ToArray();
        Assert.Equal(["Land_Measure_OperatingTime", "Land_NAE", Col], codes);
        Assert.Equal(12.5, input.GetProperty("arguments")[2].GetProperty("value").GetDouble());

        // Повторний запуск на вже підміненій чернетці - 0 змін.
        Assert.Empty(Stage("done").Puts);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Етап_не_чіпає_опубліковану_версію_і_не_входить_у_All()
    {
        var (puts, output) = Stage("published");

        Assert.Empty(puts);
        Assert.Contains("опублікована", output, StringComparison.Ordinal);

        // лише явно: умова запуску - `$Stage -contains 'ArgumentSuffix'`, не Want (яке враховує All)
        var source = File.ReadAllText(BuildScript);
        Assert.Matches(@"if \(\$Stage -contains 'ArgumentSuffix'\) \{ Stage-ArgumentSuffix \}", source);
        Assert.DoesNotMatch(@"Want 'ArgumentSuffix'", source);
        // порядок: ArgumentSuffix перед OptionalArguments (той лишається останнім)
        Assert.True(
            source.IndexOf("Stage-ArgumentSuffix }", StringComparison.Ordinal)
            < source.IndexOf("Stage-OptionalArguments }", StringComparison.Ordinal));
    }
}
