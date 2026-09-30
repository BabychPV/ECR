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
}
