using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// B26 (P-0118-23, рішення людини 18.11, відповідь (a)): методологія <c>ECW_C09_02_01</c> на <c>T7b_00</c> рахує
/// <c>ECW_EC_tons = (@Concentration * @Volume) / k1</c>, але колонок із такими кодами в таблиці не було — банер «бракує
/// входів Concentration/Volume». Тепер: нова місячна колонка введення <c>Volume_M01..M12</c> (м3) одразу після
/// <c>December</c> і два мости-селектори місяця — <c>Concentration</c> (над January…December) і <c>Volume</c> (над
/// Volume_Mmm); у річному шаблоні етап YearMonths робить із них псевдоніми місяця (<see cref="LandYearMonthsLibTests"/>).
/// Модель методології не змінюється.
/// </summary>
/// <remarks>
/// Мутаційні докази (лише локально, 18.11, генератор відпрацював — модель збудовано): у <c>gen_ecr_model.py</c> прибрати
/// <c>'Volume'</c> з <c>DECIDED_MONTH_ARGS</c> і перегенерувати модель — червоніють «міст Volume», «немає в unresolved»,
/// псевдоніми YearMonths і розрахунок (<c>LandT7bVolumeCalculationTests</c>); міст Volume на сусідній місяць
/// (<c>month_expr(mc[1:] + mc[:1])</c>) — червоніють «міст Volume», псевдоніми YearMonths і всі три розрахункові.
/// Зсув самого списку колонок генератор відхиляє сам (SystemExit: місяць джерела ≠ m).
/// </remarks>
public sealed class LandT7bVolumeModelTests
{
    private static readonly string Dir = Path.Combine(SourceTree.Root, "docs", "delivery", "reference-data", "land-template");

    private static readonly string[] MonthNames =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    ];

    private static JsonDocument Load(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, name)));

    private static JsonElement T7b(JsonDocument model)
        => model.RootElement.GetProperty("sheets").EnumerateArray()
            .Where(s => s.GetProperty("code").GetString() == "S07B_WATER_MEASUREMENTS")
            .SelectMany(s => s.GetProperty("tables").EnumerateArray())
            .Single(t => t.GetProperty("code").GetString() == "T7b_00");

    private static List<JsonElement> Columns(JsonElement table) => [.. table.GetProperty("columns").EnumerateArray()];

    private static string Code(JsonElement c) => c.GetProperty("code").GetString()!;

    private static int? Month(JsonElement c)
        => c.TryGetProperty("month", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : null;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "B26")]
    public void Volume_нова_місячна_колонка_введення_в_м3_одразу_після_December()
    {
        using var model = Load("land-template.model.json");
        var cols = Columns(T7b(model));
        var codes = cols.Select(Code).ToList();
        var at = codes.IndexOf("December");

        Assert.True(at >= 0);
        for (var m = 1; m <= 12; m++)
        {
            var c = cols[at + m];
            Assert.Equal($"Volume_M{m:00}", Code(c));
            Assert.Equal("Decimal", c.GetProperty("dataType").GetString());
            Assert.Equal("m3", c.GetProperty("unit").GetString());
            Assert.Equal(m, Month(c));
            Assert.False(c.GetProperty("readOnly").GetBoolean());
            Assert.False(c.GetProperty("hidden").GetBoolean());
            Assert.Contains("P-0118-23", c.GetProperty("decision").GetString(), StringComparison.Ordinal);
        }

        // концентрація лишилась тією самою (жодних перейменувань наявних колонок)
        Assert.Equal(MonthNames, codes.Where(x => MonthNames.Contains(x)));
        Assert.All(cols.Where(c => MonthNames.Contains(Code(c))), c => Assert.Equal("mg/l", c.GetProperty("unit").GetString()));
    }

    [Theory]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "B26")]
    [InlineData("Concentration", "mg/l", "")]
    [InlineData("Volume", "m3", "Volume_M")]
    public void Аргумент_ECW_C09_02_01_це_міст_що_вибирає_колонку_місяця_розрахунку(string argument, string unit, string prefix)
    {
        using var model = Load("land-template.model.json");
        var cols = Columns(T7b(model));
        var byCode = cols.ToDictionary(Code, StringComparer.Ordinal);
        var bridge = byCode[argument];

        Assert.Equal("Formula", bridge.GetProperty("dataType").GetString());
        Assert.True(bridge.GetProperty("readOnly").GetBoolean());
        Assert.Equal(unit, bridge.GetProperty("unit").GetString());

        var sources = SelectorSources(bridge.GetProperty("expression").GetString()!);
        var expected = Enumerable.Range(1, 12).Select(m => prefix.Length == 0 ? MonthNames[m - 1] : $"{prefix}{m:00}").ToList();
        Assert.Equal(expected, sources);
        for (var m = 1; m <= 12; m++)
        {
            Assert.Equal(m, Month(byCode[sources[m - 1]]));
            Assert.Equal("Decimal", byCode[sources[m - 1]].GetProperty("dataType").GetString());
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "B26")]
    public void Входи_ECW_C09_02_01_оголошені_і_зв_язані_а_не_в_unresolved()
    {
        using var model = Load("land-template.model.json");
        using var outputs = Load("land-result-outputs.json");
        var bridges = model.RootElement.GetProperty("argumentBridges");

        var inputs = outputs.RootElement.GetProperty("supplementalMethodologies").GetProperty("ECW_C09_02_01")
            .GetProperty("inputs").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Equal(["Concentration", "Volume"], inputs);

        var bridged = bridges.GetProperty("bridged").GetProperty("T7b_00").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Equal(["Concentration", "Volume"], bridged);
        Assert.False(bridges.GetProperty("unresolved").TryGetProperty("T7b_00", out _));

        // методологія — та сама, вихід — той самий (модель методології не змінюється)
        var calc = T7b(model).GetProperty("calc");
        Assert.Equal(["ECW_C09_02_01"], calc.GetProperty("methodologies").EnumerateArray().Select(x => x.GetString()!));
        Assert.Equal(["ECW_EC_tons"], calc.GetProperty("outputs").EnumerateArray().Select(x => x.GetString()!));
    }

    /// <summary>Джерела селектора «[Period].Sequence = 1 ? [A] : … : [L]» за місяцями (та сама форма, що Get-LandSelectorSources).</summary>
    internal static List<string> SelectorSources(string expression)
    {
        var branches = Regex.Matches(expression, @"\[Period\]\.Sequence = (\d+) \? \[([^\]]+)\] : ");
        Assert.Equal(11, branches.Count);
        var result = new List<string>();
        for (var i = 0; i < 11; i++)
        {
            Assert.Equal((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), branches[i].Groups[1].Value);
            result.Add(branches[i].Groups[2].Value);
        }

        var last = Regex.Match(expression, @": \[([^\]]+)\]$");
        Assert.True(last.Success);
        result.Add(last.Groups[1].Value);
        return result;
    }
}
