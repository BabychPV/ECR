using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Sources;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Sources;

/// <summary>
/// Число від інтеграції — до масштабу комірки (Z2-01): обчислене значення межі не має відхиляти батч таблиці.
/// </summary>
public sealed class IntegrationValueScaleTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Z2-01")]
    public void Середнє_з_нескінченним_дробом_до_масштабу_сховища_і_проходить_розбір()
    {
        var column = Column(CellDataType.Decimal, scale: null);
        var avg = 31m / 3m;

        // Без округлення розбір відмовляє: це і є відмова, що валила батч матеріалізації.
        var refusal = Assert.Throws<BusinessRuleException>(() => CellValueReader.Read(avg, column));
        Assert.Equal("err.ECR-CELL-0422.tooManyDecimals", refusal.Details!["messageKey"]);

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: повернути значення без `decimal.Round` → 10.333333333333333333333333333, червоний.
        var rounded = IntegrationValueScale.ToColumn(avg, column);

        Assert.Equal(10.3333333333333333m, rounded);
        var stored = CellValueReader.Read(rounded, column);
        Assert.NotNull(stored);
        Assert.Null(column.ValidateValue(stored!));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Z2-01")]
    [InlineData(null, "13249.6265996705107118")]
    [InlineData((byte)3, "13249.627")]
    [InlineData((byte)20, "13249.6265996705107118")]
    public void Конверсія_Nm3_в_Sm3_до_масштабу_колонки_і_проходить_перевірку_Scale(byte? scale, string expected)
    {
        // `Avg` 12345.678 Nm3 → Sm3 (множник 1.073219842577338459): 21 знак після коми.
        var column = Column(CellDataType.Decimal, scale);
        var converted = 12345.678m * 1.073219842577338459m;

        var rounded = IntegrationValueScale.Round(converted, column);

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), rounded);
        Assert.Null(column.ValidateValue(CellValueReader.Read(rounded, column)!));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Z2-01")]
    public void Середина_округлюється_від_нуля_як_у_сховищі()
    {
        var column = Column(CellDataType.Decimal, scale: 3);

        Assert.Equal(2.001m, IntegrationValueScale.Round(2.0005m, column));
        Assert.Equal(-2.001m, IntegrationValueScale.Round(-2.0005m, column));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Z2-01")]
    public void Ціла_колонка_до_цілого_не_округлюється_а_нечислові_значення_як_є()
    {
        // Дробове число в колонці Int — розбіжність конфігурації: відмова видима, мовчазного ±0.5 немає.
        var integer = Column(CellDataType.Int, scale: null);
        Assert.Equal(2.5m, IntegrationValueScale.ToColumn(2.5m, integer));

        var text = Column(CellDataType.String, scale: null);
        Assert.Equal("10.123456789012345678", IntegrationValueScale.ToColumn("10.123456789012345678", text));
        Assert.Equal(7L, IntegrationValueScale.ToColumn(7L, Column(CellDataType.Lookup, scale: null)));
    }

    private static ColumnDef Column(CellDataType type, byte? scale)
    {
        var column = new ColumnDef(
            1, EcrCode.Create("C1"), new LocalizedText(new Dictionary<string, string> { ["en"] = "C1" }), 1, type);
        if (scale is not null)
        {
            column.SetNumericFormat(precision: null, scale);
        }

        return column;
    }
}
