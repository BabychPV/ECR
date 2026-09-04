using Ecr.Domain.Entities.Configuration;
using Ecr.Expressions.Parsing;

namespace Ecr.Expressions.Binding;

/// <summary>
/// Розкриває діапазони рядків у явний список <c>RowKey</c> **на момент
/// `Publish`**. У рантаймі діапазонів не існує (B03 §4).
/// </summary>
/// <remarks>
/// Саме це робить зміну <c>Ordinal</c> після публікації безпечною: формула вже
/// посилається на конкретні рядки. Якби діапазон обчислювався в рантаймі за
/// <c>Ordinal</c>, презентаційна правка мовчки змінювала б числа — найгірший
/// клас помилок, бо даних не зіпсовано, а результат інший.
/// </remarks>
public sealed class RangeExpander
{
    /// <summary>Розкриває діапазон у список ключів.</summary>
    /// <param name="table">Таблиця, в якій живуть рядки.</param>
    /// <param name="fromRowKey">Початок діапазону.</param>
    /// <param name="toRowKey">Кінець діапазону.</param>
    /// <param name="diagnostics">
    /// Куди складати зауваження; <c>null</c> — виклик без діагностики, тоді
    /// непридатний діапазон просто дає порожній список.
    /// </param>
    /// <param name="position">Позиція діапазону в тексті виразу.</param>
    /// <returns>Ключі рядків у порядку <c>Ordinal</c> на момент виклику.</returns>
    public IReadOnlyList<string> Expand(
        TableDef table,
        string fromRowKey,
        string toRowKey,
        List<ExpressionDiagnostic>? diagnostics = null,
        int position = 0)
    {
        ArgumentNullException.ThrowIfNull(table);

        // Видалені рядки не входять у діапазон: soft delete (ФВ-7.6) лишає їх
        // у таблиці заради історії, але «сюди більше не входить» — це і є сенс
        // видалення.
        var ordered = table.Rows
            .Where(r => !r.IsDeleted)
            .OrderBy(r => r.Ordinal)
            .Select(r => r.RowKeyValue)
            .ToList();

        var from = ordered.IndexOf(fromRowKey);
        var to = ordered.IndexOf(toRowKey);

        if (from < 0 || to < 0)
        {
            var missing = from < 0 ? fromRowKey : toRowKey;
            diagnostics?.Add(new ExpressionDiagnostic(
                ExpressionErrors.Unresolved,
                $"Рядка '{missing}' немає в таблиці '{table.Code}' — межа діапазону не резолвиться.",
                position, 1));
            return [];
        }

        if (from > to)
        {
            // Перевернутий діапазон майже завжди означає перевернуті межі, а не
            // намір; мовчки їх переставити означало б порахувати не те, що
            // написано, і сховати друкарську помилку.
            diagnostics?.Add(new ExpressionDiagnostic(
                ExpressionErrors.Unresolved,
                $"Діапазон '{fromRowKey}:{toRowKey}' записаний у зворотному порядку.",
                position, 1));
            return [];
        }

        return ordered[from..(to + 1)];
    }
}
