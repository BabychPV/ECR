using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// B26 (P-0118-24/25, рішення людини 18.11; замінює (a) P-0118-23): методологія <c>ECW_C09_02_01</c> на <c>T7b_00</c>
/// рахує <c>ECW_EC_tons = (@Concentration * @Volume) / k1</c>. <c>Concentration</c> — місячна колонка концентрації рядка
/// (селектор над January…December); <c>Volume</c> — комірка рядка <c>S3_9</c> (об'єм із Water Report) у колонці ТОГО
/// САМОГО місяця, одна для всіх рядків речовин (міст із посиланням на фіксований рядок). Рядок S3_9 сам не рахується:
/// фільтр D-425 — колонка <c>Land_Status</c> (LAND_GENERIC_EQUIPSTATUS), Running на рядках речовин, S3_9 без статусу,
/// <c>ECW_C09_02_01</c> у <c>runningOnly</c>. Ручних колонок Volume_Mmm (варіант (a)) немає. Модель методології не змінюється.
/// </summary>
/// <remarks>
/// Мутаційні докази (лише локально; генератор відпрацював = «build OK», модель перегенеровано): (m1) у
/// <c>DECIDED_ROW_ARGS</c> рядок <c>S2_7</c> замість <c>S3_9</c> — червоніє «Volume читає S3_9»; (m2) місяці зсунуто
/// (<c>MONTHS[1:] + MONTHS[:1]</c>) — червоніє той самий тест.
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
    public void Ручних_колонок_Volume_немає_концентрація_незмінна()
    {
        using var model = Load("land-template.model.json");
        var cols = Columns(T7b(model));
        var codes = cols.Select(Code).ToList();

        Assert.DoesNotContain(codes, c => c.StartsWith("Volume_M", StringComparison.Ordinal));
        Assert.Equal(MonthNames, codes.Where(x => MonthNames.Contains(x)));
        Assert.All(cols.Where(c => MonthNames.Contains(Code(c))), c => Assert.Equal("mg/l", c.GetProperty("unit").GetString()));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "B26")]
    public void Volume_читає_S3_9_у_колонці_місяця_розрахунку_і_не_є_селектором()
    {
        using var model = Load("land-template.model.json");
        var table = T7b(model);
        var byCode = Columns(table).ToDictionary(Code, StringComparer.Ordinal);
        var bridge = byCode["Volume"];

        Assert.Equal("Formula", bridge.GetProperty("dataType").GetString());
        Assert.True(bridge.GetProperty("readOnly").GetBoolean());
        Assert.Equal("m3", bridge.GetProperty("unit").GetString());
        Assert.Equal("S3_9", bridge.GetProperty("sourceRow").GetString());

        var expression = bridge.GetProperty("expression").GetString()!;

        // у дужках: YearMonths класифікує як змінну формулу (Volume_Mmm + псевдоніми), а не як селектор
        Assert.StartsWith("(", expression, StringComparison.Ordinal);

        var sources = RowSources(expression);
        Assert.Equal(12, sources.Count);
        for (var m = 1; m <= 12; m++)
        {
            Assert.Equal(("T7b_00", "S3_9", MonthNames[m - 1]), sources[m - 1]);
            Assert.Equal(m, Month(byCode[sources[m - 1].Column]));
        }

        var rowKeys = table.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("key").GetString()).ToList();
        Assert.Contains("S3_9", rowKeys);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "B26")]
    public void Concentration_селектор_колонки_місяця_свого_рядка()
    {
        using var model = Load("land-template.model.json");
        var byCode = Columns(T7b(model)).ToDictionary(Code, StringComparer.Ordinal);
        var bridge = byCode["Concentration"];

        Assert.Equal("Formula", bridge.GetProperty("dataType").GetString());
        Assert.Equal("mg/l", bridge.GetProperty("unit").GetString());
        var expression = bridge.GetProperty("expression").GetString()!;
        var branches = Regex.Matches(expression, @"\[Period\]\.Sequence = (\d+) \? \[([^\]]+)\] : ");
        Assert.Equal(MonthNames[..11], branches.Select(b => b.Groups[2].Value));
        Assert.EndsWith(": [December]", expression, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "B26")]
    public void Статус_D425_Running_на_рядках_речовин_S3_9_без_статусу_і_методологія_в_runningOnly()
    {
        using var model = Load("land-template.model.json");
        var table = T7b(model);
        var status = Columns(table).Single(c => Code(c) == "Land_Status");

        Assert.Equal("Lookup", status.GetProperty("dataType").GetString());
        Assert.Equal("LAND_GENERIC_EQUIPSTATUS", status.GetProperty("registry").GetString());

        var registry = model.RootElement.GetProperty("registries").EnumerateArray()
            .Single(r => r.GetProperty("code").GetString() == "LAND_GENERIC_EQUIPSTATUS");
        Assert.Contains("RUNNING", registry.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("code").GetString()));

        var presets = table.GetProperty("statusPresets");
        Assert.Equal("Land_Status", presets.GetProperty("column").GetString());
        Assert.Equal("RUNNING", presets.GetProperty("entry").GetString());
        Assert.Equal(["S1_6", "S2_7"], presets.GetProperty("rows").EnumerateArray().Select(x => x.GetString()!));
        Assert.Equal(["S3_9"], presets.GetProperty("emptyRows").EnumerateArray().Select(x => x.GetString()!));

        using var filter = JsonDocument.Parse(File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "land", "land-row-filter.json")));
        Assert.Equal("LAND_GENERIC_EQUIPSTATUS:Running", filter.RootElement.GetProperty("filter").GetString());
        Assert.Contains("ECW_C09_02_01", filter.RootElement.GetProperty("runningOnly").EnumerateArray().Select(x => x.GetString()));
        Assert.False(filter.RootElement.GetProperty("exempt").TryGetProperty("ECW_C09_02_01", out _));
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

        // ✎ P-1123-4: ECW_EC_tons рахує формула таблиці - методологію від таблиці відв'язано (calc немає).
        Assert.False(T7b(model).TryGetProperty("calc", out _));
    }

    /// <summary>
    /// ✎ P-1123-4 (людина 23.11): T7b_00.ECW_EC_tons - формула таблиці з ТІЄЮ САМОЮ семантикою, що й методологія
    /// ECW_C09_02_01: (C * V) / 1e6; лише рядки зі статусом RUNNING (фільтр D-425: S3_9 без результату); бракує C або V -
    /// результату немає (не нуль). Мутаційний доказ: прибрати умову статусу або перевірку NULL у DECIDED_TABLE_FORMULAS
    /// (gen_ecr_model.py) і перегенерувати модель - тест червоний.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void ECW_EC_tons_формула_таблиці_з_умовою_Running_і_без_результату_при_порожніх_аргументах()
    {
        using var model = Load("land-template.model.json");
        var column = T7b(model).GetProperty("columns").EnumerateArray()
            .Single(c => c.GetProperty("code").GetString() == "ECW_EC_tons");

        Assert.Equal("Formula", column.GetProperty("dataType").GetString());
        Assert.Equal(
            "IF(REGFIELD([Land_Status], 'NAME') = 'Running' AND NOT ([Concentration] = NULL OR [Volume] = NULL),"
            + " [Concentration], NULL) * [Volume] / 1000000",
            column.GetProperty("expression").GetString());

        var statusEntries = model.RootElement.GetProperty("registries").EnumerateArray()
            .Single(r => r.GetProperty("code").GetString() == "LAND_GENERIC_EQUIPSTATUS")
            .GetProperty("entries").EnumerateArray().ToList();
        var running = Assert.Single(statusEntries, e => e.GetProperty("code").GetString() == "RUNNING");
        Assert.Equal("Running", running.GetProperty("name").GetString());
    }

    /// <summary>Джерела «([Period].Sequence = 1 ? [T].[R].[C] : … : [T].[R].[C])» за місяцями: (таблиця, рядок, колонка).</summary>
    internal static List<(string Table, string Row, string Column)> RowSources(string expression)
        => [.. Regex.Matches(expression, @"\[([^\]]+)\]\.\[([^\]]+)\]\.\[([^\]]+)\]")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value))];
}
