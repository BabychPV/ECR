using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Прод-зразок HSE 2026-10-12 (прогалини 3-5): коди колонок прод-таблиць AF (<c>Land_Measure_*</c>, <c>Land_EF_*</c>,
/// <c>Land_AL_*</c>, <c>Land_Agent_*</c>) розходяться з кодами моделі шаблону Land, а частини колонок введення в Excel-шаблоні
/// немає. Модель має <c>prodColumnMapping</c> (аліаси за заголовком Excel, текстові мости, нові колонки введення).
/// Тест стереже цілісність мапи щодо самої моделі: ціль існує, вона вхідна (не Formula/Calculated, не read-only),
/// місячні групи повні, нові колонки не дублюють наявних кодів.
/// </summary>
public sealed class LandProdColumnMappingModelTests
{
    private static JsonDocument Model()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(
            SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));

    private static JsonElement TableByKey(JsonDocument model, string key)
    {
        var parts = key.Split('|');

        return model.RootElement.GetProperty("sheets").EnumerateArray()
            .Single(s => s.GetProperty("code").GetString() == parts[0])
            .GetProperty("tables").EnumerateArray()
            .Single(t => t.GetProperty("code").GetString() == parts[1]);
    }

    private static Dictionary<string, JsonElement> Columns(JsonElement table)
        => table.GetProperty("columns").EnumerateArray().ToDictionary(c => c.GetProperty("code").GetString()!, StringComparer.Ordinal);

    private static IEnumerable<(string Key, JsonElement Table, JsonElement Entry)> Entries(JsonDocument model)
        => model.RootElement.GetProperty("prodColumnMapping").GetProperty("tables").EnumerateObject()
            .Select(p => (p.Name, TableByKey(model, p.Name), p.Value));

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Ціль_кожного_аліаса_існує_і_є_вхідною_колонкою()
    {
        using var model = Model();
        var seen = 0;

        foreach (var (key, table, entry) in Entries(model))
        {
            var columns = Columns(table);

            foreach (var alias in entry.GetProperty("aliases").EnumerateArray())
            {
                var prod = alias.GetProperty("prod").GetString();
                var targets = alias.TryGetProperty("monthColumns", out var months)
                    ? months.EnumerateArray().Select(e => e.GetString()!).ToList()
                    : [alias.GetProperty("column").GetString()!];

                foreach (var code in targets)
                {
                    Assert.True(columns.TryGetValue(code, out var column), $"{key}: {prod} -> {code} відсутня в таблиці.");
                    var type = column.GetProperty("dataType").GetString();
                    Assert.True(type is not ("Formula" or "Calculated"), $"{key}: {prod} -> {code} обчислювана ({type}).");
                    Assert.False(column.TryGetProperty("readOnly", out var ro) && ro.GetBoolean(), $"{key}: {prod} -> {code} read-only.");
                    Assert.False(column.TryGetProperty("hidden", out var hidden) && hidden.GetBoolean(), $"{key}: {prod} -> {code} прихована.");
                }

                if (alias.TryGetProperty("monthColumns", out var m))
                {
                    var monthNumbers = m.EnumerateArray().Select(e => columns[e.GetString()!].GetProperty("month").GetInt32()).ToList();
                    Assert.Equal(Enumerable.Range(1, 12), monthNumbers);
                }

                seen++;
            }
        }

        Assert.True(seen >= 100, $"Очікувалось щонайменше 100 аліасів, є {seen}.");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Аліас_прод_колонки_в_таблиці_не_веде_у_дві_колонки_і_дві_прод_колонки_не_ведуть_в_одну()
    {
        using var model = Model();

        foreach (var (key, _, entry) in Entries(model))
        {
            var prods = entry.GetProperty("aliases").EnumerateArray().Select(a => a.GetProperty("prod").GetString()!).ToList();
            Assert.Equal(prods.Count, prods.Distinct(StringComparer.Ordinal).Count());

            var targets = entry.GetProperty("aliases").EnumerateArray()
                .Select(a => a.TryGetProperty("column", out var c)
                    ? c.GetString()!
                    : string.Join(",", a.GetProperty("monthColumns").EnumerateArray().Select(e => e.GetString())))
                .ToList();

            Assert.True(
                targets.Count == targets.Distinct(StringComparer.Ordinal).Count(),
                $"{key}: дві прод-колонки ведуть в одну колонку шаблону.");
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Стаціонарне_обладнання_має_відповідність_для_палива_EF_і_AL()
    {
        using var model = Model();
        var table = TableByKey(model, "S03_STATIONARY_EQUIPMENT|T3_00");
        var columns = Columns(table);
        var entry = model.RootElement.GetProperty("prodColumnMapping").GetProperty("tables").GetProperty("S03_STATIONARY_EQUIPMENT|T3_00");
        var byProd = entry.GetProperty("aliases").EnumerateArray().ToDictionary(a => a.GetProperty("prod").GetString()!);

        var fuel = columns[byProd["Land_FuelConsumption"].GetProperty("column").GetString()!];
        Assert.Equal("Decimal", fuel.GetProperty("dataType").GetString());
        Assert.Equal("l/h", fuel.GetProperty("unit").GetString());

        Assert.Equal(8, byProd.Keys.Count(k => k.StartsWith("Land_EF_", StringComparison.Ordinal) && k != "Land_EF_Remarks"));
        Assert.Equal(11, byProd.Keys.Count(k => k.StartsWith("Land_AL_", StringComparison.Ordinal)));
        Assert.Contains("Land_EF_Remarks", byProd.Keys);

        // Текстові мости: прод-текст пишеться у вхідну Lookup-колонку *Ref, а не у read-only формулу.
        var refs = entry.GetProperty("refs").EnumerateArray().ToDictionary(r => r.GetProperty("prod").GetString()!, r => r.GetProperty("column").GetString()!);
        Assert.Equal("Land_OverhauledEnginesRef", refs["Land_OverhauledEngines"]);
        Assert.Equal("Lookup", columns[refs["Land_OverhauledEngines"]].GetProperty("dataType").GetString());
        Assert.True(columns["Land_OverhauledEngines"].GetProperty("readOnly").GetBoolean());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Дубльована_споживання_кг_і_кг_год_у_9_04_розведені_по_місячних_колонках()
    {
        using var model = Model();
        var entry = model.RootElement.GetProperty("prodColumnMapping").GetProperty("tables").GetProperty("S09_UTILITY|T9_04");
        var columns = Columns(TableByKey(model, "S09_UTILITY|T9_04"));
        var byProd = entry.GetProperty("aliases").EnumerateArray().ToDictionary(a => a.GetProperty("prod").GetString()!);

        var kg = byProd["Land_Measure_SubstanceConsumptionKg"].GetProperty("monthColumns").EnumerateArray().Select(e => e.GetString()!).ToList();
        var kgh = byProd["Land_Measure_SubstanceConsumptionKgH"].GetProperty("monthColumns").EnumerateArray().Select(e => e.GetString()!).ToList();

        Assert.Empty(kg.Intersect(kgh));
        Assert.All(kg, c => Assert.Equal("kg", columns[c].GetProperty("unit").GetString()));
        Assert.All(kgh, c => Assert.Equal("kg/h", columns[c].GetProperty("unit").GetString()));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Нові_колонки_введення_існують_вхідні_і_не_дублюють_коди()
    {
        using var model = Model();
        var registries = model.RootElement.GetProperty("registries").EnumerateArray().Select(r => r.GetProperty("code").GetString()!).ToHashSet();
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["S10_OFF_UTILITY_DI|T10_01"] = 1,
            ["S10_OFF_UTILITY_DI|T10_03"] = 1 + 21,
            ["S10_OFF_UTILITY_DI|T10_04"] = 1,
            ["S11_OFF_PROCESS_DI|T11_01"] = 1,
            ["S11_OFF_PROCESS_DI|T11_03"] = 1 + 26,
            ["S05_OTHER_EQUIPMENT|T5_08"] = 1,
            ["S05_OTHER_EQUIPMENT|T5_09"] = 1,
            ["S05_OTHER_EQUIPMENT|T5_27"] = 1,
        };
        var mapping = model.RootElement.GetProperty("prodColumnMapping").GetProperty("tables");
        var total = 0;

        foreach (var (key, count) in expected)
        {
            var table = TableByKey(model, key);
            var all = table.GetProperty("columns").EnumerateArray().ToList();
            var codes = all.Select(c => c.GetProperty("code").GetString()!).ToList();
            var added = mapping.GetProperty(key).GetProperty("newColumns").EnumerateArray().Select(e => e.GetString()!).ToList();

            Assert.Equal(count, added.Count);
            Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
            total += added.Count;

            foreach (var code in added)
            {
                var column = all.Single(c => c.GetProperty("code").GetString() == code);
                Assert.False(column.GetProperty("readOnly").GetBoolean());
                Assert.False(column.GetProperty("hidden").GetBoolean());
                Assert.False(string.IsNullOrEmpty(column.GetProperty("ecrAdded").GetString()));
                Assert.False(string.IsNullOrEmpty(column.GetProperty("prodEvidence").GetString()));

                if (column.GetProperty("dataType").GetString() == "Lookup")
                {
                    Assert.Contains(column.GetProperty("registry").GetString()!, registries);
                }
            }
        }

        Assert.Equal(55, total);
        // 5147 колонок до prodColumnMapping + 55 колонок введення + 570 колонок результатів C08/C09/C11/C12 (LP-08: 544;
        // ще 26 у таблицях 9a_17a/21a/21b, які LP-09 повернув у модель під правильними кодами; ще 1 - ECW_EC_tons у T7b_00)
        const int resultColumns = 571;
        Assert.Equal(55, model.RootElement.GetProperty("stats").GetProperty("columns").GetInt32() - 5147 - resultColumns);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Недоведене_і_розбіжність_одиниць_названі_у_моделі()
    {
        using var model = Model();
        var root = model.RootElement.GetProperty("prodColumnMapping");

        Assert.NotEmpty(root.GetProperty("notProven").EnumerateArray());

        foreach (var mismatch in root.GetProperty("unitMismatches").EnumerateArray())
        {
            var table = model.RootElement.GetProperty("sheets").EnumerateArray()
                .SelectMany(s => s.GetProperty("tables").EnumerateArray())
                .First(t => t.GetProperty("code").GetString() == mismatch.GetProperty("table").GetString());
            var column = Columns(table).First(kv => kv.Key.StartsWith(mismatch.GetProperty("column").GetString()!, StringComparison.Ordinal)
                && kv.Value.GetProperty("headerEn").GetString() == mismatch.GetProperty("header").GetString()).Value;

            Assert.Equal(mismatch.GetProperty("modelUnit").GetString(), column.GetProperty("unit").GetString());
        }
    }
}
