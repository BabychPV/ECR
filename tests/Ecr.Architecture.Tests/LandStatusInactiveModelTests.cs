using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// CELL-STYLE L6 / рішення людини 12.10 17:35Z: «Inactive» (заливка 00CCFF) у відтвореному Land.
/// У xlsm слово «Inactive» є ЛИШЕ у формулах умовного форматування (142 правила
/// <c>N($B5="Inactive")</c>, dxf indexed 40 = 00CCFF), у списку статусів
/// <c>Generic_EquipStatus DropdownList!A5:A6</c> його немає. Рішення: запис <c>INACTIVE</c> у довіднику моделі й
/// правила «за статусом рядка» (<c>conditionalFormatsRowStatus</c>) на таблицю зі статусною колонкою B.
/// Правила на колонці C аркушів 9a/11 НЕ доведені (що в C — невідомо) і лежать окремо
/// (<c>conditionalFormatsStatusUnproven</c>). Тест читає лише файл моделі; без перегенерації моделі
/// (<c>tools/land/model-gen/run-model-gen.ps1</c>) він червоний: ключів немає.
/// </summary>
public sealed class LandStatusInactiveModelTests
{
    private const string StatusRegistry = "LAND_GENERIC_EQUIPSTATUS";

    private static JsonDocument Model()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(
            SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));

    private static IEnumerable<JsonElement> Tables(JsonElement root)
        => root.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray());

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Довідник_статусів_має_Running_Removed_і_Inactive_без_дублів()
    {
        using var doc = Model();
        var registry = doc.RootElement.GetProperty("registries").EnumerateArray()
            .Single(r => r.GetProperty("code").GetString() == StatusRegistry);
        var codes = registry.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("code").GetString()!).ToList();

        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("RUNNING", codes);
        Assert.Contains("REMOVED", codes);
        Assert.Contains("INACTIVE", codes);

        // Назви — за сусідніми записами: en = name, ru порожнє, як у Running/Removed.
        var inactive = registry.GetProperty("entries").EnumerateArray()
            .Single(e => e.GetProperty("code").GetString() == "INACTIVE");
        Assert.Equal("Inactive", inactive.GetProperty("name").GetString());
        Assert.Equal("Inactive", inactive.GetProperty("en").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Правила_Inactive_прив_язані_до_статусної_колонки_таблиці_з_довідником_і_мають_колір_00CCFF()
    {
        using var doc = Model();
        // Код таблиці не унікальний у моделі (T7_00 є на двох аркушах): ключ — (аркуш, таблиця).
        var tables = doc.RootElement.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray()
                .Select(t => (Sheet: s.GetProperty("code").GetString()!, Table: t.GetProperty("code").GetString()!, Element: t)))
            .ToDictionary(x => (x.Sheet, x.Table), x => x.Element);
        var rules = doc.RootElement.GetProperty("conditionalFormatsRowStatus").EnumerateArray().ToList();

        Assert.NotEmpty(rules);
        foreach (var rule in rules)
        {
            var table = tables[(rule.GetProperty("sheet").GetString()!, rule.GetProperty("table").GetString()!)];
            var status = table.GetProperty("columns").EnumerateArray()
                .Single(c => c.GetProperty("code").GetString() == rule.GetProperty("statusColumn").GetString());

            Assert.Equal("Inactive", rule.GetProperty("value").GetString());
            Assert.Equal("text", rule.GetProperty("valueKind").GetString());
            Assert.Equal("#00CCFF", rule.GetProperty("backgroundHex").GetString());
            Assert.Equal("Lookup", status.GetProperty("dataType").GetString());
            Assert.Equal(StatusRegistry, status.GetProperty("registry").GetString());
            // Лише доведена колонка B (статус таблиці в xlsm).
            Assert.Equal("B", rule.GetProperty("excelColumn").GetString());
        }

        // Одне правило на (аркуш, таблицю).
        Assert.Equal(
            rules.Count,
            rules.Select(r => (r.GetProperty("sheet").GetString(), r.GetProperty("table").GetString())).Distinct().Count());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Правила_на_колонці_C_не_переносяться_і_лежать_окремо_до_відповіді_замовника()
    {
        using var doc = Model();
        var transferred = doc.RootElement.GetProperty("conditionalFormatsRowStatus").EnumerateArray()
            .Select(r => r.GetProperty("table").GetString()).ToHashSet(StringComparer.Ordinal);
        var unproven = doc.RootElement.GetProperty("conditionalFormatsStatusUnproven").EnumerateArray().ToList();

        Assert.NotEmpty(unproven);
        Assert.All(unproven, u => Assert.NotEqual("B", u.GetProperty("excelColumn").GetString()));
        // Таблиця з непідтвердженою колонкою не отримує правила «за статусом» мовчки.
        Assert.All(unproven, u => Assert.DoesNotContain(u.GetProperty("table").GetString(), transferred));
    }
}
