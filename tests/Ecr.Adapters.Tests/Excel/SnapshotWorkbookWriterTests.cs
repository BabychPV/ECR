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
        // ⚠ Підпис дорівнює коду: опис без назв (`R9`) приходить саме таким, і
        // решта перевірок цього файлу лишається про те, про що була.
        new("OutputCode", ReportSourceColumns.Text, "OutputCode"),
        new("Value", ReportSourceColumns.Number, "Value"),
        new("RowKey", ReportSourceColumns.Text, "RowKey"),
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
    public async Task Заголовок_книги_це_НАЗВА_колонки_а_не_її_код()
    {
        // ⛔ Мутація, якою перевірено цей тест: у `SnapshotWorkbookWriter.WriteAsync`
        // повернути заголовок на `workbook.Columns[c].Code`. Падає РІВНО цей
        // тест — решта файлу описує колонки без назв, де підпис дорівнює коду.
        //
        // ⚠ Заради цього R9 і робився: регулятор читає книгу, а не наші коди
        // полів, і `OutputCode` у шапці держформи для нього не означає нічого.
        SnapshotColumn[] columns =
        [
            new("OutputCode", ReportSourceColumns.Text, "Загрязняющее вещество"),
            new("Value", ReportSourceColumns.Number, "Объём, т"),
        ];

        var row = new SnapshotRow(
            1,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["OutputCode"] = "CO2",
                ["Value"] = 12.5m,
            });

        using var sheet = await SheetOf(new SnapshotWorkbook(7, columns, [row])).ConfigureAwait(true);

        Assert.Equal("Загрязняющее вещество", sheet.Cell(1, 1).GetString());
        Assert.Equal("Объём, т", sheet.Cell(1, 2).GetString());

        // Дані під підписаною шапкою лишаються тими самими й на своїх місцях.
        Assert.Equal("CO2", sheet.Cell(2, 1).GetString());
        Assert.Equal(12.5, sheet.Cell(2, 2).GetDouble());
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
        SnapshotColumn[] columns = [new("StartedAt", ReportSourceColumns.Date, "StartedAt")];

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

        SnapshotColumn[] columns = [new("Value", ReportSourceColumns.Number, "Value")];
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

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Книга_містить_заголовки_груп_і_рядки_підсумків_макета()
    {
        // ⛔ Мутація, якою перевірено цей тест: у `SnapshotWorkbookWriter.WriteAsync`
        // прибрати обидва виклики `Totals(...)` (підсумки лишаються порахованими
        // для `rows`, але в книгу не потрапляють). Падає РІВНО цей тест — і
        // жоден інший у репозиторії.
        //
        // ⚠ Книга й екран мусять показувати ОДНЕ І ТЕ САМЕ: підсумок, який є в
        // відповіді `…/rows` і якого немає у вивантаженому файлі, робить із
        // «звірити у файлі» дію, що нічого не доводить.
        SnapshotRow[] rows = [Row(2, "NOx", 4m, "kg-1"), Row(1, "CO2", 12.5m, "t-1"), Row(3, "SO2", 0.5m, "t-2")];

        SnapshotRowGroup[] groups =
        [
            new("OutputCode", "NOx", 1, [new("Value", ReportLayout.Sum, 4m)]),
            new("OutputCode", null, 2, [new("Value", ReportLayout.Sum, 13m)]),
        ];

        using var sheet = await SheetOf(
            new SnapshotWorkbook(
                7, Columns, rows, groups, [new("Value", ReportLayout.Sum, 17m)], ShowGroupHeader: true))
            .ConfigureAwait(true);

        // Заголовок групи → її рядок → підсумок групи; так само для другої.
        Assert.Equal("OutputCode: NOx", sheet.Cell(2, 1).GetString());
        Assert.Equal("NOx", sheet.Cell(3, 1).GetString());
        Assert.Equal(4.0, sheet.Cell(4, 2).GetDouble());

        // Порожнє значення групи — заголовок без нього, а не «—» і не пропуск рядка.
        Assert.Equal("OutputCode: ", sheet.Cell(5, 1).GetString());
        Assert.Equal("CO2", sheet.Cell(6, 1).GetString());
        Assert.Equal("SO2", sheet.Cell(7, 1).GetString());
        Assert.Equal(13.0, sheet.Cell(8, 2).GetDouble());

        // Підсумок усього зрізу — останнім рядком, підписаним у першій колонці.
        Assert.Equal(SnapshotWorkbookWriter.TotalsLabel, sheet.Cell(9, 1).GetString());
        Assert.Equal(XLDataType.Number, sheet.Cell(9, 2).DataType);
        Assert.Equal(17.0, sheet.Cell(9, 2).GetDouble());
        Assert.Equal(9, sheet.LastRowUsed()!.RowNumber());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Макет_без_груп_дає_книгу_з_одним_рядком_підсумків()
    {
        using var sheet = await SheetOf(
            new SnapshotWorkbook(
                7,
                Columns,
                [Row(1, "CO2", 12.5m, "t-1")],
                Groups: null,
                Totals: [new("Value", ReportLayout.Count, 1m)]))
            .ConfigureAwait(true);

        Assert.Equal("CO2", sheet.Cell(2, 1).GetString());
        Assert.Equal(SnapshotWorkbookWriter.TotalsLabel, sheet.Cell(3, 1).GetString());

        // ⚠ `count` — число, хоч би якого типу була сама колонка.
        Assert.Equal(XLDataType.Number, sheet.Cell(3, 2).DataType);
        Assert.Equal(3, sheet.LastRowUsed()!.RowNumber());
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
