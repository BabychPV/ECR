using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Застосовність речовин на рядок (рішення людини 13.10 «2»): довідник <c>LAND_SUBSTANCE_APPLICABILITY</c> у моделі
/// Land, його відповідність словникам старого C# (значення нижче скопійовано з <c>ECW_C11_*.cs</c> /
/// <c>ECW_C12_*.cs</c>, не з прода), обгортка правила категорії й етап <c>SubstanceApplicability</c>.
/// </summary>
/// <remarks>
/// Мутаційні докази (лише цей клас): у <c>substance_applicability.py</c> прибрати METHODOLOGY з ключа запису чи
/// змінити <c>_provider_prefix</c> й перегенерувати модель — червоніють
/// <see cref="Різні_методології_мають_різні_набори_для_одного_тексту_потоку"/> і
/// <see cref="Словники_C_sharp_відтворені_побітно_у_довіднику"/>; у <c>Get-LandSubstanceApplicabilityInnerRule</c>
/// ігнорувати рядкові літерали при пошуку парної дужки — червоніє
/// <see cref="Обгортка_правила_відновлюється_до_вихідного_а_повторна_не_ставиться"/>.
/// </remarks>
public sealed class LandSubstanceApplicabilityTests
{
    private static readonly string Dir = Path.Combine(
        SourceTree.Root, "docs", "delivery", "reference-data", "land-template");

    private static readonly string[] KeyFields = ["METHODOLOGY", "SELECTOR_KIND", "SELECTOR_VALUE", "SUBSTANCE_CODE"];

    private static readonly string[] AllowedKinds = ["NAE", "InvNum", "TypeFuel", "Stream"];

    private static string LandFile(string name) => Path.Combine(SourceTree.Root, "tools", "land", name);

    private static JsonDocument LoadModel()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "land-template.model.json")));

    // Рядки довідника: (методологія, вид) -> значення селектора -> коди речовин.
    private static Dictionary<string, Dictionary<string, List<string>>> Rows(JsonDocument model, out HashSet<string> markers)
    {
        var registry = model.RootElement.GetProperty("registries").EnumerateArray()
            .Single(r => r.GetProperty("code").GetString() == "LAND_SUBSTANCE_APPLICABILITY");
        var result = new Dictionary<string, Dictionary<string, List<string>>>();
        markers = [];
        foreach (var entry in registry.GetProperty("entries").EnumerateArray())
        {
            var f = entry.GetProperty("fields");
            var methodology = f.GetProperty("METHODOLOGY").GetString()!;
            var kind = f.GetProperty("SELECTOR_KIND").GetString()!;
            var value = f.GetProperty("SELECTOR_VALUE").GetString()!;
            var code = f.GetProperty("SUBSTANCE_CODE").GetString()!;
            if (kind == "*")
            {
                Assert.Equal("*", value);
                Assert.Equal("*", code);
                markers.Add(methodology);
                continue;
            }

            if (!result.TryGetValue(methodology, out var byValue))
            {
                result[methodology] = byValue = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            }

            if (!byValue.TryGetValue(value, out var codes))
            {
                byValue[value] = codes = [];
            }

            codes.Add(code);
        }

        return result;
    }

    private static string[] Codes(Dictionary<string, Dictionary<string, List<string>>> rows, string methodology, string value)
        => rows.TryGetValue(methodology, out var byValue) && byValue.TryGetValue(value, out var codes)
            ? [.. codes.Order(StringComparer.Ordinal)]
            : [];

    private static string[] Sorted(params string[] codes) => [.. codes.Order(StringComparer.Ordinal)];

    // Списки кодів старого C# (pollutionCodeByFuelType; C11_01 / C11_02 / C11_10 / C12_03 - по методологіях).
    private static readonly string[] FgMakat = ["333", "415", "416", "1728"];

    private static readonly string[] FgInternal =
        ["333", "370", "415", "416", "602", "616", "621", "1702", "1707", "1715", "1720", "1728", "2754"];

    private static readonly string[] FgHmb =
        ["333", "334", "370", "415", "416", "602", "616", "621", "627", "1702", "1715", "1720", "1728", "2754"];

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Довідник_має_складений_ключ_з_чотирьох_частин_а_коди_записів_унікальні()
    {
        using var model = LoadModel();
        var registry = model.RootElement.GetProperty("registries").EnumerateArray()
            .Single(r => r.GetProperty("code").GetString() == "LAND_SUBSTANCE_APPLICABILITY");

        var key = Assert.Single(registry.GetProperty("keys").EnumerateArray());
        Assert.True(key.GetProperty("isPrimary").GetBoolean());
        // Регістр значущий: старий C# порівнює рядки ординально; METHODOLOGY - частина ключа.
        Assert.False(key.GetProperty("ignoreCase").GetBoolean());
        Assert.Equal(
            ["METHODOLOGY", "SELECTOR_KIND", "SELECTOR_VALUE", "SUBSTANCE_CODE"],
            key.GetProperty("fields").EnumerateArray().Select(f => f.GetString()!).ToArray());
        Assert.All(
            registry.GetProperty("fieldSpecs").EnumerateArray(),
            f => Assert.Equal("String", f.GetProperty("dataType").GetString()));

        var codes = registry.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("code").GetString()!).ToList();
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(codes, c => Assert.Matches("^[A-Z][A-Z0-9]{10}$", c));

        // Складені ключі записів теж унікальні (інакше PUT довідника відхилив би дублікат).
        var keys = registry.GetProperty("entries").EnumerateArray()
            .Select(e => string.Join('|', KeyFields
                .Select(n => e.GetProperty("fields").GetProperty(n).GetString())))
            .ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожна_методологія_з_описом_має_маркер_і_записи_а_вид_селектора_з_допустимих()
    {
        using var model = LoadModel();
        var rows = Rows(model, out var markers);
        var description = model.RootElement.GetProperty("substanceApplicability");
        var described = description.GetProperty("methodologies").EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

        Assert.Equal(
            ["ECW_C11_01", "ECW_C11_02", "ECW_C11_10", "ECW_C11_11_02", "ECW_C11_11_03", "ECW_C11_11_05", "ECW_C11_19",
                "ECW_C12_01", "ECW_C12_03", "ECW_C12_04_01", "ECW_C12_04_02"],
            described.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(described.Keys.Order().ToArray(), markers.Order().ToArray());
        Assert.Equal(described.Keys.Order().ToArray(), rows.Keys.Order().ToArray());

        var kinds = described.ToDictionary(p => p.Key, p => p.Value.GetProperty("kind").GetString());
        Assert.Equal("Stream", kinds["ECW_C11_01"]);
        Assert.Equal("Stream", kinds["ECW_C12_01"]);
        Assert.Equal("InvNum", kinds["ECW_C12_04_01"]);
        Assert.Equal("TypeFuel", kinds["ECW_C12_03"]);
        Assert.Equal("TypeFuel", kinds["ECW_C12_04_02"]);
        Assert.Equal("NAE", kinds["ECW_C11_11_03"]);
        Assert.Equal("NAE", kinds["ECW_C11_11_02"]);
        Assert.Equal("@Land_NAE", described["ECW_C11_11_02"].GetProperty("valueExpression").GetString());
        Assert.All(kinds.Values, k => Assert.Contains(k, AllowedKinds));

        // Значення селектора у правилі - те саме, що читає правило категорії в Row-фазі.
        Assert.Equal("@Land_TypeFuelOrStream", described["ECW_C11_02"].GetProperty("valueExpression").GetString());
        Assert.Equal("@Land_Streams", described["ECW_C12_01"].GetProperty("valueExpression").GetString());
        Assert.Equal("@Land_InvNumEqp", described["ECW_C12_04_01"].GetProperty("valueExpression").GetString());
        Assert.Equal("@Land_NAE", described["ECW_C11_19"].GetProperty("valueExpression").GetString());
        Assert.Equal("REGFIELD(@Land_TypeFuel, 'NAME')", described["ECW_C12_03"].GetProperty("valueExpression").GetString());
        Assert.Matches(
            @"^if\(in\(@Land_TypeStoredMaterial,'Diesel - Дизель','Diesel - Дизельное топливо'\),'diesel','oil'\)$",
            described["ECW_C12_04_02"].GetProperty("valueExpression").GetString()!);
        Assert.Equal("SUBSTANCE_APPLICABLE", description.GetProperty("function").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Різні_методології_мають_різні_набори_для_одного_тексту_потоку()
    {
        using var model = LoadModel();
        var rows = Rows(model, out _);

        const string internalLpg = "LPG (Internal) - СУГ (Внутренний)";
        const string diesel = "Diesel - Дизель";

        // C11_01: LPG (Internal) без 1707; C11_02/C11_10: з 1707 (різні словники в старому коді).
        Assert.Equal(Sorted("333", "370", "415", "416", "602", "616", "621", "1702", "1715", "1720", "1728", "2754"),
            Codes(rows, "ECW_C11_01", internalLpg));
        Assert.Equal(Sorted(FgInternal), Codes(rows, "ECW_C11_02", internalLpg));
        Assert.Equal(Sorted(FgInternal), Codes(rows, "ECW_C11_10", internalLpg));

        // Diesel - лише C11_01 (ланцюг streamfuel); у C11_02 / C11_10 / C12_03 префікса немає - речовин немає.
        Assert.Equal(Sorted("333", "2754"), Codes(rows, "ECW_C11_01", diesel));
        Assert.Empty(Codes(rows, "ECW_C11_02", diesel));
        Assert.Empty(Codes(rows, "ECW_C11_10", diesel));
        Assert.Empty(Codes(rows, "ECW_C12_03", diesel));

        // C12_03: словник лише газу FG_*; LPG у ньому немає (KeyNotFound у старому коді) - речовин немає.
        Assert.Empty(Codes(rows, "ECW_C12_03", "LPG (HMB) - СУГ (МТБ)"));
        Assert.Equal(Sorted(FgHmb), Codes(rows, "ECW_C12_03", "Fuel gas (HMB) - Топливный газ (МТБ)"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Словники_C_sharp_відтворені_побітно_у_довіднику()
    {
        using var model = LoadModel();
        var rows = Rows(model, out _);

        // C11_01 / C11_02 / C11_10: pollutionCodeByFuelType + PrefixProvider.
        Assert.Equal(Sorted(FgMakat), Codes(rows, "ECW_C11_01", "Makat Gas - Макатский газ"));
        Assert.Equal(Sorted(FgHmb), Codes(rows, "ECW_C11_01", "LPG (HMB) - СУГ (МТБ)"));
        Assert.Equal(Sorted(FgInternal), Codes(rows, "ECW_C11_01", "Fuel gas (Internal) - Топливный газ (Внутренний)"));
        Assert.Equal(Sorted("333", "1052", "2754"), Codes(rows, "ECW_C11_02", "1-204 sludge - 1-204 шлам"));
        Assert.Equal(Sorted("333", "2754"), Codes(rows, "ECW_C11_02", "spent caustic - отработанный каустик"));
        Assert.Equal(Sorted("333", "334", "370", "415", "1715", "1728"), Codes(rows, "ECW_C11_10", "104"));
        Assert.Empty(Codes(rows, "ECW_C11_01", "LPG - СУГ"));   // токен LPG без префікса: у словнику немає

        // C12_01: typeOfStreamsInRDS + polCodeList; Asphaltene inhibitor у polCodeList закоментований.
        Assert.Equal(Sorted(FgInternal), Codes(rows, "ECW_C12_01", "Fuel gas"));
        Assert.Equal(Sorted(FgHmb), Codes(rows, "ECW_C12_01", "1-D1"));
        Assert.Equal(Sorted(FgHmb), Codes(rows, "ECW_C12_01", "1D-1"));
        Assert.Equal(Sorted("333", "2754"), Codes(rows, "ECW_C12_01", "Diesel/arctic diesel"));
        Assert.Equal(Sorted("616", "621", "2750"), Codes(rows, "ECW_C12_01", "Wax inhibitor"));
        Assert.Empty(Codes(rows, "ECW_C12_01", "Asphaltene inhibitor"));

        // C12_04_01: Т/А кирилиця -> латиниця; кожна літера окремо.
        var cyrillic = "B4-120-ТА-101";
        Assert.Equal(Sorted("1001", "1051", "1052", "1860"), Codes(rows, "ECW_C12_04_01", "B4-120-TA-101"));
        Assert.Equal(Sorted("1001", "1051", "1052", "1860"), Codes(rows, "ECW_C12_04_01", cyrillic));
        Assert.Equal(Sorted("1001", "1051", "1052", "1860"), Codes(rows, "ECW_C12_04_01", "B4-120-TА-101"));
        Assert.Equal(Sorted("2853"), Codes(rows, "ECW_C12_04_01", "B4-120-ТC-401"));
        Assert.Equal(Sorted("1023", "1052"), Codes(rows, "ECW_C12_04_01", "B4-380-VА-305"));
        Assert.Equal(32, rows["ECW_C12_04_01"].Count);
        Assert.Empty(Codes(rows, "ECW_C12_04_01", "B4-120-TA-999"));

        // C12_04_02: Contains("Diesel") -> diesel / oil.
        Assert.Equal(Sorted("333", "2754"), Codes(rows, "ECW_C12_04_02", "diesel"));
        Assert.Equal(Sorted("2735"), Codes(rows, "ECW_C12_04_02", "oil"));

        // NAE-словники: канонічне написання й `NNNN (NNN)` з пробілом.
        Assert.Equal(Sorted("333", "2754"), Codes(rows, "ECW_C11_11_03", "0800(001)"));
        Assert.Equal(Sorted("333", "2754"), Codes(rows, "ECW_C11_11_03", "0800 (001)"));
        Assert.Equal(Sorted("333", "1052", "2754"), Codes(rows, "ECW_C11_11_03", "0782(008)"));
        Assert.Equal(Sorted("333", "1052", "2754"), Codes(rows, "ECW_C11_11_03", "0804"));
        // C11_11_02: NAE -> група groupNAE -> коди EC (ECW_C11_11_02.cs): група 6975 тягне 6981..6986, 0562 - 0563.
        Assert.Equal(Sorted("333", "1052", "2754"), Codes(rows, "ECW_C11_11_02", "0788"));
        Assert.Equal(Sorted("333", "1052", "2754"), Codes(rows, "ECW_C11_11_02", "0791(001)"));
        Assert.Equal(Sorted("333", "1052", "2754"), Codes(rows, "ECW_C11_11_02", "0791 (001)"));
        Assert.Equal(Sorted("333", "2754"), Codes(rows, "ECW_C11_11_02", "6981"));
        Assert.Equal(Sorted("333", "2754"), Codes(rows, "ECW_C11_11_02", "6976"));
        Assert.Equal(Sorted("1078"), Codes(rows, "ECW_C11_11_02", "0563"));
        Assert.Equal(Sorted("1078"), Codes(rows, "ECW_C11_11_02", "6904"));
        Assert.Equal(Sorted("322"), Codes(rows, "ECW_C11_11_02", "0883"));
        Assert.Empty(Codes(rows, "ECW_C11_11_02", "0804"));   // NAE поза groupNAE: «Error category - NAE», рядок пропущено
        Assert.Equal(Sorted("330", "333"), Codes(rows, "ECW_C11_11_05", "0483"));
        Assert.Equal(Sorted("333"), Codes(rows, "ECW_C11_11_05", "0480"));
        Assert.Equal(Sorted("333"), Codes(rows, "ECW_C11_11_05", "6482 (001)"));
        Assert.Equal(Sorted("330", "331", "333"), Codes(rows, "ECW_C11_19", "0485"));
        Assert.Equal(Sorted("333"), Codes(rows, "ECW_C11_19", "0484"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Файл_словників_і_модель_узгоджені_а_значень_прода_у_файлі_немає()
    {
        var specPath = Path.Combine(Dir, "land-substance-applicability.json");
        using var spec = JsonDocument.Parse(File.ReadAllText(specPath));
        using var model = LoadModel();
        _ = Rows(model, out var markers);

        var names = spec.RootElement.GetProperty("methodologies").EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(markers.Order().ToArray(), names);

        // Кожен код речовини у файлі - число 3-4 цифри (як у PollutionCodeService / AF_PermitConstant).
        foreach (var m in spec.RootElement.GetProperty("methodologies").EnumerateObject())
        {
            foreach (var codes in m.Value.GetProperty("codes").EnumerateObject())
            {
                Assert.All(codes.Value.EnumerateArray(), c => Assert.Matches("^[1-9][0-9]{2,3}$", c.GetString()!));
            }

            Assert.StartsWith("docs/source/ECR Current Back end/", m.Value.GetProperty("source").GetString(), StringComparison.Ordinal);
        }

        // Скрипт генерації детермінований: той самий вхід -> побітно той самий файл (кінець рядка LF, без часу).
        var text = File.ReadAllText(specPath);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"20\d\d-\d\d-\d\dT\d\d:", text);
    }

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

        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-appl-{Guid.NewGuid():N}.ps1");
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
    public void Обгортка_правила_відновлюється_до_вихідного_а_повторна_не_ставиться()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            $reg = 'LAND_SUBSTANCE_APPLICABILITY'
            $inner = "if(in(@Land_InvNumEqp,'B4-120-TA-101'),'B4-120-TA-101','')"
            $w = New-LandSubstanceApplicabilityRule -Inner $inner -Registry $reg -Methodology 'ECW_C12_04_01' -Kind 'InvNum' -ValueExpression '@Land_InvNumEqp'
            'WRAPPED=' + $w
            'ROUNDTRIP=' + ((Get-LandSubstanceApplicabilityInnerRule -Rule $w) -ceq $inner)
            'IS_WRAPPED=' + (Test-LandSubstanceApplicabilityRule -Rule $w)
            'IS_WRAPPED_BASE=' + (Test-LandSubstanceApplicabilityRule -Rule $inner)
            'MATCH_DIRECT=' + (Test-LandRuleMatchesBaseRule -Existing $inner -Base $inner)
            'MATCH_WRAPPED=' + (Test-LandRuleMatchesBaseRule -Existing $w -Base $inner)
            'MATCH_OTHER=' + (Test-LandRuleMatchesBaseRule -Existing $w -Base "@Land_NAE")
            # Значення селектора з дужками, комами й літералами: парна дужка не плутається з рядками.
            $v = "if(in(@Land_TypeStoredMaterial,'Diesel - Дизель','a),b'),'diesel','oil')"
            $w2 = New-LandSubstanceApplicabilityRule -Inner "@Land_NAE" -Registry $reg -Methodology 'ECW_C12_04_02' -Kind 'TypeFuel' -ValueExpression $v
            'ROUNDTRIP2=' + ((Get-LandSubstanceApplicabilityInnerRule -Rule $w2) -ceq '@Land_NAE')
            $w3 = New-LandSubstanceApplicabilityRule -Inner $inner -Registry $reg -Methodology 'ECW_C12_03' -Kind 'TypeFuel' -ValueExpression "REGFIELD(@Land_TypeFuel, 'NAME')"
            'ROUNDTRIP3=' + ((Get-LandSubstanceApplicabilityInnerRule -Rule $w3) -ceq $inner)
            'NOT_WRAPPER=' + ($null -eq (Get-LandSubstanceApplicabilityInnerRule -Rule $inner))
            """;

        var (exit, output) = RunPowerShell(script, LandFile("LandSubstanceApplicability.Lib.ps1"));

        Assert.True(exit == 0, output);
        Assert.Contains(
            "WRAPPED=if(SUBSTANCE_APPLICABLE('LAND_SUBSTANCE_APPLICABILITY','ECW_C12_04_01','InvNum',@Land_InvNumEqp),"
            + "if(in(@Land_InvNumEqp,'B4-120-TA-101'),'B4-120-TA-101',''),'')",
            output,
            StringComparison.Ordinal);
        Assert.Contains("ROUNDTRIP=True", output, StringComparison.Ordinal);
        Assert.Contains("IS_WRAPPED=True", output, StringComparison.Ordinal);
        Assert.Contains("IS_WRAPPED_BASE=False", output, StringComparison.Ordinal);
        Assert.Contains("MATCH_DIRECT=True", output, StringComparison.Ordinal);
        Assert.Contains("MATCH_WRAPPED=True", output, StringComparison.Ordinal);
        Assert.Contains("MATCH_OTHER=False", output, StringComparison.Ordinal);
        Assert.Contains("ROUNDTRIP2=True", output, StringComparison.Ordinal);
        Assert.Contains("ROUNDTRIP3=True", output, StringComparison.Ordinal);
        Assert.Contains("NOT_WRAPPER=True", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void План_довідника_з_моделі_має_одинадцять_маркерів_і_жодного_пропущеного()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            $mdl = Get-Content -LiteralPath $args[1] -Raw -Encoding UTF8 | ConvertFrom-Json
            $reg = @($mdl.registries) | Where-Object { $_.code -eq 'LAND_SUBSTANCE_APPLICABILITY' } | Select-Object -First 1
            $plan = ConvertTo-LandSubstanceApplicabilityPlan -Registry $reg
            'ITEMS=' + @($plan.Items).Count
            'MARKERS=' + $plan.Markers
            'SKIPPED=' + $plan.Skipped
            'ENTRIES=' + @($reg.entries).Count
            $bad = [pscustomobject]@{ entries = @(
                [pscustomobject]@{ code = 'A1'; fields = [pscustomobject]@{ METHODOLOGY = 'ECW_X'; SELECTOR_KIND = 'Bogus'; SELECTOR_VALUE = 'v'; SUBSTANCE_CODE = '333' } },
                [pscustomobject]@{ code = 'A2'; fields = [pscustomobject]@{ METHODOLOGY = 'ECW_X'; SELECTOR_KIND = 'Stream'; SELECTOR_VALUE = 'v'; SUBSTANCE_CODE = '33' } },
                [pscustomobject]@{ code = 'A3'; fields = [pscustomobject]@{ METHODOLOGY = 'ECW_X'; SELECTOR_KIND = 'Stream'; SELECTOR_VALUE = 'v'; SUBSTANCE_CODE = '333' } },
                [pscustomobject]@{ code = 'A4'; fields = [pscustomobject]@{ METHODOLOGY = 'ECW_X'; SELECTOR_KIND = 'Stream'; SELECTOR_VALUE = 'v'; SUBSTANCE_CODE = '333' } }) }
            $p2 = ConvertTo-LandSubstanceApplicabilityPlan -Registry $bad
            'BAD_ITEMS=' + @($p2.Items).Count
            'BAD_SKIPPED=' + $p2.Skipped
            """;

        var (exit, output) = RunPowerShell(
            script, LandFile("LandSubstanceApplicability.Lib.ps1"), Path.Combine(Dir, "land-template.model.json"));

        Assert.True(exit == 0, output);
        Assert.Matches(@"ITEMS=\d+", output);
        Assert.Contains("MARKERS=11", output, StringComparison.Ordinal);
        Assert.Contains("SKIPPED=0", output, StringComparison.Ordinal);
        var items = int.Parse(Regex.Match(output, @"ITEMS=(\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var entries = int.Parse(Regex.Match(output, @"ENTRIES=(\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(entries, items);
        Assert.Contains("BAD_ITEMS=1", output, StringComparison.Ordinal);      // залишається лише перший валідний
        Assert.Contains("BAD_SKIPPED=3", output, StringComparison.Ordinal);    // невідомий вид, код з 2 цифр, дублікат
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Етап_SubstanceApplicability_лише_явний_і_LandParity_не_скидає_обгортку()
    {
        var build = File.ReadAllText(LandFile("Build-LandTemplate.ps1"));

        Assert.Matches(@"ValidateSet\([^)]*'SubstanceApplicability'[^)]*\)", build);
        Assert.Contains("if ($Stage -contains 'SubstanceApplicability') { Stage-SubstanceApplicability }", build, StringComparison.Ordinal);
        Assert.DoesNotContain("Want 'SubstanceApplicability'", build, StringComparison.Ordinal);

        // LandParity порівнює правило з файлом через Test-LandRuleMatchesBaseRule, а не `-ceq`.
        var parity = build[build.IndexOf("function Stage-LandParity", StringComparison.Ordinal)..];
        parity = parity[..parity.IndexOf("function Stage-CompositionMass", StringComparison.Ordinal)];
        Assert.Contains("Test-LandRuleMatchesBaseRule", parity, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"Json\.expression\s+-ceq\s+\$expr", parity);

        // Етап не публікує й не створює правил: лише PUT category-rule чернетки.
        var stage = build[build.IndexOf("function Stage-SubstanceApplicability", StringComparison.Ordinal)..];
        stage = stage[..stage.IndexOf("function Stage-Documents", StringComparison.Ordinal)];
        Assert.Contains("-eq 'Published'", stage, StringComparison.Ordinal);
        Assert.DoesNotContain("/publish", stage, StringComparison.Ordinal);
    }

    // D-7: обгортка читає @Land_TypeFuelOrStream (C11_10), а правило без обгортки - ні, тож PARITY_1 без цього
    // аргументу дає 422 ECR-CALC-0422. Мутаційний доказ: у Get-LandSubstanceApplicabilitySampleArguments повернути
    // порожню таблицю (або прибрати виклик зі Stage-SubstanceApplicability) - тест червоніє.
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Обгортка_додає_у_PARITY_1_аргумент_селектора_кожної_методології()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            . $args[2]
            $m = Get-Content -LiteralPath $args[1] -Raw -Encoding UTF8 | ConvertFrom-Json
            $sa = $m.substanceApplicability
            $reg = @($m.registries) | Where-Object { $_.code -eq $sa.registry } | Select-Object -First 1
            foreach ($p in $sa.methodologies.PSObject.Properties) {
              $n = Get-LandSubstanceApplicabilitySampleArguments -Methodology $p.Name -Kind ([string]$p.Value.kind) `
                   -ValueExpression ([string]$p.Value.valueExpression) -Entries @($reg.entries)
              $names = Get-LandRuleArgumentNames -Expression ([string]$p.Value.valueExpression)
              $missing = @($names | Where-Object { -not $n.ContainsKey($_) })
              'M=' + $p.Name + ';NEEDED=' + (($n.Keys | Sort-Object) -join ',') + ';MISSING=' + ($missing -join ',')
            }
            $ex = Get-LandSubstanceApplicabilitySampleArguments -Methodology 'ECW_C11_10' -Kind 'Stream' `
                  -ValueExpression '@Land_TypeFuelOrStream' -Entries @([pscustomobject]@{ fields = [pscustomobject]@{
                    METHODOLOGY = 'ECW_C11_10'; SELECTOR_KIND = '*'; SELECTOR_VALUE = '*'; SUBSTANCE_CODE = '*' } },
                  [pscustomobject]@{ fields = [pscustomobject]@{
                    METHODOLOGY = 'ECW_C11_10'; SELECTOR_KIND = 'Stream'; SELECTOR_VALUE = 'S1'; SUBSTANCE_CODE = '333' } })
            'SYN=' + $ex['Land_TypeFuelOrStream']
            'SYNNONE=' + (Get-LandSubstanceApplicabilitySampleArguments -Methodology 'X' -Kind 'Stream' `
                  -ValueExpression '@A' -Entries @()).Count
            """;

        var (exit, output) = RunPowerShell(
            script,
            LandFile("LandSubstanceApplicability.Lib.ps1"),
            Path.Combine(Dir, "land-template.model.json"),
            LandFile("LandParityRuleArgs.Lib.ps1"));

        Assert.True(exit == 0, output);
        Assert.Contains("M=ECW_C11_10;NEEDED=Land_TypeFuelOrStream;MISSING=", output, StringComparison.Ordinal);
        Assert.Contains("SYN=S1", output, StringComparison.Ordinal);
        Assert.Contains("SYNNONE=0", output, StringComparison.Ordinal);

        // Усі методології, крім REGFIELD (C12_03: значення - код запису довідника), отримують свій аргумент.
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("M=", StringComparison.Ordinal)))
        {
            if (line.StartsWith("M=ECW_C12_03;", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.Matches(@"NEEDED=\w+;MISSING=\s*$", line.TrimEnd());
        }

        // Етап справді вживає це при публікації обгортки.
        var build = File.ReadAllText(LandFile("Build-LandTemplate.ps1"));
        var stage = build[build.IndexOf("function Stage-SubstanceApplicability", StringComparison.Ordinal)..];
        stage = stage[..stage.IndexOf("function Stage-Documents", StringComparison.Ordinal)];
        Assert.Contains("Get-LandSubstanceApplicabilitySampleArguments", stage, StringComparison.Ordinal);
        Assert.Contains("/tests/PARITY_1", stage, StringComparison.Ordinal);
    }

    // Нечутливі до регістру «пари» ключа навмисні: старий C# тримає обидва написання (ординальні словники), ключ довідника
    // ignoreCase=false, тож це РІЗНІ ключі з однаковим складом. Фіксуємо перелік і однаковість наборів кодів між написаннями.
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Регістро_нечутливі_пари_ключа_очікувані_з_однаковими_наборами_кодів_а_точних_дублів_немає()
    {
        using var model = LoadModel();
        var registry = model.RootElement.GetProperty("registries").EnumerateArray()
            .Single(r => r.GetProperty("code").GetString() == "LAND_SUBSTANCE_APPLICABILITY");
        var entries = registry.GetProperty("entries").EnumerateArray()
            .Select(e => KeyFields.Select(n => e.GetProperty("fields").GetProperty(n).GetString()!).ToArray())
            .Where(k => k[1] != "*")
            .ToList();

        // Точних (ординальних) дублів ключа немає.
        Assert.Equal(entries.Count, entries.Select(k => string.Join('|', k)).Distinct(StringComparer.Ordinal).Count());

        var groups = entries
            .GroupBy(k => string.Join('|', k[0], k[1], k[2].ToUpperInvariant(), k[3]), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();

        // Кожна група - рівно два написання одного значення селектора.
        Assert.All(groups, g => Assert.Equal(2, g.Count()));
        // 61 -> 65: ECW_C11_10 дістав `Makat gas - Макатский газ` (мала g; LAND-K8-MAKAT-VERIFY) - ще чотири пари за кодами речовин.
        Assert.Equal(65, groups.Count);
        Assert.Equal(130, groups.Sum(g => g.Count()));

        var byMethodology = groups.GroupBy(g => g.First()[0]).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        Assert.Equal(
            new Dictionary<string, int>
            {
                ["ECW_C11_01"] = 17, ["ECW_C11_02"] = 17, ["ECW_C11_10"] = 17, ["ECW_C12_01"] = 1, ["ECW_C12_03"] = 13,
            },
            byMethodology);

        var spellingSets = entries
            .GroupBy(k => (Methodology: k[0], Kind: k[1], Value: k[2].ToUpperInvariant()))
            .Where(p => p.Select(k => k[2]).Distinct(StringComparer.Ordinal).Count() > 1)
            .ToList();
        var pairs = spellingSets
            .Select(p => $"{p.Key.Methodology}|{p.Key.Kind}|" + string.Join(" / ", p.Select(k => k[2]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                "ECW_C11_01|Stream|Fuel Gas (Internal) - Топливный газ (Внутренний) / Fuel gas (Internal) - Топливный газ (Внутренний)",
                "ECW_C11_01|Stream|Makat Gas - Макатский газ / Makat gas - Макатский газ",
                "ECW_C11_02|Stream|Fuel Gas (Internal) - Топливный газ (Внутренний) / Fuel gas (Internal) - Топливный газ (Внутренний)",
                "ECW_C11_02|Stream|Makat Gas - Макатский газ / Makat gas - Макатский газ",
                "ECW_C11_10|Stream|Fuel Gas (Internal) - Топливный газ (Внутренний) / Fuel gas (Internal) - Топливный газ (Внутренний)",
                "ECW_C11_10|Stream|Makat Gas - Макатский газ / Makat gas - Макатский газ",
                "ECW_C12_01|Stream|Hydraulic Oil / Hydraulic oil",
                "ECW_C12_03|TypeFuel|Fuel Gas (Internal) - Топливный газ (Внутренний) / Fuel gas (Internal) - Топливный газ (Внутренний)",
            ],
            pairs);

        // Набір кодів речовин однаковий у обох написань кожної пари.
        foreach (var pair in spellingSets)
        {
            var sets = pair.GroupBy(k => k[2], StringComparer.Ordinal)
                .Select(s => string.Join(',', s.Select(k => k[3]).Order(StringComparer.Ordinal)))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            Assert.True(sets.Count == 1, $"{pair.Key}: набори кодів різняться між написаннями.");
        }
    }

    // LAND-K8-MAKAT-VERIFY: у даних прод-вибірки потік Makat пишеться `Makat gas - Макатский газ` (мала g); ключ довідника
    // порівнюється ординально, тож без запису C11_10 мовчки втрачав усі 4 речовини. Мутаційний доказ: прибрати
    // EXTRA_SELECTOR_TEXTS['ECW_C11_10'] у substance_applicability.py і перегенерувати модель - тест червоніє.
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void C11_10_має_усі_чотири_написання_потоку_Makat_з_однаковими_кодами()
    {
        using var model = LoadModel();
        var rows = Rows(model, out _);

        string[] spellings =
        [
            "Makat",
            "Makat Gas - Макатский газ",
            "Makat gas - Макатский газ",
            "Fuel gas (Makat) - Топливный газ (Makat)",
        ];

        foreach (var spelling in spellings)
        {
            Assert.Equal(Sorted(FgMakat), Codes(rows, "ECW_C11_10", spelling));
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Прогалини_покриття_селектора_знаходяться_порівнянням_ordinal_проти_ToLower_Contains()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            $spec = Get-Content -LiteralPath $args[1] -Raw -Encoding UTF8 | ConvertFrom-Json
            $m = Get-Content -LiteralPath $args[2] -Raw -Encoding UTF8 | ConvertFrom-Json
            $reg = @($m.registries) | Where-Object { $_.code -eq 'LAND_SUBSTANCE_APPLICABILITY' } | Select-Object -First 1
            $c10 = [pscustomobject]@{ ECW_C11_10 = $spec.methodologies.ECW_C11_10 }
            $obs = @(
              [pscustomobject]@{ Value = 'Makat gas - Макатский газ'; Rows = 7 },
              [pscustomobject]@{ Value = 'MAKAT GAS - Макатский газ'; Rows = 2 },
              [pscustomobject]@{ Value = 'Diesel - Дизель'; Rows = 5 })
            # Поточна модель: мала g покрита, а вигаданий верхній регістр - ні (ordinal), Diesel легасі не приймає.
            $g = @(Get-LandSelectorCoverageGaps -Observed $obs -Methodologies $c10 -Entries @($reg.entries))
            'GAPS=' + (($g | ForEach-Object { $_.Value + ':' + $_.Rows + ':' + ($_.MissingCodes -join '/') }) -join ';')
            # Синтетичний довідник без запису `Makat gas`: прогалина з усіма чотирма кодами.
            $syn = @($reg.entries | Where-Object { -not ($_.fields.METHODOLOGY -ceq 'ECW_C11_10' -and $_.fields.SELECTOR_VALUE -ceq 'Makat gas - Макатский газ') })
            $g2 = @(Get-LandSelectorCoverageGaps -Observed @($obs[0]) -Methodologies $c10 -Entries $syn)
            'SYN=' + $g2.Count + ':' + ($g2[0].MissingCodes -join '/')
            'PREFIX=' + (Test-LandLegacyPrefixAccepts -Text 'Makat gas - Макатский газ' -ProviderEntries @($spec.methodologies.ECW_C11_10.providerEntries))
            'NOPREFIX=' + ($null -eq (Test-LandLegacyPrefixAccepts -Text 'Diesel - Дизель' -ProviderEntries @($spec.methodologies.ECW_C11_10.providerEntries)))
            """;

        var (exit, output) = RunPowerShell(
            script,
            LandFile("LandSubstanceApplicability.Lib.ps1"),
            Path.Combine(Dir, "land-substance-applicability.json"),
            Path.Combine(Dir, "land-template.model.json"));

        Assert.True(exit == 0, output);
        Assert.Contains("GAPS=MAKAT GAS - Макатский газ:2:333/415/416/1728", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Makat gas - Макатский газ:7", output, StringComparison.Ordinal);
        Assert.Contains("SYN=1:333/415/416/1728", output, StringComparison.Ordinal);
        Assert.Contains("PREFIX=FG_Makat", output, StringComparison.Ordinal);
        Assert.Contains("NOPREFIX=True", output, StringComparison.Ordinal);
    }
}