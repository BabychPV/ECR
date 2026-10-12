using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// LP-10: таблиці аркушів 6, 9-11 (MonthsInColumns) не мали мостів аргументів методологій C08/C09/C11/C12, на відміну від
/// аркушів 3-5, де Formula-міст <c>X</c> вибирає місяць із <c>X_M01..X_M12</c> за <c>[Period].Sequence</c>. Мости робляться
/// лише там, де відповідність прод-колонці AF доведена <c>prodColumnMapping</c> і вхід справді вживає методологія таблиці;
/// решта перелічена в <c>argumentBridges.unresolved</c> з причиною (нічого не вгадуємо).
/// </summary>
public sealed class LandArgBridgesModelTests
{
    private static readonly string Dir = Path.Combine(
        SourceTree.Root, "docs", "delivery", "reference-data", "land-template");

    private static JsonDocument Load(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, name)));

    private static Dictionary<string, JsonElement> Tables(JsonDocument model)
        => model.RootElement.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray())
            .GroupBy(t => t.GetProperty("code").GetString()!)
            .ToDictionary(g => g.Key, g => g.First());

    private static Dictionary<string, JsonElement> Columns(JsonElement table)
        => table.GetProperty("columns").EnumerateArray().ToDictionary(c => c.GetProperty("code").GetString()!, c => c);

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_міст_це_Formula_лише_для_читання_і_посилається_на_колонки_своєї_таблиці()
    {
        using var model = Load("land-template.model.json");
        var tables = Tables(model);
        var total = 0;

        foreach (var table in model.RootElement.GetProperty("argumentBridges").GetProperty("bridged").EnumerateObject())
        {
            var cols = Columns(tables[table.Name]);
            foreach (var name in table.Value.EnumerateArray().Select(x => x.GetString()!))
            {
                total++;
                var c = cols[name];
                Assert.Equal("Formula", c.GetProperty("dataType").GetString());
                Assert.True(c.GetProperty("readOnly").GetBoolean());
                var expr = c.GetProperty("expression").GetString()!;
                foreach (Match m in Regex.Matches(expr, @"\[([^\]]+)\]"))
                {
                    var token = m.Groups[1].Value;
                    if (token.StartsWith("Period", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    Assert.True(cols.ContainsKey(token), $"{table.Name}.{name}: [{token}] не колонка таблиці.");
                }
            }
        }

        Assert.True(total >= 44, $"Мостів має бути ≥ 44, зараз {total}.");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Місячний_міст_вибирає_12_місячних_колонок_за_номером_періоду()
    {
        using var model = Load("land-template.model.json");
        var cols = Columns(Tables(model)["T9_04"]);

        foreach (var code in new[] { "Land_Measure_SubstanceConsumptionKgH", "Land_Measure_SubstanceConsumptionKg" })
        {
            var expr = cols[code].GetProperty("expression").GetString()!;
            var months = Regex.Matches(expr, @"\[Period\]\.Sequence = (\d+) \? \[([^\]]+)\]");

            Assert.Equal(11, months.Count);
            for (var i = 0; i < 11; i++)
            {
                Assert.Equal(i + 1, int.Parse(months[i].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
                Assert.Equal(i + 1, cols[months[i].Groups[2].Value].GetProperty("month").GetInt32());
            }
        }
    }

    [Theory]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("T10_05", "CalculationMonth", "[Period].Sequence")]
    [InlineData("T9_03", "Winter", "[Period].Sequence < 4")]
    [InlineData("T6_12", "Land_Agent_Fraction", "[")]
    public void Календарні_аргументи_і_alias_мости_присутні(string table, string name, string fragment)
    {
        using var model = Load("land-template.model.json");
        var c = Columns(Tables(model)[table])[name];

        Assert.Equal("Formula", c.GetProperty("dataType").GetString());
        Assert.Contains(fragment, c.GetProperty("expression").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Недоведені_входи_не_отримали_мостів_а_названі_в_unresolved()
    {
        using var model = Load("land-template.model.json");
        var tables = Tables(model);
        var bridges = model.RootElement.GetProperty("argumentBridges");
        var unresolved = bridges.GetProperty("unresolved");

        // склад газу (HSE400) і DaysInCalcMonth (правило 7.7 доведено лише для 3-5) - без мостів
        Assert.True(unresolved.GetProperty("T9_04").TryGetProperty("CompositionMass", out _));
        Assert.True(unresolved.GetProperty("T9_03").TryGetProperty("DaysInCalcMonth", out var why));
        Assert.Contains("7.7", why.GetString());
        Assert.DoesNotContain("DaysInCalcMonth", Columns(tables["T9_03"]).Keys);
        Assert.DoesNotContain("CompositionMass", Columns(tables["T9_04"]).Keys);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_вхід_методології_таблиці_є_колонкою_мостом_або_названий_у_unresolved()
    {
        using var model = Load("land-template.model.json");
        using var map = Load("land-result-outputs.json");
        var tables = Tables(model);
        var unresolved = model.RootElement.GetProperty("argumentBridges").GetProperty("unresolved");
        var calcTables = new HashSet<string> { "T3_00", "T4_01", "T5_08", "T5_09", "T5_22", "T5_27" };
        var checkedPairs = 0;

        foreach (var m in map.RootElement.GetProperty("methodologies").EnumerateObject())
        {
            foreach (var t in m.Value.GetProperty("tables").EnumerateArray().Select(x => x.GetString()!))
            {
                if (calcTables.Contains(t) || !tables.TryGetValue(t, out var table))
                {
                    continue;
                }

                var cols = Columns(table);
                foreach (var input in m.Value.GetProperty("inputs").EnumerateArray().Select(x => x.GetString()!))
                {
                    checkedPairs++;
                    var named = unresolved.TryGetProperty(t, out var u) && u.TryGetProperty(input, out _);
                    Assert.True(cols.ContainsKey(input) || named, $"{t}: вхід {input} ({m.Name}) без колонки, мосту й запису в unresolved.");
                }
            }
        }

        Assert.True(checkedPairs > 100);
    }
}
