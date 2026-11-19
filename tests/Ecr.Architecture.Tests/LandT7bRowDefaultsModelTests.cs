// tests/Ecr.Architecture.Tests/LandT7bRowDefaultsModelTests.cs
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// ✎ B175 × B26 (P-0118-34, «поставлений дефолт відповідає наміру»): згенерована модель Land дає рядкам речовин
/// T7b_00 (S1_6, S2_7) дефолт рядка <c>Land_Status = RUNNING</c> — той самий запис довідника
/// <c>LAND_GENERIC_EQUIPSTATUS</c>, що <c>statusPresets.entry</c>, — а рядку об'єму S3_9 жодного (D-425 його не рахує).
/// Так новий документ проходить фільтр активності ECW_C09_02_01 без етапу <c>-Stage T7bStatus</c>.
/// </summary>
/// <remarks>
/// Мутації генератора (<c>gen_ecr_model.py</c>, модель перегенерована <c>run-model-gen.ps1</c>): прибрати T7b_00 з
/// ROW_CELL_DEFAULTS — червоний; дати S3_9 Running (не пропускати emptyRows) — червоний.
/// </remarks>
public sealed class LandT7bRowDefaultsModelTests
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "B175")]
    public void T7b_00_рядки_речовин_мають_дефолт_Running_а_S3_9_жодного()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));
        var root = doc.RootElement;
        var table = root.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray())
            .Single(t => t.GetProperty("code").GetString() == "T7b_00");

        var presets = table.GetProperty("statusPresets");
        var column = presets.GetProperty("column").GetString();
        var entry = presets.GetProperty("entry").GetString();
        Assert.Equal("Land_Status", column);
        Assert.Equal("RUNNING", entry);

        // Код справді є записом довідника колонки (інакше дефолт не розв'язався б і фільтр виключив би рядок).
        var status = table.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("code").GetString() == column);
        Assert.Equal("Lookup", status.GetProperty("dataType").GetString());
        var registry = status.GetProperty("registry").GetString();
        Assert.Contains(
            root.GetProperty("registries").EnumerateArray()
                .Single(r => r.GetProperty("code").GetString() == registry)
                .GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("code").GetString() == entry);

        var rows = table.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("key").GetString()!);
        foreach (var key in new[] { "S1_6", "S2_7" })
        {
            Assert.True(rows[key].TryGetProperty("cellDefaults", out var defaults), $"{key}: немає cellDefaults");
            var only = Assert.Single(defaults.EnumerateObject());
            Assert.Equal(column, only.Name);
            Assert.Equal(entry, only.Value.GetString());
        }

        Assert.False(rows["S3_9"].TryGetProperty("cellDefaults", out _), "S3_9 не має отримати дефолт статусу");

        // Жодна інша таблиця моделі дефолтів рядка поки не несе (розширення — свідоме рішення, не побічний ефект).
        var others = root.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray())
            .Where(t => t.GetProperty("code").GetString() != "T7b_00")
            .SelectMany(t => t.GetProperty("rows").EnumerateArray())
            .Count(r => r.TryGetProperty("cellDefaults", out _));
        Assert.Equal(0, others);
    }
}
