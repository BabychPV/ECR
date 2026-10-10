using ClosedXML.Excel;

namespace Ecr.Adapters.Excel;

/// <summary>
/// Відбитки підписів рядків і заголовків колонок блоку на момент експорту (Y5-01):
/// чи стоять рядки й колонки книги там, де їх поставив експорт.
/// </summary>
/// <remarks>
/// ⛔ Навіщо. Карта книги (<see cref="ExcelWorkbookMap"/>) тримає ПОЗИЦІЇ рядків і
/// колонок, а Excel про неї не знає й нічого в ній не оновлює. Сортування таблиці,
/// видалення чи вставка рядка або колонки зсувають значення, а імпорт читав би їх за
/// старими позиціями — і число рядка R7 тихо лягало б у R1. Зміни в перегляді
/// виглядали правдоподібно (ті самі числа, лише в інших рядках), тож людина їх
/// погоджувала. Тепер експорт кладе в карту відбиток підпису кожного рядка (стовпець
/// праворуч від колонок даних) і заголовка кожної колонки, а імпорт звіряє їх із
/// книгою: розбіжність — відмова всієї таблиці (<see cref="ImportMessageKeys.LayoutChanged"/>).
///
/// ⚠ Межі, названі чесно. (1) Рядки з ОДНАКОВИМ підписом, переставлені між собою, не
/// ловляться: їх нічим відрізнити. Підпис без опису рядка — ключ рядка, він
/// унікальний. (2) Сортування лише стовпців даних без стовпця підписів (Excel
/// за замовчуванням пропонує розширити виділення) теж не ловиться. (3) Книга без
/// відбитків (вивантажена до цієї правки) звіряється як раніше — ніяк.
///
/// ⚠ Правило відбитка — <see cref="CalculatedCellFingerprint.Of"/> (16 біт на комірку):
/// колізія на одному рядку можлива (1/65 536), але перестановка зачіпає щонайменше
/// два рядки, і пропуск усіх розбіжностей таблиці практично неможливий.
/// </remarks>
public static class LayoutFingerprint
{
    /// <summary>Стовпець підписів рядків блоку: перший праворуч від колонок даних (T4-04).</summary>
    /// <param name="block">Блок карти.</param>
    public static int LabelColumn(ExcelTableBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        return block.Columns.Count == 0 ? 1 : block.Columns.Max(c => c.Number) + 1;
    }

    /// <summary>Блок із відбитками підписів рядків і заголовків колонок, узятими з аркуша.</summary>
    /// <param name="worksheet">Аркуш книги, вже заповнений експортом.</param>
    /// <param name="block">Блок карти.</param>
    public static ExcelTableBlock Stamp(IXLWorksheet worksheet, ExcelTableBlock block)
    {
        ArgumentNullException.ThrowIfNull(worksheet);
        ArgumentNullException.ThrowIfNull(block);

        var labelColumn = LabelColumn(block);

        return block with
        {
            Columns = [.. block.Columns.Select(column => column with
            {
                Header = CalculatedCellFingerprint.Of(worksheet.Cell(block.HeaderRow, column.Number)),
            })],
            Rows = [.. block.Rows.Select(row => row with
            {
                Label = CalculatedCellFingerprint.Of(worksheet.Cell(row.Number, labelColumn)),
            })],
        };
    }

    /// <summary>
    /// Перша комірка книги, де підпис рядка чи заголовок колонки не збігся з відбитком
    /// експорту; <c>null</c> — розкладка та сама (або відбитків немає).
    /// </summary>
    /// <param name="worksheet">Аркуш книги.</param>
    /// <param name="block">Блок карти.</param>
    public static (string? RowKey, string? ColumnCode, string Address)? FirstMismatch(
        IXLWorksheet worksheet, ExcelTableBlock block)
    {
        ArgumentNullException.ThrowIfNull(worksheet);
        ArgumentNullException.ThrowIfNull(block);

        foreach (var column in block.Columns)
        {
            if (column.Header is { } header)
            {
                var cell = worksheet.Cell(block.HeaderRow, column.Number);

                if (!string.Equals(header, CalculatedCellFingerprint.Of(cell), StringComparison.Ordinal))
                {
                    return (null, column.Code, cell.Address.ToString() ?? string.Empty);
                }
            }
        }

        var labelColumn = LabelColumn(block);

        foreach (var row in block.Rows)
        {
            if (row.Label is { } label)
            {
                var cell = worksheet.Cell(row.Number, labelColumn);

                if (!string.Equals(label, CalculatedCellFingerprint.Of(cell), StringComparison.Ordinal))
                {
                    return (row.RowKey, null, cell.Address.ToString() ?? string.Empty);
                }
            }
        }

        return null;
    }
}
