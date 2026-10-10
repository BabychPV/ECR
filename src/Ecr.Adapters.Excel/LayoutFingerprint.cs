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
/// ⛔ V7-01: підписи на місці ще не означають, що на місці значення. Сортування лише
/// стовпців даних без стовпця підписів (Excel це дозволяє, якщо не розширити виділення)
/// і перестановка рядків з ОДНАКОВИМ підписом лишають підписи як були. Тому після
/// підписів звіряються відбитки введених комірок (<see cref="ExcelRowRef.Cells"/>):
/// якщо серед змінених рядків є цикл «рядок A тепер має те, що на експорті мав B,
/// B — те, що мав C, …, — те, що мав A», значення переставлено, а не виправлено —
/// відмова таблиці. Цикл, а не рівність усього набору, — щоб одночасна правка
/// іншого рядка не ховала перестановку. Копія рядка в інший (джерело не змінилося)
/// циклу не дає — хибної відмови немає. Свідомий обмін значеннями двох рядків теж
/// відмовляється: безпечний бік, такий обмін можна внести двома імпортами чи в сітці.
///
/// ⚠ Межі, названі чесно. (1) Перестановка, у якій КОЖЕН переставлений рядок ще й
/// виправлено, циклу не дає і не ловиться. (2) Книга без відбитків (вивантажена до
/// Y5-01 / AN-118) звіряється як раніше — ніяк. (3) Рядки, що на експорті мали
/// однакові значення, між собою не розрізняються — але й перестановка їх нічого не
/// зсуває.
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

        return PermutedRow(worksheet, block);
    }

    /// <summary>
    /// V7-01: перший рядок циклу перестановки значень серед рядків, чиї введені комірки
    /// змінено після експорту; <c>null</c> — циклу немає (або відбитків комірок немає).
    /// </summary>
    private static (string? RowKey, string? ColumnCode, string Address)? PermutedRow(
        IXLWorksheet worksheet, ExcelTableBlock block)
    {
        var firstEntered = block.Columns.FirstOrDefault(c => !c.IsCalculated);

        if (firstEntered is null)
        {
            return null;
        }

        var changed = new List<(ExcelRowRef Row, string Stored, string Now)>();

        foreach (var row in block.Rows)
        {
            if (row.Cells is { } stored
                && EnteredCellFingerprint.OfRow(worksheet, block.Columns, row.Number) is { } now
                && !string.Equals(stored, now, StringComparison.Ordinal))
            {
                changed.Add((row, stored, now));
            }
        }

        if (changed.Count < 2)
        {
            return null;
        }

        // Ребро i → j: рядок i тепер має те, що на експорті мав рядок j (j ≠ i, бо в i
        // книга ≠ експорт). Цикл у цьому графі — значення переставлено.
        var byStored = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        for (var i = 0; i < changed.Count; i++)
        {
            if (!byStored.TryGetValue(changed[i].Stored, out var bucket))
            {
                bucket = [];
                byStored[changed[i].Stored] = bucket;
            }

            bucket.Add(i);
        }

        var empty = new List<int>();
        var state = new byte[changed.Count]; // 0 — не відвідано, 1 — на стеку, 2 — завершено.

        for (var start = 0; start < changed.Count; start++)
        {
            if (state[start] != 0)
            {
                continue;
            }

            // Ітеративний DFS: глибина графа — кількість рядків таблиці, рекурсія тут зайва.
            var stack = new Stack<(int Node, int Edge)>();
            stack.Push((start, 0));
            state[start] = 1;

            while (stack.Count > 0)
            {
                var (node, edge) = stack.Pop();
                var targets = byStored.GetValueOrDefault(changed[node].Now) ?? empty;

                if (edge < targets.Count)
                {
                    stack.Push((node, edge + 1));
                    var next = targets[edge];

                    if (state[next] == 1)
                    {
                        var row = changed[next].Row;
                        return (row.RowKey, null,
                            worksheet.Cell(row.Number, firstEntered.Number).Address.ToString() ?? string.Empty);
                    }

                    if (state[next] == 0)
                    {
                        state[next] = 1;
                        stack.Push((next, 0));
                    }
                }
                else
                {
                    state[node] = 2;
                }
            }
        }

        return null;
    }
}
