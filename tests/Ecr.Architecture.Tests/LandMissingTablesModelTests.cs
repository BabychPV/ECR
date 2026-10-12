using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// LP-09 (інвентар LAND-PARITY 12.10): рули прода для AF_Air_Land_ProcessUnits_9a_17a/21a/21b не знаходили таблиць у
/// шаблоні Land. Таблиці в Excel є (аркуш «9a. Process units report»), але їхні заголовки «9a.17а» (кирилична а) і
/// «9a.21a» (двічі) не розпізнавались генератором, і код брався з порядкового номера блоку (T9a_03, T9a_06, T9a_07).
/// 7b: таблиця є (єдина таблиця аркуша, код T7b_00); у скрипта завантаження рулів AF_Water_Land_07b мапилась на «T7b».
/// </summary>
public sealed class LandMissingTablesModelTests
{
    private static readonly string[] ExpectedCodes =
    {
        "T9a_16", "T9a_17", "T9a_17a", "T9a_19", "T9a_21", "T9a_21a", "T9a_21b", "T9a_22", "T9a_23",
        "T9a_24", "T9a_25", "T9a_26", "T9a_27", "T9a_28", "T9a_29", "T9a_30",
    };

    private static JsonDocument Model()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(
            SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));

    private static JsonElement Sheet(JsonDocument model, string code)
        => model.RootElement.GetProperty("sheets").EnumerateArray().Single(s => s.GetProperty("code").GetString() == code);

    private static JsonElement Table(JsonDocument model, string sheet, string code)
        => Sheet(model, sheet).GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("code").GetString() == code);

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Аркуш_9a_має_16_таблиць_із_кодами_за_номерами_секцій_Excel()
    {
        using var model = Model();
        var codes = Sheet(model, "S09A_PROCESS_UNITS").GetProperty("tables").EnumerateArray()
            .Select(t => t.GetProperty("code").GetString()!).ToList();

        Assert.Equal(ExpectedCodes, codes);
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("T9a_17a", 73, 9, 134)]   // «9a.17а» (кирилична а): рядок шапки, рядків даних, фізичних колонок Excel
    [InlineData("T9a_21a", 110, 1, 110)]  // перший блок «9a.21a»
    [InlineData("T9a_21b", 115, 3, 122)]  // другий блок із тим самим заголовком «9a.21a» (прод: _9a_21b); 122 = як у 9a.22
    public void Таблиці_9a_17a_21a_21b_відповідають_блокам_Excel(string code, int headerRow, int rows, int physicalColumns)
    {
        using var model = Model();
        var table = Table(model, "S09A_PROCESS_UNITS", code);
        var excel = table.GetProperty("excel");

        Assert.Equal("MonthsInColumns", table.GetProperty("layout").GetString());
        Assert.Equal("Dynamic", table.GetProperty("rowMode").GetString());
        Assert.Equal(headerRow, excel.GetProperty("headerRow").GetInt32());
        Assert.Equal(rows, excel.GetProperty("rowCount").GetInt32());
        Assert.Equal(physicalColumns, excel.GetProperty("physicalColumns").GetInt32());
        Assert.NotEmpty(table.GetProperty("columns").EnumerateArray());
    }

    [Theory]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("T9a_03")]
    [InlineData("T9a_06")]
    [InlineData("T9a_07")]
    public void Порядкові_коди_блоків_з_літерою_в_заголовку_зникли(string legacyCode)
    {
        using var model = Model();
        var codes = model.RootElement.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray())
            .Select(t => t.GetProperty("code").GetString()!);

        Assert.DoesNotContain(legacyCode, codes);
    }

    [Theory]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("T9a_17a", "ECW_EC_tons_333_type1")]  // C11_11_02
    [InlineData("T9a_21a", "ECW_EC_tons_331")]        // C11_15
    [InlineData("T9a_21b", "ECW_EC_gsec_XXX_type2")]  // C11_14, C11_16
    public void Нові_таблиці_мають_колонки_результатів_методологій(string table, string output)
    {
        using var model = Model();
        var columns = Table(model, "S09A_PROCESS_UNITS", table).GetProperty("columns").EnumerateArray()
            .ToDictionary(c => c.GetProperty("code").GetString()!, c => c);

        Assert.Equal("Calculated", columns[output].GetProperty("dataType").GetString());
        Assert.Empty(model.RootElement.GetProperty("resultOutputsPending").EnumerateObject());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Таблиця_7b_одна_і_має_код_T7b_00()
    {
        using var model = Model();
        var tables = Sheet(model, "S07B_WATER_MEASUREMENTS").GetProperty("tables").EnumerateArray().ToList();

        Assert.Single(tables);
        Assert.Equal("T7b_00", tables[0].GetProperty("code").GetString());
        Assert.Equal("Fixed", tables[0].GetProperty("rowMode").GetString());

        // вихід C09_02_01: одна формула ECW_EC_tons (т), назва без компонента; взято зі стенда, у AF XML методології немає
        var column = tables[0].GetProperty("columns").EnumerateArray()
            .Single(c => c.GetProperty("code").GetString() == "ECW_EC_tons");
        Assert.Equal("Calculated", column.GetProperty("dataType").GetString());
        Assert.Equal("t", column.GetProperty("unit").GetString());
        Assert.True(column.GetProperty("readOnly").GetBoolean());
        Assert.Contains("ECW_C09_02_01", tables[0].GetProperty("calc").GetProperty("methodologies").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Генератор_розпізнає_літеру_після_номера_секції_і_розрізняє_дубль_заголовка()
    {
        var gen = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "land", "model-gen", "gen_ecr_model.py"));
        var map = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "land", "model-gen", "prod_mapping.py"));

        Assert.Contains(@"([a-d\u0430])?(?!\w)", gen);
        Assert.Contains("tcode[:-1] + 'b'", gen);
        Assert.Contains("AF_Air_Land_ProcessUnits_9a_", map);
        Assert.Contains("'T7b_00'", map);
    }
}
