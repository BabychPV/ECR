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
    /// Ідентифікатори рядків кількох таблиць ОДНИМ запитом; екземпляр без
    /// жодного рядка в результат не потрапляє.
    /// </summary>
    /// <remarks>
    /// ⛔ Q-165 (аудит фази 2, продуктивність), той самий випадок, що й
    /// <see cref="ICellStore.ReadSlicesAsync"/> поруч: <see cref="GetRowIdsAsync"/>
    /// у циклі по таблицях документа коштує другого походу в базу НА КОЖНУ з
    /// ~90 таблиць.
    /// </remarks>
    public Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, long>>> GetRowIdsBatchAsync(
        IReadOnlyList<long> tableInstanceIds, PeriodKey periodKey, CancellationToken ct);

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
    /// Створює кілька рядків ОДНИМ пакетом; <c>Id</c> повертаються в тому
    /// самому порядку, що й <paramref name="rowKeys"/>.
    /// </summary>
    /// <remarks>
    /// ⛔ Q-164 (аудит фази 2, продуктивність). Той самий прийом, що вже
    /// застосований для екземплярів таблиць
    /// (<c>EnsureTableInstancesAsync</c>/<c>MaterializeFixedRowsAsync</c>):
    /// ОДИН діапазон <c>SEQUENCE</c> на весь набір, ОДИН
    /// <c>SaveChangesAsync</c>. Виклик <see cref="CreateRowAsync"/> у циклі
    /// коштує двох походів у базу НА КОЖЕН рядок — на батчі в 100 рядків це
    /// вихід за бюджет запису (300 мс на 100 комірок) ще до першої комірки.
    /// </remarks>
    public Task<IReadOnlyList<long>> CreateRowsAsync(
        long tableInstanceId, PeriodKey periodKey, IReadOnlyList<RowKey> rowKeys, int ordinal, CancellationToken ct);

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

    /// <summary>Екземпляри таблиць документа за період.</summary>
    /// <remarks>
    /// Потрібні валідації і поданню: обидві працюють із ДОКУМЕНТОМ цілком, а
    /// не з окремою таблицею, і без цього переліку довелося б або
    /// перебирати структуру шаблону запитом на кожну таблицю, або тягнути
    /// <c>DbContext</c> у застосунок.
    /// </remarks>
    public Task<IReadOnlyList<TableInstanceRef>> GetTableInstancesAsync(
        long documentId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>
    /// Створює екземпляри таблиць документа за період, яких ще немає.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період; екземпляр існує окремо на кожен (R-A6).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки екземплярів створено.</returns>
    /// <remarks>
    /// ⛔ Без цього документ, створений через API, НЕ МАЄ ЖОДНОЇ ТАБЛИЦІ і
    /// ніколи її не отримає (`A7-30`): у всій системі <c>doc.TableInstance</c>
    /// лише читався. Екран документа показував порожнечу, а заповнити його не
    /// було куди — при тому, що аркуші документа існували й <c>sheetCount</c>
    /// їх чесно рахував.
    ///
    /// ⚠ Створення ЛІНИВЕ, при першому відкритті періоду. Заводити наперед
    /// усі періоди × усі таблиці означало б тисячу рядків на документ, якого
    /// ніхто не відкривав; та сама схема вже застосована до календаря періодів.
    ///
    /// ⚠ Ідемпотентність тримає <c>UQ_TableInstance</c>, а не перевірка перед
    /// вставкою: два одночасні відкриття документа — звичайна річ.
    /// </remarks>
    public Task<int> EnsureTableInstancesAsync(long documentId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>
    /// Рядки документа з ознакою <c>IsOrphaned</c> за період.
    /// </summary>
    /// <remarks>
    /// ⚠ Окремий метод від <see cref="GetOrphanFlagsAsync"/>, а не «той самий
    /// із іншим числом»: там перший аргумент — <c>TableInstanceId</c>, тут —
    /// <c>DocumentId</c>. Обидва <c>long</c>, тож переплутати їх компілятор не
    /// заважає — і саме так у <c>SubmitSheetHandler</c> з'явилася перевірка,
    /// яка мовчки нічого не знаходила.
    /// </remarks>
    public Task<IReadOnlyList<long>> GetOrphanedRowIdsAsync(
        long documentId, PeriodKey periodKey, CancellationToken ct);
}

/// <summary>Ідентичність екземпляра таблиці.</summary>
/// <param name="TableInstanceId">Екземпляр.</param>
/// <param name="DocumentId">Документ, якому він належить.</param>
/// <param name="TableDefId">Опис таблиці.</param>
/// <param name="TemplateVersionId">Версія шаблону — ключ знімка метаданих.</param>
/// <param name="PeriodKey">Період екземпляра; він же ключ партиції.</param>
public sealed record TableInstanceRef(
    long TableInstanceId, long DocumentId, int TableDefId, int TemplateVersionId, int PeriodKey);
