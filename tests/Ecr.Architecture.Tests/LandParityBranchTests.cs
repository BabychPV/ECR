// tests/Ecr.Architecture.Tests/LandParityBranchTests.cs
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// C12_02: вибір гілки type1/type2 за наявністю компонента (рішення координатора 13.10). Старий код рахує для компонента
/// лише type1, якщо значення є, інакше лише type2; в ECR обидві гілки - виходи, тож формула type2 обгортається
/// <c>if(@Land_Measure_Component_&lt;код&gt; = NULL, &lt;вираз&gt;, NULL)</c> (<c>tools/land/LandParityBranch.Lib.ps1</c>,
/// етап <c>-Stage ParityBranch</c>). Функції й етап виконуються справжнім PowerShell із підставленими Api/Invoke-Ecr;
/// текст обгортки обчислюється рушієм діалекту методологій. Значень прода тут немає.
/// </summary>
/// <remarks>
/// Мутаційний доказ (лише цей клас): у <c>Get-LandParityBranchComponent</c> прибрати перевірку списку компонентів
/// (415 потрапляє в обгортку) - червоніють <see cref="Код_415_і_type1_не_змінюються"/> та
/// <see cref="Етап_обгортає_лише_type2_компонентів_і_не_чіпає_415_та_type1"/>; з обгортки прибрати <c>, NULL)</c> -
/// червоніє <see cref="Обгортка_дає_порожній_вихід_за_наявного_компонента_і_вираз_без_нього"/>.
/// Маркер `?` (D-397; міст компонента не зберігає комірку за порожньої множини GTU, без `?` type2 = #ARG): з
/// <c>Convert-LandParityBranchFormula</c> прибрати дописування `?` - червоніють
/// <see cref="Компонент_аргументи_type2_отримують_маркер_необовязковості_а_чужі_елементи_цілі_і_повтор_нуль_змін"/>,
/// <see cref="Етап_лише_ставить_маркер_у_вже_обгорнутій_без_нього_формулі_і_не_повторює_обгортку"/> та перевірки
/// списку аргументів у перших двох тестах.
/// </remarks>
public sealed class LandParityBranchTests
{
    private static string BuildScript => Path.Combine(SourceTree.Root, "tools", "land", "Build-LandTemplate.ps1");

    private const string LibHarness = """
        $ErrorActionPreference = 'Stop'
        $root = $args[0]
        . (Join-Path $root 'tools\land\LandParityBranch.Lib.ps1')
        $plan = Get-LandParityBranchMethodologies
        function Out($k, $o) { $j = if ($null -eq $o) { 'null' } else { $o | ConvertTo-Json -Compress -Depth 4 }; Write-Host ($k + '=' + $j) }
        $comps = @($plan['ECW_C12_02'])
        Out 'COMPS' $comps
        Out 'C_T2' (Get-LandParityBranchComponent -FormulaCode 'ECW_EC_tons_301_type2' -Components $comps)
        Out 'C_G2' (Get-LandParityBranchComponent -FormulaCode 'ECW_EC_gsec_337_type2' -Components $comps)
        Out 'C_T1' (Get-LandParityBranchComponent -FormulaCode 'ECW_EC_tons_301_type1' -Components $comps)
        Out 'C_415' (Get-LandParityBranchComponent -FormulaCode 'ECW_EC_tons_415_type2' -Components $comps)
        Out 'C_OTHER' (Get-LandParityBranchComponent -FormulaCode 'ECW_Category' -Components $comps)
        $r = Convert-LandParityBranchFormula -Expression '@Land_Measure_GasConsumption * 2' -ArgumentsCsv '@Land_Measure_GasConsumption;' -Component '301'
        Out 'R1' $r
        $r2 = Convert-LandParityBranchFormula -Expression $r.Expression -ArgumentsCsv $r.ArgumentsCsv -Component '301'
        Out 'R2' $r2
        $r3 = Convert-LandParityBranchFormula -Expression '@Land_Measure_GasConsumption * 2' -ArgumentsCsv $null -Component '304'
        Out 'R3' $r3
        $r4 = Convert-LandParityBranchFormula -Expression '@Land_Measure_GasConsumption * 2' -ArgumentsCsv '@Land_Measure_GasConsumption;@Land_Measure_Component_330;' -Component '330'
        Out 'R4' $r4
        # Вже обгорнута, але без `?` (чернетка до D-397): лише маркер, обгортка не повторюється; чужі елементи цілі.
        $w = 'if(@Land_Measure_Component_304 = NULL, @Land_Measure_GasConsumption * 2, NULL)'
        $r5 = Convert-LandParityBranchFormula -Expression $w -ArgumentsCsv '@Land_Measure_GasConsumption;@Land_Measure_Component_304;@Land_Measure_Component_O2;@Land_Foreign?;' -Component '304'
        Out 'R5' $r5
        $r6 = Convert-LandParityBranchFormula -Expression $r5.Expression -ArgumentsCsv $r5.ArgumentsCsv -Component '304'
        Out 'R6' $r6
        $r7 = Convert-LandParityBranchFormula -Expression '@Land_Measure_GasConsumption * 2' -ArgumentsCsv '@Land_Measure_GasConsumption;@Land_Measure_Component_337?;' -Component '337'
        Out 'R7' $r7
        Out 'N_T1' (Get-LandParityBranchNullType1Component -FormulaCode 'ECW_EC_tons_415_type1' -Components @('415'))
        Out 'N_G1' (Get-LandParityBranchNullType1Component -FormulaCode 'ECW_EC_gsec_415_type1' -Components @('415'))
        Out 'N_T2' (Get-LandParityBranchNullType1Component -FormulaCode 'ECW_EC_tons_415_type2' -Components @('415'))
        Out 'N_301' (Get-LandParityBranchNullType1Component -FormulaCode 'ECW_EC_tons_301_type1' -Components @('415'))
        Out 'N_R1' (Convert-LandParityBranchNullFormula -Expression '@Land_Measure_GasConsumption * 7')
        Out 'N_R2' (Convert-LandParityBranchNullFormula -Expression ' null ')
        """;

    private const string StageHarness = """
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $ErrorActionPreference = 'Stop'
        $script = $args[0]; $mode = $args[1]; $root = $args[2]
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$null)
        $fn = $ast.Find({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq 'Stage-ParityBranch' }, $true)
        if (-not $fn) { throw 'Stage-ParityBranch не знайдено' }
        $here = Join-Path $root 'tools\land'
        function Log([string]$m) { Write-Host ('LOG=' + $m) }
        function LoginAs([string]$u) { }
        function Get-EcrErrorCode($r) { return 'ECR-X' }
        function Items($j) {
            if ($null -eq $j) { return @() }
            if ($j -is [array]) { return $j }
            if ($j.PSObject.Properties['items']) { return @($j.items) }
            return @($j)
        }
        $wrapped = "if(@Land_Measure_Component_301 = NULL, @Land_Measure_GasConsumption * 2, NULL)"
        function Formula($code, $expr, $args2) { return @{ code = $code; expression = $expr; argumentsCsv = $args2; resultType = 'Number'; outputUnitId = 5; scope = $null } }
        $extra = @()
        if ($mode -eq 'fresh415' -or $mode -eq 'nulled415') {
            $x415 = if ($mode -eq 'nulled415') { 'NULL' } else { '@Land_Measure_GasConsumption * 7' }
            $extra = @(
                (Formula 'ECW_EC_tons_415_type1' $x415 '@Land_Measure_GasConsumption;'),
                (Formula 'ECW_EC_gsec_415_type1' $x415 '@Land_Measure_GasConsumption;'),
                (Formula 'ECW_EC_tons_304_type1' '@Land_Measure_Component_304 * 2' '@Land_Measure_Component_304;'))
        }
        function Api {
            param([string]$Method, [string]$Path, $Payload = $null)
            if ($Path -eq '/api/v1/methodologies') { return @{ Json = @(@{ code = 'ECW_C12_02'; id = 7 }, @{ code = 'ECW_C11_01'; id = 8 }) } }
            if ($Path -like '*/methodologies/7/versions') {
                $st = if ($mode -eq 'published') { 'Published' } else { 'Draft' }
                return @{ Json = @(@{ id = 70; status = $st }) }
            }
            if ($Path -like '*/versions/70/formulas') {
                $t2 = if ($mode -eq 'wrapped' -or $mode -eq 'wrappedUnmarked') { $wrapped } else { '@Land_Measure_GasConsumption * 2' }
                $a2 = if ($mode -eq 'wrapped') { '@Land_Measure_GasConsumption;@Land_Measure_Component_301?;' } elseif ($mode -eq 'wrappedUnmarked') { '@Land_Measure_GasConsumption;@Land_Measure_Component_301;' } else { '@Land_Measure_GasConsumption;' }
                return @{ Json = @(
                    (Formula 'ECW_EC_tons_301_type1' '@Land_Measure_Component_301 * 2' '@Land_Measure_Component_301;'),
                    (Formula 'ECW_EC_tons_301_type2' $t2 $a2),
                    (Formula 'ECW_EC_gsec_337_type2' '@Land_Measure_GasConsumption / 3' '@Land_Measure_GasConsumption;'),
                    (Formula 'ECW_EC_tons_415_type2' '@Land_Measure_GasConsumption * 7' '@Land_Measure_GasConsumption;'),
                    (Formula 'ECW_Category' "'A'" $null)) + $extra }
            }
            throw "Api: неочікуваний шлях $Method $Path"
        }
        function Invoke-Ecr {
            param([string]$Method, [string]$Path, $Body = $null)
            if ($Method -eq 'PUT' -and $Path -like '*/versions/70/formulas/*') {
                Write-Host ('PUT=' + $Path + '|' + ($Body | ConvertTo-Json -Compress))
                return @{ Status = 200; Text = ''; Json = $null }
            }
            throw "Invoke-Ecr: неочікуваний виклик $Method $Path"
        }
        . ([scriptblock]::Create($fn.Extent.Text))
        Stage-ParityBranch
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

        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-branch-{Guid.NewGuid():N}.ps1");
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

    private static List<(string Path, JsonElement Body)> Stage(string mode, out string output)
    {
        var (exit, text) = RunPowerShell(StageHarness, BuildScript, mode, SourceTree.Root);
        Assert.True(exit == 0, text);
        output = text;
        var puts = new List<(string, JsonElement)>();
        foreach (var line in text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("PUT=", StringComparison.Ordinal)))
        {
            var bar = line.IndexOf('|', StringComparison.Ordinal);
            puts.Add((line[4..bar], JsonDocument.Parse(line[(bar + 1)..]).RootElement.Clone()));
        }

        return puts;
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Тип2_компонентів_обгортається_а_повтор_не_змінює()
    {
        var r = Lib();

        Assert.Equal(["301", "304", "330", "337"], r["COMPS"].EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal("301", r["C_T2"].GetString());
        Assert.Equal("337", r["C_G2"].GetString());

        Assert.True(r["R1"].GetProperty("Changed").GetBoolean());
        Assert.Equal(
            "if(@Land_Measure_Component_301 = NULL, @Land_Measure_GasConsumption * 2, NULL)",
            r["R1"].GetProperty("Expression").GetString());
        // D-397: аргумент-компонент необов'язковий (`?`): міст GTU не зберігає комірку, коли рядка-джерела немає.
        Assert.Equal("@Land_Measure_GasConsumption;@Land_Measure_Component_301?;", r["R1"].GetProperty("ArgumentsCsv").GetString());

        // Повтор на вже обгорнутій формулі - 0 змін.
        Assert.False(r["R2"].GetProperty("Changed").GetBoolean());
        Assert.Equal(r["R1"].GetProperty("Expression").GetString(), r["R2"].GetProperty("Expression").GetString());

        // Формула без списку аргументів: список лишається null (а не порожнім рядком - ECR-CALC-0432).
        Assert.True(r["R3"].GetProperty("Changed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, r["R3"].GetProperty("ArgumentsCsv").ValueKind);

        // Аргумент уже в списку - не дублюється, лише отримує маркер `?`.
        Assert.Equal(
            "@Land_Measure_GasConsumption;@Land_Measure_Component_330?;",
            r["R4"].GetProperty("ArgumentsCsv").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Компонент_аргументи_type2_отримують_маркер_необовязковості_а_чужі_елементи_цілі_і_повтор_нуль_змін()
    {
        var r = Lib();

        // Уже обгорнута без `?`: обгортка та сама, лише маркер; усі Land_Measure_Component_* позначені, чужі - ні.
        Assert.True(r["R5"].GetProperty("Changed").GetBoolean());
        Assert.Equal(
            "if(@Land_Measure_Component_304 = NULL, @Land_Measure_GasConsumption * 2, NULL)",
            r["R5"].GetProperty("Expression").GetString());
        Assert.Equal(
            "@Land_Measure_GasConsumption;@Land_Measure_Component_304?;@Land_Measure_Component_O2?;@Land_Foreign?;",
            r["R5"].GetProperty("ArgumentsCsv").GetString());

        // Повтор - 0 змін.
        Assert.False(r["R6"].GetProperty("Changed").GetBoolean());
        Assert.Equal(r["R5"].GetProperty("ArgumentsCsv").GetString(), r["R6"].GetProperty("ArgumentsCsv").GetString());

        // Не обгорнута, маркер уже є: обгортається, `?` не дублюється.
        Assert.True(r["R7"].GetProperty("Changed").GetBoolean());
        Assert.Equal(
            "@Land_Measure_GasConsumption;@Land_Measure_Component_337?;",
            r["R7"].GetProperty("ArgumentsCsv").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Етап_лише_ставить_маркер_у_вже_обгорнутій_без_нього_формулі_і_не_повторює_обгортку()
    {
        var puts = Stage("wrappedUnmarked", out var output);

        // 301_type2 уже обгорнута, але без `?` - ОДИН PUT з тим самим виразом і маркером; 337 - обгортається повністю.
        var tons = puts.Single(p => p.Path.EndsWith("ECW_EC_tons_301_type2", StringComparison.Ordinal)).Body;
        Assert.Equal(
            "if(@Land_Measure_Component_301 = NULL, @Land_Measure_GasConsumption * 2, NULL)",
            tons.GetProperty("expression").GetString());
        Assert.Equal("@Land_Measure_GasConsumption;@Land_Measure_Component_301?;", tons.GetProperty("argumentsCsv").GetString());
        Assert.Equal(2, puts.Count);

        // type1 компонента й код 415 маркера не отримують: їхні формули не пишуться.
        Assert.DoesNotContain(puts, p => p.Path.EndsWith("_type1", StringComparison.Ordinal));
        Assert.DoesNotContain(puts, p => p.Path.Contains("_415_", StringComparison.Ordinal));
        Assert.Contains("змінено формул: 2", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Код_415_і_type1_не_змінюються()
    {
        var r = Lib();

        Assert.Equal(JsonValueKind.Null, r["C_T1"].ValueKind);
        Assert.Equal(JsonValueKind.Null, r["C_415"].ValueKind);
        Assert.Equal(JsonValueKind.Null, r["C_OTHER"].ValueKind);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Обгортка_дає_порожній_вихід_за_наявного_компонента_і_вираз_без_нього()
    {
        var wrapped = Lib()["R1"].GetProperty("Expression").GetString()!;

        static ExpressionValue Eval(string expression, ExpressionValue? component)
        {
            var context = new TestEvaluationContext();
            context.Arguments["Land_Measure_GasConsumption"] = ExpressionValue.Number(5m);
            if (component is { } value)
            {
                context.Arguments["Land_Measure_Component_301"] = value;
            }

            return Expr.Eval(expression, context, ExpressionDialect.Methodology);
        }

        // Компонента немає (клітинка порожня -> аргумент null): type2 рахується, як без обгортки.
        var plain = Eval("@Land_Measure_GasConsumption * 2", ExpressionValue.Null);
        Assert.Equal(10m, plain.AsNumber());
        Assert.Equal(10m, Eval(wrapped, ExpressionValue.Null).AsNumber());

        // Компонент має значення (навіть 0): type2 порожній - вийде лише type1.
        Assert.True(Eval(wrapped, ExpressionValue.Number(3m)).IsNull);
        Assert.True(Eval(wrapped, ExpressionValue.Number(0m)).IsNull);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Етап_обгортає_лише_type2_компонентів_і_не_чіпає_415_та_type1()
    {
        var puts = Stage("fresh", out var output);

        Assert.Equal(
            ["ECW_EC_gsec_337_type2", "ECW_EC_tons_301_type2"],
            puts.Select(p => p.Path[(p.Path.LastIndexOf('/') + 1)..]).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.All(puts, p => Assert.Contains("/methodologies/7/versions/70/formulas/", p.Path, StringComparison.Ordinal));

        var tons = puts.Single(p => p.Path.EndsWith("ECW_EC_tons_301_type2", StringComparison.Ordinal)).Body;
        Assert.Equal(
            "if(@Land_Measure_Component_301 = NULL, @Land_Measure_GasConsumption * 2, NULL)",
            tons.GetProperty("expression").GetString());
        Assert.Equal("@Land_Measure_GasConsumption;@Land_Measure_Component_301?;", tons.GetProperty("argumentsCsv").GetString());
        Assert.Equal(5, tons.GetProperty("outputUnitId").GetInt32());

        var gsec = puts.Single(p => p.Path.EndsWith("ECW_EC_gsec_337_type2", StringComparison.Ordinal)).Body;
        Assert.StartsWith("if(@Land_Measure_Component_337 = NULL, ", gsec.GetProperty("expression").GetString(), StringComparison.Ordinal);
        Assert.Contains("змінено формул: 2", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Повторний_запуск_етапу_дає_нуль_змін_а_опубліковану_версію_етап_не_чіпає()
    {
        var wrapped = Stage("wrapped", out var output);
        // ECW_EC_tons_301_type2 уже обгорнута; решта type2 (337) - ще ні.
        Assert.Single(wrapped);
        Assert.EndsWith("ECW_EC_gsec_337_type2", wrapped[0].Path, StringComparison.Ordinal);
        Assert.Contains("змінено формул: 1", output, StringComparison.Ordinal);

        var published = Stage("published", out var publishedOutput);
        Assert.Empty(published);
        Assert.Contains("остання версія опублікована", publishedOutput, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Етап_лише_явний_у_All_не_входить_і_не_публікує()
    {
        var text = File.ReadAllText(BuildScript);

        Assert.Matches(@"ValidateSet\([^)]*'ParityBranch'[^)]*\)", text);
        Assert.Matches(@"if \(\$Stage -contains 'ParityBranch'\) \{ Stage-ParityBranch \}", text);
        Assert.DoesNotContain("Want 'ParityBranch'", text, StringComparison.Ordinal);

        // Тіло етапу: чернетка, без публікації/нової версії.
        var body = Regex.Match(text, @"function Stage-ParityBranch \{.*?\r?\n\}\r?\n", RegexOptions.Singleline).Value;
        Assert.NotEmpty(body);
        Assert.DoesNotContain("/publish", body, StringComparison.Ordinal);
        Assert.DoesNotContain("-Method POST", body, StringComparison.Ordinal);
        Assert.Contains("опублікована - не чіпаю", body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Type1_коду_415_стає_NULL_а_type2_і_type1_інших_кодів_не_змінюються()
    {
        var r = Lib();

        // tons і gsec type1 коду 415 розпізнаються; type2 коду 415 і type1 іншого коду - ні.
        Assert.Equal("415", r["N_T1"].GetString());
        Assert.Equal("415", r["N_G1"].GetString());
        Assert.Equal(JsonValueKind.Null, r["N_T2"].ValueKind);
        Assert.Equal(JsonValueKind.Null, r["N_301"].ValueKind);

        Assert.True(r["N_R1"].GetProperty("Changed").GetBoolean());
        Assert.Equal("NULL", r["N_R1"].GetProperty("Expression").GetString());

        // Уже NULL (будь-який регістр, з пробілами) - 0 змін.
        Assert.False(r["N_R2"].GetProperty("Changed").GetBoolean());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Етап_ставить_NULL_лише_у_type1_коду_415_не_чіпаючи_аргументи_type2_та_інші_type1()
    {
        var puts = Stage("fresh415", out var output);

        Assert.Equal(
            ["ECW_EC_gsec_337_type2", "ECW_EC_gsec_415_type1", "ECW_EC_tons_301_type2", "ECW_EC_tons_415_type1"],
            puts.Select(p => p.Path[(p.Path.LastIndexOf('/') + 1)..]).OrderBy(x => x, StringComparer.Ordinal).ToArray());

        foreach (var put in puts.Where(p => p.Path.EndsWith("_415_type1", StringComparison.Ordinal)))
        {
            Assert.Equal("NULL", put.Body.GetProperty("expression").GetString());
            Assert.Equal("@Land_Measure_GasConsumption;", put.Body.GetProperty("argumentsCsv").GetString());
            Assert.Equal(5, put.Body.GetProperty("outputUnitId").GetInt32());
        }

        Assert.Contains("змінено формул: 4", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Повторний_запуск_етапу_не_пише_type1_коду_415_якщо_там_уже_NULL()
    {
        var puts = Stage("nulled415", out var output);

        Assert.DoesNotContain(puts, p => p.Path.Contains("_415_", StringComparison.Ordinal));
        Assert.Equal(
            ["ECW_EC_gsec_337_type2", "ECW_EC_tons_301_type2"],
            puts.Select(p => p.Path[(p.Path.LastIndexOf('/') + 1)..]).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Contains("type1 без числа (код 415, вираз NULL) - 0", output, StringComparison.Ordinal);
    }
}
