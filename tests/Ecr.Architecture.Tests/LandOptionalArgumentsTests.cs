// tests/Ecr.Architecture.Tests/LandOptionalArgumentsTests.cs
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Binding;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// D-397 (клас O з D-8): аргументи, яких старий код не передає для частини таблиць методологій C11_10 / C11_11_03 / C11_16,
/// позначаються в ЧЕРНЕТЦІ маркером <c>?</c> у списку аргументів формул (<c>tools/land/LandOptionalArguments.Lib.ps1</c>, етап
/// <c>-Stage OptionalArguments</c>, перелік - <c>argumentBridges.optionalArguments</c> моделі). NULL-мости не будуються.
/// Функції й етап виконуються справжнім PowerShell із підставленими Api/Invoke-Ecr; розбір маркера - справжнім
/// <see cref="ArgumentDeclarationChecker"/>. Значень прода тут немає.
/// </summary>
/// <remarks>
/// Мутаційний доказ (лише цей клас): у <c>Convert-LandOptionalArgumentsCsv</c> прибрати додавання <c>?</c> - червоніють
/// тести бібліотеки й етапу; у <c>OPTIONAL_ARGUMENTS</c> додати пару C11_03/RunningHours - червоніє
/// <see cref="Перелік_моделі_лише_клас_O_без_Q_і_K_і_без_NULL_мостів"/>.
/// </remarks>
public sealed class LandOptionalArgumentsTests
{
    private static readonly string Dir = Path.Combine(
        SourceTree.Root, "docs", "delivery", "reference-data", "land-template");

    private static string BuildScript => Path.Combine(SourceTree.Root, "tools", "land", "Build-LandTemplate.ps1");

    private const string LibHarness = """
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $ErrorActionPreference = 'Stop'
        $root = $args[0]
        . (Join-Path $root 'tools\land\LandOptionalArguments.Lib.ps1')
        function Out($k, $o) { $j = if ($null -eq $o) { 'null' } else { $o | ConvertTo-Json -Compress -Depth 4 }; Write-Host ($k + '=' + $j) }
        $args1 = @('Land_Measure_OperatingTime', 'Land_Measure_SubstanceConsumption')
        Out 'R1' (Convert-LandOptionalArgumentsCsv -ArgumentsCsv '@Land_Measure_OperatingTime;@Density20;' -Arguments $args1)
        $r1 = Convert-LandOptionalArgumentsCsv -ArgumentsCsv '@Land_Measure_OperatingTime;@Density20;' -Arguments $args1
        Out 'R2' (Convert-LandOptionalArgumentsCsv -ArgumentsCsv $r1.ArgumentsCsv -Arguments $args1)
        Out 'R3' (Convert-LandOptionalArgumentsCsv -ArgumentsCsv $null -Arguments $args1)
        Out 'R4' (Convert-LandOptionalArgumentsCsv -ArgumentsCsv ' land_measure_operatingtime ; Land.Measure.SubstanceConsumption' -Arguments $args1)
        Out 'R5' (Convert-LandOptionalArgumentsCsv -ArgumentsCsv '@Land_Measure_OperatingTimeX;@Other;' -Arguments $args1)
        Out 'R6' (Convert-LandOptionalArgumentsCsv -ArgumentsCsv '' -Arguments $args1)
        """;

    private const string StageHarness = """
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $ErrorActionPreference = 'Stop'
        $script = $args[0]; $mode = $args[1]; $root = $args[2]
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$null)
        $fn = $ast.Find({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq 'Stage-OptionalArguments' }, $true)
        if (-not $fn) { throw 'Stage-OptionalArguments не знайдено' }
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
        function Formula($code, $expr, $args2) { return @{ code = $code; expression = $expr; argumentsCsv = $args2; resultType = 'Number'; outputUnitId = 5; scope = $null } }
        function Api {
            param([string]$Method, [string]$Path, $Payload = $null)
            if ($Path -eq '/api/v1/methodologies') { return @{ Json = @(@{ code = 'ECW_C11_10'; id = 7 }, @{ code = 'ECW_C11_03'; id = 9 }) } }
            if ($Path -like '*/methodologies/7/versions') {
                $st = if ($mode -eq 'published') { 'Published' } else { 'Draft' }
                return @{ Json = @(@{ id = 70; status = $st }) }
            }
            if ($Path -like '*/methodologies/9/versions') { throw 'C11_03 не з переліку: не мав читатися' }
            if ($Path -like '*/versions/70/formulas') {
                $marked = if ($mode -eq 'marked') { '@Land_Measure_OperatingTime?;@Land_Measure_SubstanceConsumption?;@Density20;' } else { '@Land_Measure_OperatingTime;@Land_Measure_SubstanceConsumption;@Density20;' }
                return @{ Json = @(
                    (Formula 'ECW_EC_tons_301' '@Land_Measure_OperatingTime * @Land_Measure_SubstanceConsumption * @Density20' $marked),
                    (Formula 'ECW_Category' "'A'" $null),
                    (Formula 'ECW_Other' '@Density20 * 2' '@Density20;')) }
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
        Stage-OptionalArguments
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

        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-optional-{Guid.NewGuid():N}.ps1");
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

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Маркер_ставиться_на_елемент_списку_повтор_не_змінює_і_null_лишається_null()
    {
        var r = Lib();

        Assert.True(r["R1"].GetProperty("Changed").GetBoolean());
        Assert.Equal("@Land_Measure_OperatingTime?;@Density20;", r["R1"].GetProperty("ArgumentsCsv").GetString());
        Assert.Equal(["Land_Measure_OperatingTime"], Strings(r["R1"].GetProperty("Marked")));
        Assert.Equal(["Land_Measure_SubstanceConsumption"], Strings(r["R1"].GetProperty("Missing")));

        // Повтор на вже позначеному - 0 змін.
        Assert.False(r["R2"].GetProperty("Changed").GetBoolean());
        Assert.Equal("@Land_Measure_OperatingTime?;@Density20;", r["R2"].GetProperty("ArgumentsCsv").GetString());

        // $null лишається $null: формула без списку не перевіряється, порожній рядок - «нуль аргументів».
        Assert.False(r["R3"].GetProperty("Changed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, r["R3"].GetProperty("ArgumentsCsv").ValueKind);

        // Без урахування регістру, пробіли, `.` = `_`; елементи без `@` теж позначаються.
        Assert.True(r["R4"].GetProperty("Changed").GetBoolean());
        Assert.Equal(
            "land_measure_operatingtime?;Land.Measure.SubstanceConsumption?;",
            r["R4"].GetProperty("ArgumentsCsv").GetString());

        // Префікс довшого імені не позначається.
        Assert.False(r["R5"].GetProperty("Changed").GetBoolean());
        Assert.Equal(2, Strings(r["R5"].GetProperty("Missing")).Length);

        Assert.False(r["R6"].GetProperty("Changed").GetBoolean());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Позначений_список_рушій_читає_як_необовязковий_а_решту_лишає_оголошеними()
    {
        var csv = Lib()["R1"].GetProperty("ArgumentsCsv").GetString()!;

        var declared = ArgumentDeclarationChecker.ParseDeclaration(csv);
        var optional = ArgumentDeclarationChecker.OptionalNames(declared);
        Assert.Equal(["Land_Measure_OperatingTime"], optional.ToArray());

        // Справжній розбір: токени виразу оголошені, `?` не робить ім'я «невживаним» і не дає 0432.
        var parsed = Expr.Parse("@Land_Measure_OperatingTime * @Density20", ExpressionDialect.Methodology);
        Assert.True(parsed.IsSuccess);
        var audit = ArgumentDeclarationChecker.Audit(parsed.Expression!.Root, declared);
        Assert.Empty(audit.Undeclared);
        Assert.Empty(audit.UnusedDeclared);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Перелік_моделі_лише_клас_O_без_Q_і_K_і_без_NULL_мостів()
    {
        using var model = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "land-template.model.json")));
        var bridges = model.RootElement.GetProperty("argumentBridges");
        var optional = bridges.GetProperty("optionalArguments").GetProperty("methodologies");

        var pairs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var m in optional.EnumerateObject())
        {
            foreach (var arg in m.Value.GetProperty("tables").EnumerateObject())
            {
                foreach (var table in arg.Value.EnumerateArray())
                {
                    pairs.Add($"{m.Name}|{table.GetString()}|{arg.Name}");
                }
            }
        }

        // Рівно клас O з LAND-D8-ARGS (#9-#16) + C11_10/T9_07 (після K1 суфікс C11_11_02 лишив пару [AF-немає]);
        // #17-#18 (C11_03 RunningHours, Q-D8-2) і решта класу K - не тут.
        Assert.Equal(
            [
                "ECW_C11_10|T9_02|Land_Measure_OperatingTime",
                "ECW_C11_10|T9_07|Land_Measure_SubstanceConsumption",
                "ECW_C11_10|T9_11|Land_Measure_OperatingTime",
                "ECW_C11_10|T9a_24|Land_Measure_OperatingTime",
                "ECW_C11_10|T9a_26|Land_Measure_OperatingTime",
                "ECW_C11_10|T9a_27|Land_Measure_SubstanceConsumption",
                "ECW_C11_10|T9a_28|Land_Measure_SubstanceConsumption",
                "ECW_C11_11_03|T9_06|Land_Measure_MaterialResidence",
                "ECW_C11_16|T9a_23|Land_Measure_FlowRateSulfur",
            ],
            pairs.ToArray());
        Assert.False(optional.TryGetProperty("ECW_C11_03", out _));

        var unresolved = bridges.GetProperty("unresolved");
        var tables = model.RootElement.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray())
            .GroupBy(t => t.GetProperty("code").GetString()!)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var key in pairs)
        {
            var part = key.Split('|');
            var (table, arg) = (part[1], part[2]);

            // Моста немає й не буде: аргумент названо в unresolved як «AF-немає», колонки з таким кодом у таблиці немає.
            Assert.StartsWith(
                "[AF-немає]",
                unresolved.GetProperty(table).GetProperty(arg).GetString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                tables[table].GetProperty("columns").EnumerateArray(),
                c => c.GetProperty("code").GetString() == arg);
        }

        // NULL-міст (Formula-колонка `NULL`) за LAND-D8-Q1 дав би #ARG, а не null: у моделі таких колонок немає.
        Assert.DoesNotContain(
            tables.Values.SelectMany(t => t.GetProperty("columns").EnumerateArray()),
            c => c.TryGetProperty("expression", out var e) && e.ValueKind == JsonValueKind.String
                 && string.Equals(e.GetString(), "NULL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Етап_позначає_лише_перелічені_аргументи_у_чернетці_і_повтор_нічого_не_міняє()
    {
        var (puts, output) = Stage("fresh");

        // Лише формула, що оголошує аргументи з переліку; ECW_Category (без списку) і ECW_Other не чіпаються.
        var put = Assert.Single(puts);
        Assert.EndsWith("/formulas/ECW_EC_tons_301", put.Path, StringComparison.Ordinal);
        Assert.Equal(
            "@Land_Measure_OperatingTime?;@Land_Measure_SubstanceConsumption?;@Density20;",
            put.Body.GetProperty("argumentsCsv").GetString());
        Assert.Equal(
            "@Land_Measure_OperatingTime * @Land_Measure_SubstanceConsumption * @Density20",
            put.Body.GetProperty("expression").GetString());
        Assert.DoesNotContain("ECW_C11_03", output, StringComparison.Ordinal);

        // Повторний запуск на вже позначеній чернетці - 0 змін.
        Assert.Empty(Stage("marked").Puts);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Етап_не_чіпає_опубліковану_версію()
    {
        var (puts, output) = Stage("published");

        Assert.Empty(puts);
        Assert.Contains("опублікована", output, StringComparison.Ordinal);
    }
}
