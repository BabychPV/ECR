// tests/Ecr.Adapters.Tests/Excel/RegistryWorkbookWriterTests.cs
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// Книга експорту довідника (RT-16): число лягає числом лише тоді, коли <c>double</c> тримає його без
/// втрат; інакше — текстом рядка сервера (D-30).
/// </summary>
/// <remarks>
/// Мутаційний доказ: <c>IsExactInDouble</c> → <c>true</c> завжди → <see cref="Точне_число_текстом_коротке_числом"/>
/// червоний (26 цифр стають числом і обрізаються до 15).
/// </remarks>
public sealed class RegistryWorkbookWriterTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Точне_число_текстом_коротке_числом()
    {
        var workbook = new RegistryWorkbook(
            "Sheet/with:bad*chars[and-a-very-long-name]",
            [new("code", CellDataType.String), new("T", CellDataType.Decimal), new("D", CellDataType.Date), new("ON", CellDataType.Bool)],
            [
                ["A", "1234567890.1234567890123456", "2026-01-01", "true"],
                ["00123", "12.5", "not-a-date", null],
            ]);

        await using var stream = await new RegistryWorkbookWriter().WriteAsync(workbook, default);
        using var book = new XLWorkbook(stream);
        var sheet = book.Worksheet(1);

        Assert.Equal("Sheet_with_bad_chars_and-a-very", sheet.Name);
        Assert.Equal("T", sheet.Cell(1, 2).GetString());

        Assert.Equal(XLDataType.Text, sheet.Cell(2, 2).DataType);
        Assert.Equal("1234567890.1234567890123456", sheet.Cell(2, 2).GetString());
        Assert.Equal(XLDataType.Number, sheet.Cell(3, 2).DataType);
        Assert.Equal(12.5, sheet.Cell(3, 2).GetDouble());

        Assert.Equal(XLDataType.DateTime, sheet.Cell(2, 3).DataType);
        Assert.Equal(XLDataType.Boolean, sheet.Cell(2, 4).DataType);

        // Рядок лягає текстом як є: провідні нулі й нерозбірна дата не губляться.
        Assert.Equal(XLDataType.Text, sheet.Cell(3, 1).DataType);
        Assert.Equal("00123", sheet.Cell(3, 1).GetString());
        Assert.Equal("not-a-date", sheet.Cell(3, 3).GetString());
        Assert.True(sheet.Cell(3, 4).IsEmpty());
    }

    /// <summary>
    /// Значення, що починається з <c>= + - @</c> (і табуляції), не стає формулою: у книзі це ТЕКСТ
    /// (ClosedXML ≥ 0.100 не інтерпретує рядок), а в XML аркуша немає елемента <c>&lt;f&gt;</c>.
    /// Окремо від CSV (<c>CsvFormat.Field</c>): там екранування потрібне, бо Excel, відкриваючи CSV,
    /// сам розбирає текст; у XLSX тип комірки заданий явно. Мутаційний доказ: запис значення на
    /// <c>=</c> через <c>FormulaA1</c> робить тест червоним.
    /// </summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.16")]
    [InlineData("=1+1")]
    [InlineData("=HYPERLINK(\"http://evil\",\"x\")")]
    [InlineData("+cmd|' /C calc'!A0")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1:A2)")]
    [InlineData("\t=1+1")]
    public async Task Значення_що_схоже_на_формулу_лягає_текстом_а_не_формулою(string value)
    {
        var workbook = new RegistryWorkbook(
            "F",
            [new("code", CellDataType.String), new("V", CellDataType.String)],
            [["A", value]]);

        await using var stream = await new RegistryWorkbookWriter().WriteAsync(workbook, default);
        var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        bytes.Position = 0;

        using (var zip = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true))
        {
            using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            Assert.DoesNotContain("<f>", await reader.ReadToEndAsync(), StringComparison.Ordinal);
        }

        bytes.Position = 0;
        using var book = new XLWorkbook(bytes);
        var cell = book.Worksheet(1).Cell(2, 2);
        Assert.False(cell.HasFormula);
        Assert.Equal(XLDataType.Text, cell.DataType);
        Assert.Equal(value, cell.GetString());
    }
}
