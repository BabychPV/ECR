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

        var line = 2;
        var written = 0;

        // ⛔ R8: групи й підсумки МАЛЮЮТЬСЯ тут, але не рахуються — вони
        // приходять готовими з тієї самої сторінки, яку бачить екран.
        foreach (var group in workbook.Groups ?? [])
        {
            if (workbook.ShowGroupHeader)
            {
                sheet.Cell(line, 1).Value = $"{group.Column}: {Text(group.Value)}";
                sheet.Row(line).Style.Font.Bold = true;
                line++;
            }

            for (var i = 0; i < group.RowCount && written < workbook.Rows.Count; i++, written++)
            {
                Row(sheet, line++, workbook.Rows[written], workbook.Columns, ct);
            }

            line = Totals(sheet, line, group.Totals, workbook.Columns);
        }

        // Рядки поза групами: макет без `groupBy` — і зріз без макета взагалі.
        for (; written < workbook.Rows.Count; written++)
        {
            Row(sheet, line++, workbook.Rows[written], workbook.Columns, ct);
        }

        _ = Totals(sheet, line, workbook.Totals, workbook.Columns);

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

    /// <summary>Підпис рядка підсумків у першій колонці.</summary>
    /// <remarks>
    /// ⚠ Без каталогу — як і заголовки колонок, які є КОДАМИ: у книзі немає ні
    /// мови користувача, ні місця, де її спитати. Підпис потрібен, щоб рядок
    /// чисел під таблицею не прочитався як ще один рядок звіту.
    /// </remarks>
    public const string TotalsLabel = "Total";

    /// <summary>Малює рядок підсумків; повертає наступний вільний рядок аркуша.</summary>
    /// <remarks>
    /// ⚠ Значення лягає в КОЛОНКУ свого підсумку: підсумок під чужою колонкою
    /// читався б як значення тієї колонки.
    /// </remarks>
    private static int Totals(
        IXLWorksheet sheet, int line, IReadOnlyList<SnapshotTotal>? totals, IReadOnlyList<SnapshotColumn> columns)
    {
        if (totals is not { Count: > 0 })
        {
            return line;
        }

        var labelled = false;

        foreach (var total in totals)
        {
            var index = IndexOf(columns, total.Column);

            if (index < 0)
            {
                continue;
            }

            // ⚠ Тип комірки — тип КОЛОНКИ, окрім `count`: кількість рядків — це
            // число, хоч би що стояло в самій колонці.
            Write(
                sheet.Cell(line, index + 1),
                total.Value,
                total.Fn == ReportLayout.Count ? ReportSourceColumns.Number : columns[index].Kind);

            labelled |= index == 0;
        }

        if (!labelled)
        {
            sheet.Cell(line, 1).Value = TotalsLabel;
        }

        sheet.Row(line).Style.Font.Bold = true;

        return line + 1;
    }

    private static int IndexOf(IReadOnlyList<SnapshotColumn> columns, string code)
    {
        for (var c = 0; c < columns.Count; c++)
        {
            if (string.Equals(columns[c].Code, code, StringComparison.Ordinal))
            {
                return c;
            }
        }

        return -1;
    }

    /// <summary>Малює один рядок зрізу.</summary>
    private static void Row(
        IXLWorksheet sheet, int line, SnapshotRow row, IReadOnlyList<SnapshotColumn> columns, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        for (var c = 0; c < columns.Count; c++)
        {
            row.Cells.TryGetValue(columns[c].Code, out var value);

            Write(sheet.Cell(line, c + 1), value, columns[c].Kind);
        }
    }

    /// <summary>Значення заголовка групи текстом; порожнє — порожньо.</summary>
    private static string Text(object? value)
        => value is null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

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
