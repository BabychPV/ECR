// src/Ecr.Adapters.Excel/SnapshotWorkbookWriter.cs
using System.Globalization;
using ClosedXML.Excel;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;

namespace Ecr.Adapters.Excel;

/// <summary>
/// Книга зрізу звітності: один плаский аркуш (<c>R7</c>, <c>D-52a</c>).
/// </summary>
/// <remarks>
/// ⚠ Тим самим пакетом (ClosedXML, MIT) і тим самим прийомом, що
/// <see cref="ExcelExporter"/>: готова книга лягає у ТИМЧАСОВИЙ ФАЙЛ, а не в
/// <c>MemoryStream</c>. Причина там записана дослівно й тут та сама — вміст у
/// пам'яті другою копією поряд з об'єктною моделлю кладе процес рівно тоді,
/// коли вивантажують усі одразу.
/// </remarks>
public sealed class SnapshotWorkbookWriter : ISnapshotWorkbookWriter
{
    /// <summary>Назва аркуша книги.</summary>
    public const string SheetName = "Snapshot";

    /// <summary>
    /// Формат дат у книзі.
    /// </summary>
    /// <remarks>
    /// ⚠ ISO, а не локальний: книгу відкриють у будь-якій локалі, і
    /// <c>03/04/2026</c> означало б там два різні дні. Формат — це ПОКАЗ;
    /// значення комірки лишається датою, тобто числом, за яким Excel сортує й
    /// віднімає.
    /// </remarks>
    public const string DateFormat = "yyyy-mm-dd";

    /// <inheritdoc/>
    public async Task<Stream> WriteAsync(SnapshotWorkbook workbook, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workbook);

        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet(SheetName);

        for (var c = 0; c < workbook.Columns.Count; c++)
        {
            // Заголовок — КОД колонки: назв мовами каталогу опис версії ще не
            // має, і вигаданий підпис розійшовся б із тим, що показує екран.
            sheet.Cell(1, c + 1).Value = workbook.Columns[c].Code;
        }

        sheet.Row(1).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);

        for (var r = 0; r < workbook.Rows.Count; r++)
        {
            ct.ThrowIfCancellationRequested();

            var cells = workbook.Rows[r].Cells;

            for (var c = 0; c < workbook.Columns.Count; c++)
            {
                var column = workbook.Columns[c];
                cells.TryGetValue(column.Code, out var value);

                Write(sheet.Cell(r + 2, c + 1), value, column.Kind);
            }
        }

        // Ширина — по РЯДКУ ЗАГОЛОВКІВ, не по всьому аркушу: той самий урок,
        // що й в `ExcelExporter.AdjustHeaders` (482 мс проти 5 мс).
        if (workbook.Columns.Count > 0)
        {
            sheet.Columns(1, workbook.Columns.Count).AdjustToContents(1, 1);
        }

        var output = TemporaryFile();

        try
        {
            book.SaveAs(output);
            output.Position = 0;
        }
        catch
        {
            await output.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return output;
    }

    /// <summary>
    /// Кладе значення комірки ЙОГО ТИПОМ, а не текстом.
    /// </summary>
    /// <remarks>
    /// ⛔ Це і є предмет усієї задачі. Книга, у якій числа — рядки, виглядає
    /// правильно і не вміє нічого: ні суми, ні сортування за величиною, ні
    /// зведеної таблиці. Той, хто її відкрив, дізнається про це не одразу.
    ///
    /// ⚠ <b>Excel тримає 15 значущих цифр.</b> Число зберігається як
    /// <c>double</c> (це формат книги, а не наш вибір), тож <c>decimal(38,16)</c>
    /// бази в книгу повністю не поміщається: значення з 16+ значущими цифрами
    /// округлиться. Для звірки байт у байт лишається <c>GET …/rows</c>, де
    /// число їде десятковим без утрат.
    /// </remarks>
    private static void Write(IXLCell cell, object? value, string kind)
    {
        if (value is null)
        {
            return;
        }

        switch (kind)
        {
            case ReportSourceColumns.Number when AsNumber(value) is { } number:
                cell.Value = number;
                return;

            case ReportSourceColumns.Date when AsDate(value) is { } date:
                cell.Value = date;
                cell.Style.NumberFormat.Format = DateFormat;
                return;

            default:
                // Усе решта — текст. ⚠ Саме текстом, а не «як вийде»: код
                // `202603` у текстовій колонці Excel інакше сам зробив би
                // числом і з'їв провідні нулі.
                //
                // ⚠ Сюди ж падає значення, яке ОГОЛОШЕНЕ числом чи датою, але
                // ним не читається. Мовчки лишити комірку порожньою означало б
                // утратити дані без сліду; текст видно.
                cell.Value = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                return;
        }
    }

    /// <summary>Число зі значення комірки; <c>null</c> — числом не читається.</summary>
    /// <remarks>
    /// ⚠ <c>decimal</c> — те, що віддає порт (<c>ReportSnapshotBuilder.ValueOf</c>).
    /// Решта арм — на випадок іншої реалізації порту, а не на «про всяк випадок»:
    /// заглушка в тестах кладе <c>int</c>, і мовчазне перетворення її на текст
    /// зробило б тест зеленим на дефекті.
    /// </remarks>
    private static decimal? AsNumber(object value)
        => value switch
        {
            decimal number => number,
            long number => number,
            int number => number,
            double number when double.IsFinite(number) && Math.Abs(number) < 7.9e28 => (decimal)number,
            string text when decimal.TryParse(
                text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };

    /// <summary>
    /// Дата зі значення комірки; <c>null</c> — значення датою не читається.
    /// </summary>
    /// <remarks>
    /// ⚠ Порт віддає дату РЯДКОМ у форматі <c>O</c>
    /// (<c>ReportSnapshotBuilder.ValueOf</c>), бо відповідь <c>rows</c> — JSON.
    /// Тому тут розбір, а не приведення типу.
    /// </remarks>
    private static DateTime? AsDate(object value)
        => value switch
        {
            DateTime date => date,
            DateTimeOffset offset => offset.UtcDateTime,
            string text when DateTime.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) => parsed,
            _ => null,
        };

    /// <summary>Порожній файл, який видаляє сам себе при закритті потоку.</summary>
    private static FileStream TemporaryFile()
        => new(
            Path.Combine(Path.GetTempPath(), $"ecr-snapshot-{Guid.NewGuid():N}.xlsx"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
}
