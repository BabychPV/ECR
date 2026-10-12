using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Прод-зразок HSE 2026-10-12 (<c>.sync-local/reports/LAND-PROD-SAMPLE-2026-10-12.md</c>, прогалина 1): типовий
/// документ старої системи довший за ліміт динамічних рядків Excel-шаблону (T3_00 ~788 проти 498, T9_04 368 проти 184,
/// T9_07 182 проти 100), тож реальний документ не вміщувався. Тест стереже, що модель шаблону Land
/// (<c>land-template.model.json</c>, з нього <c>Build-LandTemplate</c> ставить <c>MaxDynamicRows</c>) тримає прод-обсяг із запасом.
/// </summary>
public sealed class LandDynamicRowLimitsTests
{
    /// <summary>Прод-факт зі звіту: таблиця → кількість рядків типового документа.</summary>
    private static readonly Dictionary<string, int> ProdRows = new(StringComparer.Ordinal)
    {
        ["T3_00"] = 788,
        ["T9_04"] = 368,
        ["T9_07"] = 182,
    };

    private static JsonDocument Model()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(
            SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));

    private static IEnumerable<JsonElement> Tables(JsonDocument model)
        => model.RootElement.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray());

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Стеля_динамічних_рядків_вміщує_прод_обсяг_із_запасом()
    {
        using var model = Model();
        // ⚠ Код таблиці не унікальний у моделі (T7_00 є на аркушах 7 і 7.0) — ключуємо лише потрібні.
        var tables = Tables(model)
            .Where(t => ProdRows.ContainsKey(t.GetProperty("code").GetString()!))
            .ToDictionary(t => t.GetProperty("code").GetString()!, StringComparer.Ordinal);
        Assert.Equal(ProdRows.Count, tables.Count);

        foreach (var (code, prodRows) in ProdRows)
        {
            var max = tables[code].GetProperty("maxRows").GetInt32();

            // Запас не менше ~20 %: документ наступного місяця може мати кілька нових одиниць обладнання.
            Assert.True(
                max >= prodRows * 12 / 10,
                $"{code}: maxRows {max} не вміщує прод-обсяг {prodRows} із запасом 20 % (мінімум {prodRows * 12 / 10}).");
        }

        // Вимога замовлення: T3_00 — щонайменше 800 рядків.
        Assert.True(tables["T3_00"].GetProperty("maxRows").GetInt32() >= 800);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Жодна_динамічна_таблиця_не_має_стелі_нижчої_за_рядки_Excel_і_100()
    {
        using var model = Model();

        foreach (var table in Tables(model).Where(t => t.GetProperty("rowMode").GetString() == "Dynamic"))
        {
            var code = table.GetProperty("code").GetString()!;
            var max = table.GetProperty("maxRows").GetInt32();
            var excelRows = table.GetProperty("excel").GetProperty("rowCount").GetInt32();

            Assert.True(max >= 100, $"{code}: maxRows {max} < 100.");
            Assert.True(max >= excelRows, $"{code}: maxRows {max} < рядків Excel {excelRows}.");
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Fixed_таблиці_без_стелі()
    {
        using var model = Model();

        foreach (var table in Tables(model).Where(t => t.GetProperty("rowMode").GetString() == "Fixed"))
        {
            Assert.Equal(JsonValueKind.Null, table.GetProperty("maxRows").ValueKind);
        }
    }
}
