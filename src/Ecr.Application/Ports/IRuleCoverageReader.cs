using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Розподіл живих рядків документів за комбінаціями значень колонок, які згадують
/// правила методології (матриця покриття ФВ-13.9).
/// </summary>
public interface IRuleCoverageReader
{
    /// <summary>Групує рядки примірників таблиць за значеннями колонок.</summary>
    /// <param name="tableDefIds">Таблиці прив'язок методології.</param>
    /// <param name="columnDefIds">Колонки, які згадують правила.</param>
    /// <param name="projectIds">
    /// Проєкти, документи яких беруться (аудит S7): лише ті, що користувач може
    /// читати. Порожньо — жодного рядка, а не «усі». Лічильники <c>Rows</c> і
    /// <c>Documents</c> рахуються вже по відфільтрованих рядках.
    /// </param>
    /// <param name="periodFrom">Нижня межа вікна періодів (включно).</param>
    /// <param name="periodTo">Верхня межа (включно).</param>
    /// <param name="limit">Стеля комбінацій; повертається щонайбільше <c>limit + 1</c> — ознака обрізання.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RuleCoverageCombination>> ReadAsync(
        IReadOnlyList<int> tableDefIds,
        IReadOnlyList<int> columnDefIds,
        IReadOnlyCollection<int> projectIds,
        int periodFrom,
        int periodTo,
        int limit,
        CancellationToken ct);
}

/// <summary>Одна комбінація значень і скільки рядків/документів її мають.</summary>
/// <param name="Values">Значення в порядку <c>columnDefIds</c>; <c>null</c> — комірки немає.</param>
/// <param name="Rows">Кількість рядків.</param>
/// <param name="Documents">Кількість різних документів.</param>
public sealed record RuleCoverageCombination(IReadOnlyList<CellValueData?> Values, long Rows, int Documents);
