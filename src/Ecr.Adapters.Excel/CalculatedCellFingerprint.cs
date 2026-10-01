using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace Ecr.Adapters.Excel;

/// <summary>
/// Відбиток обчислюваних комірок рядка книги на момент експорту (P3 імпорту).
/// </summary>
/// <remarks>
/// ⛔ Навіщо. Обчислювана комірка, що не збігається з поточним значенням
/// системи, відхиляється (<c>ECR-CELL-4221</c>), але причин дві, і людині вони
/// кажуть протилежне: «ви змінили число, яке рахує система» — поверніть його;
/// «система перерахувала комірку після експорту» — книга застаріла,
/// вивантажте її наново. Без того, що стояло в комірці при ЕКСПОРТІ, їх не
/// розрізнити: обидві виглядають як «у файлі не те, що в базі». Тому експорт
/// кладе в карту книги відбиток кожної обчислюваної комірки рядка, а імпорт
/// порівнює з ним те, що в книзі зараз: збіг — книгу не чіпали, змінилася
/// система; розбіжність — змінив користувач.
///
/// ⚠ Відбиток — 16 біт на комірку (4 hex-символи), а не саме значення: карта
/// книги на 91 таблиці вже сягає сотень кілобайт (<see cref="ExcelWorkbookMap.ChunkSize"/>),
/// і значення read-only колонок бувають довгими текстами. Ціна колізії —
/// лише формулювання причини (відмова лишається відмовою), тож 1/65 536 тут
/// прийнятна.
/// </remarks>
public static class CalculatedCellFingerprint
{
    /// <summary>Довжина відбитка однієї комірки в рядку карти.</summary>
    public const int Width = 4;

    /// <summary>
    /// Відбиток рядка: відбитки обчислюваних (за картою) колонок блоку в їхньому порядку;
    /// <c>null</c> — у блоці немає обчислюваних колонок.
    /// </summary>
    /// <param name="worksheet">Аркуш книги.</param>
    /// <param name="columns">Колонки блоку з карти.</param>
    /// <param name="rowNumber">Номер рядка в книзі.</param>
    public static string? OfRow(IXLWorksheet worksheet, IReadOnlyList<ExcelColumnRef> columns, int rowNumber)
    {
        ArgumentNullException.ThrowIfNull(worksheet);
        ArgumentNullException.ThrowIfNull(columns);

        StringBuilder? builder = null;

        foreach (var column in columns)
        {
            if (!column.IsCalculated)
            {
                continue;
            }

            builder ??= new StringBuilder();
            builder.Append(Of(worksheet.Cell(rowNumber, column.Number)));
        }

        return builder?.ToString();
    }

    /// <summary>
    /// Чи збігається комірка книги з відбитком експорту.
    /// </summary>
    /// <param name="row">Рядок карти.</param>
    /// <param name="columns">Колонки блоку з карти.</param>
    /// <param name="column">Колонка комірки.</param>
    /// <param name="cell">Комірка книги.</param>
    /// <returns>
    /// <c>true</c> — у книзі те саме, що поклав експорт; <c>false</c> — змінено
    /// або відбитка немає (книга до цієї правки, колонка стала обчислюваною
    /// після експорту): тоді причина лишається колишньою — «змінили».
    /// </returns>
    public static bool Unchanged(
        ExcelRowRef row, IReadOnlyList<ExcelColumnRef> columns, ExcelColumnRef column, IXLCell cell)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(cell);

        if (row.Calc is not { } stored || !column.IsCalculated)
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

            if (candidate.IsCalculated)
            {
                index++;
            }
        }

        var offset = index * Width;

        return offset + Width <= stored.Length
               && string.CompareOrdinal(stored, offset, Of(cell), 0, Width) == 0;
    }

    /// <summary>Відбиток однієї комірки.</summary>
    /// <remarks>
    /// ⚠ Формула дає сталий відбиток, а не відбиток тексту формули: формулу в
    /// обчислюваній колонці імпорт пропускає й так, а значення на її місці —
    /// завжди правка користувача.
    /// </remarks>
    public static string Of(IXLCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);

        if (cell.HasFormula)
        {
            return Hash("f:");
        }

        var value = cell.Value;

        var text = value.Type switch
        {
            XLDataType.Blank => "b:",
            XLDataType.Number => "n:" + value.GetNumber().ToString("R", CultureInfo.InvariantCulture),
            XLDataType.Boolean => "l:" + (value.GetBoolean() ? "1" : "0"),
            XLDataType.DateTime => "d:" + value.GetDateTime().ToString("O", CultureInfo.InvariantCulture),
            XLDataType.TimeSpan => "t:" + value.GetTimeSpan().ToString("c", CultureInfo.InvariantCulture),
            XLDataType.Text => TextOrBlank(value.GetText()),
            _ => "x:" + value.ToString(CultureInfo.InvariantCulture),
        };

        return Hash(text);
    }

    /// <summary>
    /// Текст із самих пробілів — те саме, що порожньо: так його читає й імпорт.
    /// </summary>
    private static string TextOrBlank(string text) => string.IsNullOrWhiteSpace(text) ? "b:" : "s:" + text;

    /// <summary>FNV-1a 32 біти, згорнутий до 16 — 4 hex-символи.</summary>
    private static string Hash(string text)
    {
        var hash = 2166136261u;

        foreach (var symbol in text)
        {
            hash ^= symbol;
            hash *= 16777619u;
        }

        var folded = (hash >> 16) ^ (hash & 0xFFFF);

        return folded.ToString("x4", CultureInfo.InvariantCulture);
    }
}
