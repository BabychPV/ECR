using System.Text.Json;
using Ecr.Application.Documents;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Прод-зразок HSE 2026-10-12 (прогалина 10): API відхиляло значення з 13–16 знаками після коми
/// («не більше 12 знаків»), бо вхідні колонки моделі Land мали Precision 28 / Scale 12, тоді як сховище комірки —
/// <c>decimal(34,16)</c> (<see cref="CellValueReader.StorageScale"/>). Модель шаблону Land тримає (34,16) на вхідних
/// Decimal і на Formula-колонках, що копіюють вхід; виходи методологій (Calculated) лишаються (28,12).
/// </summary>
public sealed class LandDecimalScaleModelTests
{
    private const int StoragePrecision = 34;

    private static JsonDocument Model()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(
            SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));

    private static IEnumerable<(string Table, JsonElement Column)> Columns(JsonDocument model)
        => model.RootElement.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray())
            .SelectMany(t => t.GetProperty("columns").EnumerateArray()
                .Select(c => (t.GetProperty("code").GetString()!, c)));

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Вхідні_Decimal_колонки_тримають_масштаб_сховища_16_а_не_12()
    {
        using var model = Model();
        var decimals = Columns(model)
            .Where(x => x.Column.GetProperty("dataType").GetString() == "Decimal")
            .ToList();

        Assert.NotEmpty(decimals);

        foreach (var (table, column) in decimals)
        {
            var code = column.GetProperty("code").GetString();

            Assert.True(
                column.TryGetProperty("scale", out var scale) && scale.GetInt32() == CellValueReader.StorageScale,
                $"{table}.{code}: Scale має бути {CellValueReader.StorageScale} (сховище decimal(34,16)).");
            Assert.True(
                column.TryGetProperty("precision", out var precision) && precision.GetInt32() == StoragePrecision,
                $"{table}.{code}: Precision має бути {StoragePrecision}.");
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Жодна_колонка_не_оголошує_масштаб_чи_точність_понад_сховище()
    {
        using var model = Model();
        var integerDigits = CellValueReader.StorageIntegerDigits;

        foreach (var (table, column) in Columns(model))
        {
            if (!column.TryGetProperty("scale", out var scaleElement) || scaleElement.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            var code = column.GetProperty("code").GetString();
            var scale = scaleElement.GetInt32();
            var precision = column.GetProperty("precision").GetInt32();

            Assert.True(scale <= CellValueReader.StorageScale, $"{table}.{code}: Scale {scale} > сховища.");
            Assert.True(precision - scale <= integerDigits, $"{table}.{code}: цілих знаків {precision - scale} > {integerDigits}.");
            Assert.True(scale <= precision, $"{table}.{code}: Scale {scale} > Precision {precision}.");
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Formula_колонки_що_копіюють_вхід_не_округлюють_його_до_12_знаків()
    {
        using var model = Model();

        var bridges = Columns(model)
            .Where(x => x.Column.GetProperty("dataType").GetString() == "Formula"
                && x.Column.TryGetProperty("precision", out var p) && p.ValueKind == JsonValueKind.Number)
            .ToList();

        Assert.NotEmpty(bridges);
        Assert.All(bridges, x => Assert.Equal(CellValueReader.StorageScale, x.Column.GetProperty("scale").GetInt32()));
    }
}
