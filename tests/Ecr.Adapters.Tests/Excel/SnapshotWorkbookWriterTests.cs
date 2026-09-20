// tests/Ecr.Adapters.Tests/Excel/SnapshotWorkbookWriterTests.cs
using System.Globalization;
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// R7: книга зрізу РОЗБИРАЄТЬСЯ НАЗАД тим самим пакетом, яким її склали.
/// </summary>
/// <remarks>
/// ⛔ Саме розбирається, а не «файл не порожній». Перевірка «байтів більше
/// нуля» зелена на книзі з порожнім аркушем, з переплутаними колонками і з
/// числами, записаними текстом, — тобто на кожному дефекті, заради якого цей
/// експорт і писали. Той самий прийом, що й крок 22 <c>tools/smoke.ps1</c>:
/// відкрити результат і звірити комірки зі вхідними даними.
/// </remarks>
public sealed class SnapshotWorkbookWriterTests
{
    private static readonly SnapshotColumn[] Columns =
    [
        // ⚠ Порядок НЕ алфавітний навмисно: перевіряється опис, а не сортування.
        new("OutputCode", ReportSourceColumns.Text),
        new("Value", ReportSourceColumns.Number),
        new("RowKey", ReportSourceColumns.Text),
    ];

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Книга_розбирається_назад_і_комірки_збігаються_з_рядками_зрізу()
    {
        var rows = new[]
        {
            Row(1, "CO2", 12.5m, "boiler-1"),
            Row(2, "NOx", -0.25m, "boiler-2"),
        };

        using var sheet = await SheetOf(new SnapshotWorkbook(7, Columns, rows)).ConfigureAwait(true);

        // Заголовки — коди колонок у порядку опису.
        Assert.Equal("OutputCode", sheet.Cell(1, 1).GetString());
        Assert.Equal("Value", sheet.Cell(1, 2).GetString());
        Assert.Equal("RowKey", sheet.Cell(1, 3).GetString());

        Assert.Equal("CO2", sheet.Cell(2, 1).GetString());
        Assert.Equal(12.5, sheet.Cell(2, 2).GetDouble());
        Assert.Equal("boiler-1", sheet.Cell(2, 3).GetString());

        Assert.Equal("NOx", sheet.Cell(3, 1).GetString());
        Assert.Equal(-0.25, sheet.Cell(3, 2).GetDouble());
        Assert.Equal("boiler-2", sheet.Cell(3, 3).GetString());

        // Рядків рівно стільки, скільки в зрізі, плюс заголовок: зайвий
        // порожній рядок прочитався б споживачем як рядок звіту.
        Assert.Equal(3, sheet.LastRowUsed()!.RowNumber());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Число_лягає_ЧИСЛОМ_а_не_текстом()
    {
        // ⛔ Мутація, якою перевірено цей тест: у `SnapshotWorkbookWriter.Write`
        // прибрати арм `Number` (писати все текстом). Падають рівно три тести
        // цього файлу — усі, що читають числову комірку, — і жоден інший у
        // репозиторії.
        //
        // ⚠ Книга, у якій числа є рядками, виглядає правильно і не вміє нічого:
        // ні суми, ні сортування за величиною, ні зведеної таблиці.
        using var sheet = await SheetOf(
            new SnapshotWorkbook(7, Columns, [Row(1, "CO2", 12.5m, "boiler-1")])).ConfigureAwait(true);

        Assert.Equal(XLDataType.Number, sheet.Cell(2, 2).DataType);
        Assert.Equal(XLDataType.Text, sheet.Cell(2, 1).DataType);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Дата_лягає_датою_з_форматом_ISO()
    {
        // ⚠ Порт віддає дату РЯДКОМ у форматі `O` (відповідь `…/rows` — JSON),
        // тож книга мусить її розібрати, інакше дата приїде текстом.
        SnapshotColumn[] columns = [new("StartedAt", ReportSourceColumns.Date)];

        var row = new SnapshotRow(
            1,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["StartedAt"] = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc)
                    .ToString("O", CultureInfo.InvariantCulture),
            });

        using var sheet = await SheetOf(new SnapshotWorkbook(7, columns, [row])).ConfigureAwait(true);

        Assert.Equal(XLDataType.DateTime, sheet.Cell(2, 1).DataType);
        Assert.Equal(new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Unspecified), sheet.Cell(2, 1).GetDateTime());
        Assert.Equal(SnapshotWorkbookWriter.DateFormat, sheet.Cell(2, 1).Style.NumberFormat.Format);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Число_з_двадцятьма_значущими_цифрами_втрачає_точність_у_книзі()
    {
        // ⛔ Це НЕ дефект експорту й не те, що можна полагодити тут: книга
        // зберігає число як `double`, тобто ~15 значущих цифр. Рішення «усюди
        // 16 знаків» стосується ЗБЕРІГАННЯ (`decimal(38,16)`); Excel його не
        // витримує. Тест стоїть, щоб межа була названа й перевірена, а не
        // знайдена колись звіркою звіту з базою.
        const decimal exact = 1234567890.1234567890m;

        SnapshotColumn[] columns = [new("Value", ReportSourceColumns.Number)];
        var row = new SnapshotRow(
            1, new Dictionary<string, object?>(StringComparer.Ordinal) { ["Value"] = exact });

        using var sheet = await SheetOf(new SnapshotWorkbook(7, columns, [row])).ConfigureAwait(true);

        var stored = (decimal)sheet.Cell(2, 1).GetDouble();

        Assert.NotEqual(exact, stored);
        Assert.Equal(Math.Round(exact, 5), Math.Round(stored, 5));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Порожня_комірка_лишається_порожньою_а_не_нулем()
    {
        // ⚠ Нуль у звіті — ЗНАЧЕННЯ («виміряли й вийшло нуль»), порожнє —
        // «не вимірювали». Підміна одного одним міняє зміст звіту.
        var row = new SnapshotRow(
            1,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["OutputCode"] = "CO2",
                ["Value"] = null,
                ["RowKey"] = null,
            });

        using var sheet = await SheetOf(new SnapshotWorkbook(7, Columns, [row])).ConfigureAwait(true);

        Assert.Equal(XLDataType.Blank, sheet.Cell(2, 2).DataType);
        Assert.True(sheet.Cell(2, 3).IsEmpty());
    }

    private static SnapshotRow Row(int rowNo, string outputCode, decimal value, string rowKey)
        => new(
            rowNo,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["OutputCode"] = outputCode,
                ["Value"] = value,
                ["RowKey"] = rowKey,
            });

    /// <summary>Складає книгу і ВІДКРИВАЄ її назад тим самим пакетом.</summary>
    /// <remarks>
    /// ⚠ Книга лишається відкритою разом із поверненим аркушем — читач
    /// звільняє її сам. Потік закривається тут: <c>XLWorkbook</c> читає його
    /// цілком у конструкторі, а файл під ним видаляє себе при закритті.
    /// </remarks>
    private static async Task<SheetHandle> SheetOf(SnapshotWorkbook workbook)
    {
        await using var content = await new SnapshotWorkbookWriter()
            .WriteAsync(workbook, CancellationToken.None)
            .ConfigureAwait(true);

        var book = new XLWorkbook(content);

        return new SheetHandle(book, book.Worksheet(SnapshotWorkbookWriter.SheetName));
    }

    /// <summary>Аркуш разом із книгою, яка ним володіє.</summary>
    private sealed record SheetHandle(XLWorkbook Book, IXLWorksheet Sheet) : IDisposable
    {
        public IXLCell Cell(int row, int column) => Sheet.Cell(row, column);

        public IXLRow? LastRowUsed() => Sheet.LastRowUsed();

        public void Dispose() => Book.Dispose();
    }
}
