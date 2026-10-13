using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// LP-10 (прив'язки C11/C12, дефект D3 перевірки RC «Е»): після <c>-Stage Methodologies</c> публікація чернеток давала
/// 0 з 27 — <c>ECR-CALC-0438</c> (аргумент формули без колонки з тим самим кодом у прив'язаній таблиці) і
/// <c>ECR-CALC-0422</c> (аргумент правила категорії, <c>@Land_NAE</c> ...). Колонки шаблону названо за заголовком Excel,
/// аргументи - за атрибутом AF. Мости (Formula-колонки з кодом аргументу) будує <c>af_attr_bridges.py</c> за атрибутами
/// AF-таблиць (<c>af-land-table-attributes.json</c>), перейменуваннями з коду старої системи й календарними днями
/// C11_11_01/C12_04_02. Усе недоведене названо в <c>argumentBridges.unresolved</c> з причиною.
/// Тест читає лише файли моделі; без перегенерації (<c>tools/land/model-gen/run-model-gen.ps1</c>) він червоний.
/// </summary>
public sealed class LandAfAttributeBridgesModelTests
{
    private static readonly string Dir = Path.Combine(
        SourceTree.Root, "docs", "delivery", "reference-data", "land-template");

    // MethodologyPublishChecks.DefaultContextualArguments + DerivedArgumentNames.All: колонки не вимагають.
    private static readonly HashSet<string> Contextual = new(StringComparer.OrdinalIgnoreCase)
    {
        "CalculationDate", "Location", "Total", "Duration", "FlareUnitMode", "IsPilot", "Category",
    };

    private static readonly HashSet<string> CalcTables = ["T3_00", "T4_01", "T5_08", "T5_09", "T5_22", "T5_27"];

    private static JsonDocument Load(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, name)));

    private static Dictionary<string, JsonElement> Tables(JsonDocument model)
        => model.RootElement.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray())
            .GroupBy(t => t.GetProperty("code").GetString()!)
            .ToDictionary(g => g.Key, g => g.First());

    private static Dictionary<string, JsonElement> Columns(JsonElement table)
        => table.GetProperty("columns").EnumerateArray().ToDictionary(c => c.GetProperty("code").GetString()!, c => c);

    private static string Expression(JsonDocument model, string table, string column)
        => Columns(Tables(model)[table])[column].GetProperty("expression").GetString()!;

    private static string? Unresolved(JsonDocument model, string table, string arg)
    {
        var u = model.RootElement.GetProperty("argumentBridges").GetProperty("unresolved");
        return u.TryGetProperty(table, out var t) && t.TryGetProperty(arg, out var r) ? r.GetString() : null;
    }

    /// <summary>Аргументи, яких вимагає публікація методології для таблиці: входи формул + @Category N + правило категорії.</summary>
    private static Dictionary<(string Methodology, string Table), SortedSet<string>> Required()
    {
        using var map = Load("land-result-outputs.json");
        using var rules = Load("af-land-category-rules.json");
        var ruleArgs = rules.RootElement.GetProperty("rules").EnumerateArray().ToDictionary(
            r => r.GetProperty("methodology").GetString()!,
            r => Regex.Matches(r.GetProperty("expression").GetString()!, @"@([A-Za-z_][A-Za-z0-9_]*)")
                .Select(m => m.Groups[1].Value).ToHashSet(),
            StringComparer.Ordinal);
        var res = new Dictionary<(string, string), SortedSet<string>>();

        foreach (var m in map.RootElement.GetProperty("methodologies").EnumerateObject())
        {
            if (!Regex.IsMatch(m.Name, "^ECW_C(08|09|11|12)"))
            {
                continue;
            }

            var args = m.Value.GetProperty("inputs").EnumerateArray().Select(x => x.GetString()!).ToHashSet();
            if (m.Value.TryGetProperty("categoryInputs", out var cat))
            {
                args.UnionWith(cat.EnumerateArray().Select(x => x.GetString()!));
            }

            if (ruleArgs.TryGetValue(m.Name, out var ra))
            {
                args.UnionWith(ra);
            }

            args.ExceptWith(Contextual);
            foreach (var t in m.Value.GetProperty("tables").EnumerateArray().Select(x => x.GetString()!))
            {
                if (!CalcTables.Contains(t))
                {
                    res[(m.Name, t)] = new SortedSet<string>(args, StringComparer.Ordinal);
                }
            }
        }

        return res;
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Мости_за_атрибутами_AF_є_Formula_лише_для_читання_з_посиланнями_на_колонки_таблиці()
    {
        using var model = Load("land-template.model.json");
        var total = 0;

        foreach (var table in Tables(model).Values)
        {
            var cols = Columns(table);
            foreach (var c in cols.Values)
            {
                var evidence = c.TryGetProperty("ecrAdded", out var e) ? e.GetString() ?? "" : "";
                if (!evidence.Contains("AF attribute", StringComparison.Ordinal)
                    && !evidence.Contains("calendar days", StringComparison.Ordinal))
                {
                    continue;
                }

                total++;
                Assert.Equal("Formula", c.GetProperty("dataType").GetString());
                Assert.True(c.GetProperty("readOnly").GetBoolean());
                foreach (Match m in Regex.Matches(c.GetProperty("expression").GetString()!, @"\[([^\]]+)\]"))
                {
                    var token = m.Groups[1].Value;
                    Assert.True(
                        token.StartsWith("Period", StringComparison.Ordinal) || cols.ContainsKey(token),
                        $"{table.GetProperty("code").GetString()}.{c.GetProperty("code").GetString()}: [{token}] не колонка таблиці.");
                }
            }
        }

        Assert.True(total >= 170, $"Мостів за атрибутами AF має бути ≥ 170, зараз {total}.");
    }

    [Theory]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("T9_04", "Land_TypeFuelOrStream", "[TypeOfFuel]")]
    [InlineData("T9a_27", "Land_TypeFuelOrStream", "[Stream]")]
    [InlineData("T9_01", "Land_NAE", "[IDCodeNAEProject]")]
    [InlineData("T10_05", "Land_Kpmax", "[KpMaxExperimentalFactor]")]
    [InlineData("T11_02", "No_Pump", "[PumpNo]")]
    // перейменування, прямо написані в коді методологій (R_EmissionCalculationWork)
    [InlineData("T9_06", "Land_Measure_RunningHours", "[OperatingTime_M01]")]
    [InlineData("T9_13", "Land_Measure_RunningHours", "[OperatingTime_M01]")]
    [InlineData("T9a_24", "Land_Measure_SubstanceConsumption", "[ReleaseOnColdVent20Dgr1atm_M01]")]
    [InlineData("T9a_19", "Land_Measure_SubstanceConsumption", "[AmountOfOilPumpedIntoTheTank_M01]")]
    [InlineData("T9a_17", "Land_Measure_RunningHours", "[OperatingTime_M01]")]
    // OperatingTime стоїть у класі AF на місці єдиної колонки «Material residence time» (EXCEL_HEADER_OVERRIDES)
    [InlineData("T9a_16", "Land_Measure_OperatingTime", "[Period].Sequence = 1 ? [MaterialResidenceTimeInProcessUnits_M01]")]
    [InlineData("T9a_30", "Land_Measure_OperatingTime", "[Period].Sequence = 1 ? [MaterialResidenceTimeInProcessUnits_M01]")]
    // місячні мости вибирають колонку за номером періоду
    [InlineData("T9a_21", "Land_Measure_OperatingTime", "[Period].Sequence = 1 ? [OperatingTime_M01_M]")]
    [InlineData("T9a_21", "Land_Measure_OperatingTimeDays", "[Period].Sequence = 1 ? [OperatingTime_M01]")]
    public void Прийняті_мости_мають_доведене_джерело(string table, string argument, string fragment)
    {
        using var model = Load("land-template.model.json");

        Assert.Contains(fragment, Expression(model, table, argument), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Місячний_міст_має_12_місяців_а_текстовий_не_має_масштабу()
    {
        using var model = Load("land-template.model.json");
        var cols = Columns(Tables(model)["T9_06"]);

        var months = Regex.Matches(
            cols["Land_Measure_RunningHours"].GetProperty("expression").GetString()!, @"\[Period\]\.Sequence = (\d+) \? \[([^\]]+)\]");
        Assert.Equal(11, months.Count);
        for (var i = 0; i < 11; i++)
        {
            Assert.Equal(i + 1, int.Parse(months[i].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(i + 1, cols[months[i].Groups[2].Value].GetProperty("month").GetInt32());
        }

        // текстова колонка-джерело (String): результат моста - текст, масштабу немає (ValueString у комірці)
        var text = Columns(Tables(model)["T9_04"])["Land_TypeFuelOrStream"];
        Assert.Equal(JsonValueKind.Null, text.GetProperty("precision").ValueKind);
        Assert.Equal(JsonValueKind.Null, text.GetProperty("scale").ValueKind);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Календарні_дні_DaysInCalcMonth_лише_там_де_їх_бере_старий_код()
    {
        using var model = Load("land-template.model.json");
        using var map = Load("land-result-outputs.json");
        var allowed = new HashSet<string> { "ECW_C11_11_01", "ECW_C12_04_02" };
        var bridged = 0;

        foreach (var (code, table) in Tables(model))
        {
            if (code.StartsWith("T3_", StringComparison.Ordinal) || code.StartsWith("T4_", StringComparison.Ordinal)
                || code.StartsWith("T5_", StringComparison.Ordinal)
                || !Columns(table).TryGetValue("DaysInCalcMonth", out var column))
            {
                continue;
            }

            bridged++;
            Assert.Equal("[Period].Days", column.GetProperty("expression").GetString());

            // кожна методологія таблиці, що вживає DaysInCalcMonth, - серед тих, чий код рахує календарні дні
            foreach (var m in map.RootElement.GetProperty("methodologies").EnumerateObject())
            {
                var onTable = m.Value.GetProperty("tables").EnumerateArray().Any(t => t.GetString() == code);
                var uses = m.Value.GetProperty("inputs").EnumerateArray().Any(i => i.GetString() == "DaysInCalcMonth");
                Assert.True(!onTable || !uses || allowed.Contains(m.Name), $"{code}: {m.Name} вживає DaysInCalcMonth.");
            }
        }

        Assert.True(bridged >= 9, $"Календарних мостів DaysInCalcMonth має бути ≥ 9, зараз {bridged}.");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Недоведене_названо_з_причиною_конфлікт_відсутній_атрибут_міжрядкове()
    {
        using var model = Load("land-template.model.json");

        // C11_01/C11_02 беруть OperatingTime з OperTimeDays, C11_10 - з атрибута OperatingTime: на ОДНІЙ таблиці два джерела
        Assert.StartsWith("[конфлікт]", Unresolved(model, "T9a_27", "Land_Measure_OperatingTime"), StringComparison.Ordinal);
        // AF-таблиця 9_02 не має OperatingTime: старий код не передає аргумент
        Assert.StartsWith("[AF-немає]", Unresolved(model, "T9_02", "Land_Measure_OperatingTime"), StringComparison.Ordinal);
        // компоненти GTU беруться з іншого рядка таблиці
        Assert.StartsWith("[GTU]", Unresolved(model, "T10_03", "Land_Measure_Component_301"), StringComparison.Ordinal);
        // Land_Power і Category1 виводяться з груп NAE у коді
        Assert.StartsWith("[похідне]", Unresolved(model, "T9a_17", "Land_Power"), StringComparison.Ordinal);
        Assert.StartsWith("[похідне]", Unresolved(model, "T9_01", "Category1"), StringComparison.Ordinal);
        // вхід, названий в unresolved, не має колонки-мосту
        var tables = Tables(model);
        Assert.DoesNotContain("Land_Measure_OperatingTime", Columns(tables["T9a_27"]).Keys);
        Assert.DoesNotContain("Land_Measure_Component_301", Columns(tables["T10_03"]).Keys);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_аргумент_публікації_є_колонкою_або_названий_у_unresolved()
    {
        using var model = Load("land-template.model.json");
        var tables = Tables(model);
        var checkedPairs = 0;

        foreach (var ((methodology, table), args) in Required())
        {
            if (!tables.TryGetValue(table, out var t))
            {
                continue;
            }

            var cols = Columns(t);
            foreach (var arg in args)
            {
                checkedPairs++;
                Assert.True(
                    cols.ContainsKey(arg) || Unresolved(model, table, arg) is not null,
                    $"{table}: аргумент {arg} ({methodology}) без колонки й без запису в unresolved.");
            }
        }

        Assert.True(checkedPairs > 300, $"Перевірено лише {checkedPairs} пар.");
    }

    /// <summary>
    /// Імітація <c>CheckArgumentTableColumns</c> + <c>CheckArgumentColumns</c> правила категорії: методологія публікується,
    /// якщо для КОЖНОЇ її прив'язаної таблиці всі аргументи мають колонки. Перелічені нижче методології пройшли б
    /// (до мостів не проходила жодна з C11/C12). Це не доказ зеленої публікації (константи, категорії, склад речовин -
    /// окремі перевірки), а доказ, що колонкова частина D3 закрита.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Методології_із_закритою_колонковою_частиною_проходять_перевірку_аргументів()
    {
        using var model = Load("land-template.model.json");
        var tables = Tables(model);
        var openByMethodology = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var ((methodology, table), args) in Required())
        {
            if (!tables.TryGetValue(table, out var t))
            {
                continue;
            }

            var cols = Columns(t);
            foreach (var arg in args.Where(a => !cols.ContainsKey(a)))
            {
                if (!openByMethodology.TryGetValue(methodology, out var list))
                {
                    openByMethodology[methodology] = list = [];
                }

                list.Add($"{table}.{arg}");
            }
        }

        string[] expectedGreen =
        [
            "ECW_C11_04", "ECW_C11_05", "ECW_C11_06", "ECW_C11_07", "ECW_C11_11_01", "ECW_C11_11_05", "ECW_C11_12_01",
            "ECW_C11_12_03", "ECW_C11_13", "ECW_C11_14", "ECW_C11_15", "ECW_C11_17", "ECW_C11_18", "ECW_C11_19",
            "ECW_C11_20_01", "ECW_C11_20_02", "ECW_C12_04_01", "ECW_C12_04_02", "ECW_C12_05",
        ];

        foreach (var name in expectedGreen)
        {
            Assert.False(
                openByMethodology.TryGetValue(name, out var open),
                $"{name}: немає колонок для {string.Join(", ", open ?? [])}.");
        }

        // і є що закривати далі: CompositionMass, GTU, конфлікти й відсутні атрибути ще відкриті
        Assert.Contains("ECW_C12_01", openByMethodology.Keys);
        Assert.Contains("ECW_C12_02", openByMethodology.Keys);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Файл_атрибутів_AF_містить_структуру_без_значень()
    {
        using var attrs = Load("af-land-table-attributes.json");
        var tables = attrs.RootElement.GetProperty("tables");

        Assert.True(tables.EnumerateObject().Count() >= 50);
        var t904 = tables.GetProperty("T9_04").GetProperty("attributes").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString()!).ToList();
        Assert.Contains("Land_Measure_OperatingTime", t904);
        Assert.Contains("Land_TypeFuelOrStream", t904);
        Assert.Contains("Land_NAE", t904);

        // атрибути - лише імена й типи C#; жодного поля значень
        foreach (var t in tables.EnumerateObject())
        {
            foreach (var a in t.Value.GetProperty("attributes").EnumerateArray())
            {
                Assert.Equal(["name", "type"], a.EnumerateObject().Select(p => p.Name).ToArray());
            }
        }
    }
}
