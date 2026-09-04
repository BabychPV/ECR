// src/Ecr.Application/Ports/IRowStore.cs

using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Рядки таблиці документа: ідентичність, версія, створення.
/// </summary>
/// <remarks>
/// ⚠ Порт додано, а не вбудовано в <see cref="ICellStore"/>: той віддає
/// <b>значення</b> комірок, а тут потрібні <b>версії рядків</b>. Розширювати
/// <c>ICellStore</c> означало б змінити контракт (`02-contracts.md` §5), тоді
/// як додавання сусіднього порту нічого не ламає.
///
/// Без цього порту неможливо виконати найважчу вимогу запису: звірити
/// <c>baseVersion</c> кожного зачепленого рядка і відхилити <b>весь</b> батч
/// при розбіжності (B04 §2.3). Читати версії разом зі значеннями не можна —
/// зріз повертає лише непорожні комірки, а рядок може бути зачеплений і
/// таким, у якого всі комірки порожні.
/// </remarks>
public interface IRowStore
{
    /// <summary>
    /// Ідентичність екземпляра таблиці: до якого документа і якої версії
    /// шаблону він належить.
    /// </summary>
    /// <remarks>
    /// Потрібно, бо <c>PatchCellsRequest</c> несе лише <c>TableInstanceId</c>,
    /// а щоб резолвити коди колонок у <c>ColumnDefId</c>, обробнику потрібен
    /// знімок структури — тобто <c>TemplateVersionId</c>. Класти його в запит
    /// не можна: клієнт не має диктувати, за якою версією тлумачити дані.
    /// </remarks>
    public Task<TableInstanceRef> ResolveTableInstanceAsync(long tableInstanceId, CancellationToken ct);

    /// <summary>
    /// Поточні версії рядків таблиці: <c>RowKey</c> → hex <c>rowversion</c>.
    /// </summary>
    /// <remarks>
    /// Один виклик на батч, не на рядок: бюджет запису — 300 мс на 100 комірок,
    /// і N запитів у нього не вкладаються.
    /// </remarks>
    public Task<IReadOnlyDictionary<string, string>> GetRowVersionsAsync(
        long tableInstanceId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>
    /// Ідентифікатори рядків за ключами: <c>RowKey</c> → <c>TableRow.Id</c>.
    /// </summary>
    public Task<IReadOnlyDictionary<string, long>> GetRowIdsAsync(
        long tableInstanceId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>
    /// Створює рядок і повертає його <c>Id</c>.
    /// </summary>
    /// <remarks>
    /// <c>Id</c> береться з <c>SEQUENCE</c> <b>до</b> вставки — це те, що
    /// дозволяє вантажити <c>TableRow</c> і <c>CellValue</c> одним проходом
    /// <c>SqlBulkCopy</c>. З <c>IDENTITY</c> довелося б вставляти рядки,
    /// зчитувати ключі й лише потім комірки.
    /// </remarks>
    public Task<long> CreateRowAsync(
        long tableInstanceId, PeriodKey periodKey, RowKey rowKey, int ordinal, CancellationToken ct);

    /// <summary>
    /// Піднімає <c>ModifiedAt</c> зачеплених рядків.
    /// </summary>
    /// <remarks>
    /// ⚠ Саме це змінює <c>RowVersion</c>. Забути — означає зламати
    /// оптимістичне блокування <b>тихо</b>: наступний запис зі застарілою
    /// <c>baseVersion</c> пройде як коректний, і чужа правка зникне без сліду
    /// (B04 §2.4).
    /// </remarks>
    public Task TouchRowsAsync(IReadOnlyList<long> rowIds, DateTime utcNow, CancellationToken ct);

    /// <summary>
    /// Збережені ознаки «осиротілості» рядків: <c>TableRow.Id</c> → <c>IsOrphaned</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Саме <b>читання збереженого поля</b>, а не обчислення. Перевіряти
    /// чинність записів реєстру на кожен зріз означало б додати запит на
    /// кожен рядок і вийти за бюджет 400 мс (ФВ-8.13, <c>D-98</c>).
    /// Ознаку ставить <c>OrphanScanJob</c> уночі.
    /// </remarks>
    public Task<IReadOnlyDictionary<long, bool>> GetOrphanFlagsAsync(
        long tableInstanceId, PeriodKey periodKey, CancellationToken ct);
}

/// <summary>Ідентичність екземпляра таблиці.</summary>
/// <param name="TableInstanceId">Екземпляр.</param>
/// <param name="DocumentId">Документ, якому він належить.</param>
/// <param name="TableDefId">Опис таблиці.</param>
/// <param name="TemplateVersionId">Версія шаблону — ключ знімка метаданих.</param>
/// <param name="PeriodKey">Період екземпляра; він же ключ партиції.</param>
public sealed record TableInstanceRef(
    long TableInstanceId, long DocumentId, int TableDefId, int TemplateVersionId, int PeriodKey);
