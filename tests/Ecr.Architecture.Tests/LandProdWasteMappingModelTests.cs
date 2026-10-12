using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Прод-зразок HSE 2026-10-12 (прогалина 2): прод-відходи <c>AF_Waste_Land_08</c> нормалізовані (рядок = елемент звіту ×
/// код відходу), а шаблон Land <c>T8_00</c> — широкий (колонка <c>W_…</c> на тип відходу). Модель має таблицю
/// відповідності <c>prodMapping.waste</c>: код відходу → колонка, ключ рядка, значення. Тест перевіряє її на цілісність
/// щодо самої моделі (довідник відходів, колонки, рядки місяців) і незалежно від генератора звіряє назву колонки.
/// </summary>
public sealed partial class LandProdWasteMappingModelTests
{
    /// <summary>Елементи звіту, що були в прод-зразку (місяць 1): <c>Land_ItemId</c>.</summary>
    private static readonly string[] ProdSampleItems = ["8001001", "8002001", "8002002", "8003001", "8004001", "8004002"];

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlnum();

    private static string Norm(string? text) => NonAlnum().Replace((text ?? string.Empty).ToLowerInvariant(), string.Empty);

    private static JsonDocument Model()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(
            SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));

    private static JsonElement Table(JsonDocument model, string sheet, string table)
        => model.RootElement.GetProperty("sheets").EnumerateArray()
            .Single(s => s.GetProperty("code").GetString() == sheet)
            .GetProperty("tables").EnumerateArray()
            .Single(t => t.GetProperty("code").GetString() == table);

    private static JsonElement Registry(JsonDocument model, string code)
        => model.RootElement.GetProperty("registries").EnumerateArray()
            .Single(r => r.GetProperty("code").GetString() == code);

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_відхід_довідника_має_рівно_одну_W_колонку_і_навпаки()
    {
        using var model = Model();
        var waste = model.RootElement.GetProperty("prodMapping").GetProperty("waste");
        var table = Table(model, waste.GetProperty("template").GetProperty("sheet").GetString()!, "T8_00");
        var wColumns = table.GetProperty("columns").EnumerateArray()
            .Where(c => c.GetProperty("code").GetString()!.StartsWith("W_", StringComparison.Ordinal))
            .ToList();
        var substances = Registry(model, "LAND_WASTE_SUBSTANCE").GetProperty("entries").EnumerateArray()
            .ToDictionary(e => e.GetProperty("fields").GetProperty("SUBSTANCE_CODE").GetString()!);

        var map = waste.GetProperty("columnByWasteCode").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);

        Assert.Equal(38, wColumns.Count);
        Assert.Equal(substances.Keys.Order(StringComparer.Ordinal), map.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            wColumns.Select(c => c.GetProperty("code").GetString()!).Order(StringComparer.Ordinal),
            map.Values.Order(StringComparer.Ordinal));

        // Незалежна від генератора звірка за назвою: заголовок колонки починається з англійської назви відходу.
        var columnByCode = wColumns.ToDictionary(c => c.GetProperty("code").GetString()!);

        foreach (var (wasteCode, columnCode) in map)
        {
            var header = Norm(columnByCode[columnCode].GetProperty("headerEn").GetString());
            var name = Norm(substances[wasteCode].GetProperty("fields").GetProperty("NAME_EN").GetString());

            Assert.True(header.StartsWith(name, StringComparison.Ordinal), $"{wasteCode}: колонка {columnCode} не відповідає назві відходу.");
            Assert.Equal("Decimal", columnByCode[columnCode].GetProperty("dataType").GetString());
            Assert.Equal("t", columnByCode[columnCode].GetProperty("unit").GetString());
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Номер_відходу_Land_WasteNo_дорівнює_порядку_W_колонки()
    {
        using var model = Model();
        var waste = model.RootElement.GetProperty("prodMapping").GetProperty("waste");
        var table = Table(model, "S08_WASTE_REPORT", "T8_00");
        var order = table.GetProperty("columns").EnumerateArray()
            .Select(c => c.GetProperty("code").GetString()!)
            .Where(c => c.StartsWith("W_", StringComparison.Ordinal))
            .ToList();
        var columnByCode = waste.GetProperty("columnByWasteCode").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);

        foreach (var p in waste.GetProperty("wasteNoByCode").EnumerateObject())
        {
            Assert.Equal(order.IndexOf(columnByCode[p.Name]) + 1, p.Value.GetInt32());
        }

        Assert.Equal(
            Enumerable.Range(1, 38),
            waste.GetProperty("wasteNoByCode").EnumerateObject().Select(p => p.Value.GetInt32()).Order());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Ключі_рядків_елементів_є_в_T8_00_на_всі_12_місяців_і_збігаються_з_довідником()
    {
        using var model = Model();
        var waste = model.RootElement.GetProperty("prodMapping").GetProperty("waste");
        var rowKeys = Table(model, "S08_WASTE_REPORT", "T8_00").GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("key").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var items = waste.GetProperty("itemsInAmountTable").EnumerateArray().Select(e => e.GetString()!).ToList();
        var registryItems = Registry(model, "LAND_WASTE_REPORT_ENTRY").GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("fields").GetProperty("ENTRY_ID").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal("M{month:02d}_{Land_ItemId}", waste.GetProperty("rowKey").GetString());

        foreach (var item in ProdSampleItems)
        {
            Assert.Contains(item, items);
        }

        foreach (var item in items)
        {
            Assert.Contains(item, registryItems);

            for (var month = 1; month <= 12; month++)
            {
                Assert.Contains($"M{month:00}_{item}", rowKeys);
            }
        }

        // Групи з прод-зразка (Land_GroupId) — групи довідника елементів.
        var groups = waste.GetProperty("groupItems").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(e => e.GetString()!).ToList());

        foreach (var group in new[] { "8001000", "8002000", "8003000", "8004000" })
        {
            Assert.True(groups.ContainsKey(group), $"Група {group} відсутня.");
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Недоведене_названо_а_не_замовчано()
    {
        using var model = Model();
        var notProven = model.RootElement.GetProperty("prodMapping").GetProperty("waste").GetProperty("notProven")
            .EnumerateArray().Select(e => e.GetProperty("prodColumn").GetString()).ToList();

        Assert.Contains("Land_Waste_Name_Area", notProven);
    }
}
