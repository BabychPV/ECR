using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// AN-70, правило людини 02.11 для експорту: <c>Scale</c> ≤ 6 — формат комірки (доповнення нулями до scale);
/// більший <c>Scale</c> (колонки Land — 16, виходи методологій — 12) — точність сховища, тож книга лишає формат
/// «Загальний» (найкоротший запис), а не «0.0000000000000000» (Excel показав би «3.0000000000000000»).
/// </summary>
public sealed class StyleMapperNumberFormatTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(2, "0.00")]
    [InlineData(6, "0.000000")]
    public void Scale_up_to_six_pads_with_zeros(byte scale, string expected)
    {
        using var workbook = new XLWorkbook();
        var cell = workbook.AddWorksheet("S").Cell(1, 1);

        new StyleMapper().ApplyNumberFormat(cell.Style, null, scale);

        Assert.Equal(expected, cell.Style.NumberFormat.Format);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(18)]
    public void Storage_scale_keeps_general_format_and_does_not_pad(byte scale)
    {
        using var workbook = new XLWorkbook();
        var cell = workbook.AddWorksheet("S").Cell(1, 1);
        cell.Value = 3m;

        new StyleMapper().ApplyNumberFormat(cell.Style, null, scale);

        Assert.Equal(string.Empty, cell.Style.NumberFormat.Format);
        Assert.Equal(0, cell.Style.NumberFormat.NumberFormatId);
        Assert.Equal("3", cell.GetFormattedString());
    }

    [Fact]
    public void Storage_scale_keeps_the_style_format_already_set()
    {
        using var workbook = new XLWorkbook();
        var cell = workbook.AddWorksheet("S").Cell(1, 1);
        cell.Style.NumberFormat.Format = "#,##0.0";

        new StyleMapper().ApplyNumberFormat(cell.Style, null, 16);

        Assert.Equal("#,##0.0", cell.Style.NumberFormat.Format);
    }

    [Fact]
    public void Display_format_wins_over_any_scale()
    {
        using var workbook = new XLWorkbook();
        var cell = workbook.AddWorksheet("S").Cell(1, 1);

        new StyleMapper().ApplyNumberFormat(cell.Style, "0.000", 16);

        Assert.Equal("0.000", cell.Style.NumberFormat.Format);
    }
}
