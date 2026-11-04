// tests/Ecr.Adapters.Tests/Excel/ExcelExportLeadingApostropheTests.cs
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// PM-PASS-A-P3 п.5: експорт xlsx не з'їдає початковий апостроф текстового значення (ClosedXML трактує
/// його як «префікс тексту» Excel і прибирає з рядка) - реімпорт читає той самий рядок, а не «зміну».
/// </summary>
/// <remarks>
/// ⛔ Мутація: у <c>ExcelExporter.WriteText</c> повернути <c>cell.Value = text</c> - тест червоний.
/// </remarks>
public sealed class ExcelExportLeadingApostropheTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData("'; DROP TABLE x;--")]
    [InlineData("'abc")]
    [InlineData("'")]
    [InlineData("''два")]
    [InlineData("звичайний")]
    [InlineData("a'b")]
    public void Рядок_з_початковим_апострофом_проходить_експорт_і_читання_без_змін(string text)
    {
        using var stream = new MemoryStream();
        using (var book = new XLWorkbook())
        {
            var cell = book.AddWorksheet("S").Cell(1, 1);
            ExcelExporter.WriteText(cell, text);
            book.SaveAs(stream);
        }

        stream.Position = 0;
        using var reread = new XLWorkbook(stream);

        Assert.Equal(text, reread.Worksheet("S").Cell(1, 1).GetString());
    }
}
