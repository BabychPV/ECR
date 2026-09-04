using Ecr.Domain.Entities.Configuration;

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
    /// <returns>Ключі рядків у порядку <c>Ordinal</c> на момент виклику.</returns>
    public IReadOnlyList<string> Expand(TableDef table, string fromRowKey, string toRowKey)
        => throw new NotImplementedException(
            "TODO: знайти обидва рядки в table.Rows; впорядкувати рядки за Ordinal; " +
            "повернути всі RowKey між ними включно, ігноруючи IsDeleted. " +
            "Якщо межа не знайдена або from стоїть після to — діагностика ECR-TMPL-4222.");
}
