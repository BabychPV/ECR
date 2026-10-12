using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// <see cref="CellStyle"/>: семантика «не чіпати / зняти / задати» і нормалізація кольору
/// (CELL-STYLE L3b).
/// </summary>
public sealed class CellStyleTests
{
    private static readonly DateTime T0 = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = T0.AddMinutes(5);

    private static CellStyle New()
        => new(new CellAddress(new PeriodKey(202610), 10, 20), userId: 1, T0);

    [Fact]
    public void Нове_оформлення_порожнє()
    {
        var style = New();

        Assert.True(style.IsBlank);
        Assert.Equal(202610, style.PeriodKeyValue);
        Assert.Equal(10, style.TableRowId);
        Assert.Equal(20, style.ColumnDefId);
    }

    [Fact]
    public void Задає_колір_у_верхньому_регістрі_і_лише_змінені_поля()
    {
        var style = New();

        Assert.True(style.Apply("#ffcc99", null, true, userId: 7, T1));

        Assert.Equal("#FFCC99", style.BackgroundHex);
        Assert.Null(style.ForegroundHex);
        Assert.True(style.IsBold);
        Assert.Equal(7, style.UpdatedByUserId);
        Assert.Equal(T1, style.UpdatedAt);
        Assert.False(style.IsBlank);
    }

    [Fact]
    public void Null_не_чіпає_а_порожній_рядок_знімає_поле()
    {
        var style = New();
        style.Apply("#FFFF00", "#FF0000", true, 1, T0);

        Assert.True(style.Apply(null, "", null, 2, T1));

        Assert.Equal("#FFFF00", style.BackgroundHex);
        Assert.Null(style.ForegroundHex);
        Assert.True(style.IsBold);
    }

    [Fact]
    public void Те_саме_значення_не_змінює_автора_й_час()
    {
        var style = New();
        style.Apply("#FFFF00", null, null, 1, T0);

        Assert.False(style.Apply("#ffff00", null, null, 9, T1));

        Assert.Equal(1, style.UpdatedByUserId);
        Assert.Equal(T0, style.UpdatedAt);
    }

    [Fact]
    public void Зняття_усіх_полів_робить_рядок_порожнім()
    {
        var style = New();
        style.Apply("#FFFF00", "#FF0000", true, 1, T0);

        style.Apply("", "", false, 1, T1);

        Assert.True(style.IsBlank);
    }

    [Theory]
    [InlineData("FFFF00")]
    [InlineData("#FFF")]
    [InlineData("#GGGGGG")]
    [InlineData("#FFFF0080")]
    [InlineData("red; background:url(x)")]
    public void Некоректний_колір_відхиляється(string value)
    {
        var style = New();

        Assert.Throws<ArgumentException>(() => style.Apply(value, null, null, 1, T1));
        Assert.True(style.IsBlank);
    }
}
