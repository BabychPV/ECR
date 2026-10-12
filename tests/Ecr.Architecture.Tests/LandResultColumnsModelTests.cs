using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// LP-08 (інвентар LAND-PARITY 12.10): у шаблоні Land не було колонок результатів <c>ECW_EC_*</c> для таблиць аркушів
/// 6 (6_11/6_12), 9, 9a, 10, 10a, 11, 11a-d, тож вихід опублікованих методологій C08/C11/C12 не мав куди прив'язатись.
/// Колонки результатів — тієї ж форми, що й у таблицях аркушів 3-5: <c>Calculated</c>, лише читання, 28/12, <c>t</c> для
/// <c>ECW_EC_tons_*</c> і <c>g/s</c> для <c>ECW_EC_gsec_*</c>, код колонки = код виходу методології. Перелік виходів і
/// таблиць — <c>land-result-outputs.json</c> (структура пакета методологій AF x таблиці правил прода, без значень).
/// </summary>
public sealed class LandResultColumnsModelTests
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
        => table.GetProperty("columns").EnumerateArray()
            .ToDictionary(c => c.GetProperty("code").GetString()!, c => c);

    /// <summary>Таблиця -> виходи й методології зі зіставлення (як це робить генератор).</summary>
    private static Dictionary<string, (SortedSet<string> Outputs, SortedSet<string> Methodologies)> Mapping()
    {
        using var doc = Load("land-result-outputs.json");
        var map = new Dictionary<string, (SortedSet<string>, SortedSet<string>)>();
        foreach (var m in doc.RootElement.GetProperty("methodologies").EnumerateObject())
        {
            foreach (var table in m.Value.GetProperty("tables").EnumerateArray().Select(t => t.GetString()!))
            {
                if (!map.TryGetValue(table, out var e))
                {
                    e = (new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal));
                    map[table] = e;
                }

                foreach (var o in m.Value.GetProperty("outputs").EnumerateArray())
                {
                    e.Item1.Add(o.GetString()!);
                }

                e.Item2.Add(m.Name);
            }
        }

        return map;
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожна_таблиця_зіставлення_що_є_в_моделі_має_усі_колонки_результатів_Calculated_28_12()
    {
        using var model = Load("land-template.model.json");
        var tables = Tables(model);
        var covered = 0;

        foreach (var (table, e) in Mapping())
        {
            if (!tables.TryGetValue(table, out var t))
            {
                continue; // таблиці, яких у шаблоні ще немає (LP-09), перелічені в resultOutputsPending
            }

            covered++;
            var cols = Columns(t);
            foreach (var o in e.Outputs)
            {
                Assert.True(cols.TryGetValue(o, out var c), $"{table}: немає колонки результату {o}.");
                Assert.Equal("Calculated", c.GetProperty("dataType").GetString());
                Assert.True(c.GetProperty("readOnly").GetBoolean(), $"{table}.{o}: має бути лише для читання.");
                Assert.Equal(28, c.GetProperty("precision").GetInt32());
                Assert.Equal(12, c.GetProperty("scale").GetInt32());
                Assert.Equal(o.Contains("_tons_", StringComparison.Ordinal) ? "t" : "g/s", c.GetProperty("unit").GetString());
                Assert.False(c.GetProperty("hidden").GetBoolean(), $"{table}.{o}: колонка результату має бути видимою.");
            }

            var calc = t.GetProperty("calc");
            Assert.Equal(e.Outputs, calc.GetProperty("outputs").EnumerateArray().Select(x => x.GetString()!).ToList());
            Assert.Equal(e.Methodologies, calc.GetProperty("methodologies").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToList());
        }

        // 43 таблиці + 9a_17a/21a/21b (LP-09: після виправлення кодів таблиці зіставлення знайшлись у моделі)
        Assert.True(covered >= 46, $"Колонки результатів мають бути у ≥ 46 таблицях, зараз {covered}.");
    }

    [Theory]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("T6_11", "ECW_EC_tons_331", "ECW_EC_gsec_331")]                 // C08_04_02
    [InlineData("T6_12", "ECW_EC_tons_2902", "ECW_EC_gsec_2908")]               // C08_04_03
    [InlineData("T9a_25", "ECW_EC_tons_415", "ECW_EC_gsec_301")]                // C11_06
    [InlineData("T9a_23", "ECW_EC_tons_XXX", "ECW_EC_gsec_XXX")]                // C11_17
    [InlineData("T10_01", "ECW_EC_tons_XXX", "ECW_EC_gsec_XXX")]                // C12_03
    [InlineData("T11_01", "ECW_EC_tons_XXX", "ECW_EC_gsec_XXX")]                // C12_03
    [InlineData("T11a_01", "ECW_EC_tons_XXX_type1", "ECW_EC_gsec_XXX_type2")]   // C12_01
    [InlineData("T11d_01", "ECW_EC_tons_XXX_type2", "ECW_EC_gsec_XXX_type1")]   // C12_01
    public void Опубліковані_методології_C08_C11_C12_мають_куди_прив_язати_вихід(string table, string a, string b)
    {
        using var model = Load("land-template.model.json");
        var cols = Columns(Tables(model)[table]);

        Assert.Contains(a, cols.Keys);
        Assert.Contains(b, cols.Keys);
        Assert.Equal("Calculated", cols[a].GetProperty("dataType").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Колонки_результатів_лише_для_виходів_tons_і_gsec_без_вигаданих_додатків()
    {
        using var map = Load("land-result-outputs.json");
        var rx = new Regex("^ECW_EC_(tons|gsec)_[A-Za-z0-9_]+$");

        foreach (var m in map.RootElement.GetProperty("methodologies").EnumerateObject())
        {
            Assert.Matches(@"^ECW_C(08|09|11|12)_", m.Name);
            foreach (var o in m.Value.GetProperty("outputs").EnumerateArray())
            {
                Assert.Matches(rx, o.GetString()!);
            }
        }

        // проміжні ECW_EC_SumXiDivMi/Ri (C11_11_02) не є результатом емісії і колонок не мають
        using var model = Load("land-template.model.json");
        var excluded = map.RootElement.GetProperty("excludedOutputs").EnumerateObject()
            .SelectMany(p => p.Value.EnumerateArray().Select(x => x.GetString()!)).ToHashSet();
        Assert.NotEmpty(excluded);
        foreach (var (table, t) in Tables(model))
        {
            foreach (var code in Columns(t).Keys)
            {
                Assert.DoesNotContain(code, excluded);
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Таблиці_без_методології_в_зіставленні_не_отримали_колонок_результатів()
    {
        using var model = Load("land-template.model.json");
        var mapped = Mapping().Keys.ToHashSet();
        var upstream = new HashSet<string> { "T3_00", "T4_01", "T5_08", "T5_09", "T5_22", "T5_27", "T7b_00" }; // CALC генератора + supplemental C09_02_01

        foreach (var (table, t) in Tables(model))
        {
            if (mapped.Contains(table) || upstream.Contains(table))
            {
                continue;
            }

            Assert.DoesNotContain(Columns(t).Keys, c => c.StartsWith("ECW_EC_", StringComparison.Ordinal));
        }

        // 7a: методологій із правилами для T7a_* немає — колонок не вигадуємо (T7b_00: ECW_EC_tons від C09_02_01, LP-09/4)
        var tables = Tables(model);
        foreach (var code in tables.Keys.Where(k => k.StartsWith("T7a_", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(Columns(tables[code]).Keys, c => c.StartsWith("ECW_EC_", StringComparison.Ordinal));
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Таблиці_зі_зіставлення_яких_у_моделі_немає_названі_в_resultOutputsPending()
    {
        using var model = Load("land-template.model.json");
        var tables = Tables(model);
        var pending = model.RootElement.GetProperty("resultOutputsPending").EnumerateObject().Select(p => p.Name).ToHashSet();

        foreach (var table in Mapping().Keys)
        {
            Assert.True(
                tables.ContainsKey(table) ^ pending.Contains(table),
                $"{table}: має бути або в моделі, або в resultOutputsPending (не в обох і не в жодній).");
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Build_прив_язує_виходи_до_колонок_результатів_а_Verify_перевіряє_їх_тип()
    {
        var build = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "land", "Build-LandTemplate.ps1"));
        var verify = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "land", "Verify-LandTemplate.ps1"));

        Assert.Contains("function Bind-ResultColumns", build);
        Assert.Contains("Bind-ResultColumns $mid", build);
        Assert.Contains("/bindings/$cid/$o", build);
        Assert.Contains("[bool]$vi.active", build); // активна прив'язка до чернетки валить перерахунки
        Assert.Contains("колонка результату", verify);
    }
}
