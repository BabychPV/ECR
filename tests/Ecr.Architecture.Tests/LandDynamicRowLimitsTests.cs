using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Прод-зразок HSE 2026-10-12 (<c>.sync-local/reports/LAND-PROD-SAMPLE-2026-10-12.md</c>, прогалина 1): типовий
/// документ старої системи довший за ліміт динамічних рядків Excel-шаблону (T3_00, T9_04, T9_07), тож реальний
/// документ не вміщувався. Числа прод-обсягу в git не зберігаються (політика D-392: дані не в git) - тест стереже
/// лише стелі, обрані за звітом із запасом. Модель шаблону Land (<c>land-template.model.json</c>, з нього
/// <c>Build-LandTemplate</c> ставить <c>MaxDynamicRows</c>) мусить їх тримати.
/// </summary>
public sealed class LandDynamicRowLimitsTests
{
    /// <summary>Мінімальні стелі <c>maxRows</c>, обрані за типовим прод-документом із запасом (без самих прод-чисел).</summary>
    private static readonly Dictionary<string, int> MinCeilings = new(StringComparer.Ordinal)
    {
        ["T3_00"] = 1000,
        ["T9_04"] = 500,
        ["T9_07"] = 250,
    };

    private static JsonDocument Model()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(
            SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));

    private static IEnumerable<JsonElement> Tables(JsonDocument model)
        => model.RootElement.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray());

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Стеля_динамічних_рядків_не_нижча_за_обрану_для_типового_прод_документа()
    {
        using var model = Model();
        // ⚠ Код таблиці не унікальний у моделі (T7_00 є на аркушах 7 і 7.0) — ключуємо лише потрібні.
        var tables = Tables(model)
            .Where(t => MinCeilings.ContainsKey(t.GetProperty("code").GetString()!))
            .ToDictionary(t => t.GetProperty("code").GetString()!, StringComparer.Ordinal);
        Assert.Equal(MinCeilings.Count, tables.Count);

        foreach (var (code, ceiling) in MinCeilings)
        {
            var max = tables[code].GetProperty("maxRows").GetInt32();

            // Стеля обрана із запасом над типовим прод-документом: наступний місяць може мати нові одиниці обладнання.
            Assert.True(max >= ceiling, $"{code}: maxRows {max} нижчий за обрану стелю {ceiling}.");
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
