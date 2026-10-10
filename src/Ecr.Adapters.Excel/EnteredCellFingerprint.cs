using System.Globalization;
using ClosedXML.Excel;

namespace Ecr.Adapters.Excel;

/// <summary>
/// Відбиток ВВЕДЕНИХ (не обчислюваних) комірок рядка книги на момент експорту
/// (AN-118, R1-01, HU-14 Q2 — «лише змінені комірки»).
/// </summary>
/// <remarks>
/// ⛔ Навіщо. Конфлікт «рядок змінено після експорту» (D1-02, D-338) визначає
/// версія РЯДКА, а її піднімає будь-який запис рядка — зокрема інтеграція в
/// сусідню колонку. Без того, що стояло в комірці при ЕКСПОРТІ, перегляд не
/// відрізняв «людина вписала X» від «X лишився в книзі з експорту», і
/// перезапис рядка (AN-114) повертав застаріле значення в комірки, яких людина
/// не чіпала, ще й закріплюючи їх як людські (<c>ImportOverwrite</c> у
/// <c>HumanOriginsSql</c> → <c>KeepManual</c>). Тепер експорт кладе відбиток
/// кожної введеної комірки рядка в карту (<see cref="ExcelRowRef.Cells"/>), а
/// перегляд у рядку з конфліктом версії бере лише комірки, де «книга ≠ експорт».
///
/// ⚠ 32 біти (8 hex-символів) на комірку, а не 16, як у
/// <see cref="CalculatedCellFingerprint"/>: там колізія змінює лише
/// формулювання відмови, а тут — «людина цю комірку не чіпала», тобто її правку
/// в конфліктному рядку перегляд мовчки не показав би. 1/2³² на правку
/// прийнятна; 1/65 536 — ні.
///
/// ⚠ Розбіжність представлення (Excel зберіг комірку інакше, ніж її поклав
/// експорт) дає «змінено» — колишню поведінку конфлікту, а не втрату: помилка
/// можлива лише в безпечний бік.
/// </remarks>
public static class EnteredCellFingerprint
{
    /// <summary>Довжина відбитка однієї комірки в рядку карти.</summary>
    public const int Width = 8;

    /// <summary>
    /// Відбиток рядка: відбитки введених (за картою — не обчислюваних) колонок
    /// блоку в їхньому порядку; <c>null</c> — у блоці немає введених колонок.
    /// </summary>
    /// <param name="worksheet">Аркуш книги.</param>
    /// <param name="columns">Колонки блоку з карти.</param>
    /// <param name="rowNumber">Номер рядка в книзі.</param>
    public static string? OfRow(IXLWorksheet worksheet, IReadOnlyList<ExcelColumnRef> columns, int rowNumber)
    {
        ArgumentNullException.ThrowIfNull(worksheet);
        ArgumentNullException.ThrowIfNull(columns);

        System.Text.StringBuilder? builder = null;

        foreach (var column in columns)
        {
            if (column.IsCalculated)
            {
                continue;
            }

            builder ??= new System.Text.StringBuilder();
            builder.Append(Of(worksheet.Cell(rowNumber, column.Number)));
        }

        return builder?.ToString();
    }

    /// <summary>
    /// Чи стоїть у комірці книги те саме, що поклав експорт.
    /// </summary>
    /// <param name="row">Рядок карти.</param>
    /// <param name="columns">Колонки блоку з карти.</param>
    /// <param name="column">Колонка комірки.</param>
    /// <param name="cell">Комірка книги.</param>
    /// <returns>
    /// <c>true</c> — книгу в цій комірці не змінювали; <c>false</c> — змінили
    /// або відбитка немає (книга до AN-118, колонка була обчислюваною на
    /// експорті): тоді комірка вважається зміненою — колишня поведінка.
    /// </returns>
    public static bool Unchanged(
        ExcelRowRef row, IReadOnlyList<ExcelColumnRef> columns, ExcelColumnRef column, IXLCell cell)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(cell);

        if (row.Cells is not { } stored || column.IsCalculated)
        {
            return false;
        }

        var index = 0;
        foreach (var candidate in columns)
        {
            if (candidate.Number == column.Number)
            {
                break;
            }

            if (!candidate.IsCalculated)
            {
                index++;
            }
        }

        var offset = index * Width;

        return offset + Width <= stored.Length
               && string.CompareOrdinal(stored, offset, Of(cell), 0, Width) == 0;
    }

    /// <summary>Відбиток однієї комірки: FNV-1a 32 біти канонічного тексту, 8 hex-символів.</summary>
    public static string Of(IXLCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);

        var hash = 2166136261u;

        foreach (var symbol in CalculatedCellFingerprint.Canonical(cell))
        {
            hash ^= symbol;
            hash *= 16777619u;
        }

        return hash.ToString("x8", CultureInfo.InvariantCulture);
    }
}
