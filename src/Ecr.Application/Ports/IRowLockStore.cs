using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>✎ B75: сховище блокувань рядків (<c>doc.RowLock</c>).</summary>
public interface IRowLockStore
{
    /// <summary>Факти документа за період для рішення про блокування; <c>null</c> — документа немає.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="utcNow">Момент рішення (ефективний стан періоду).</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<RowLockTarget?> GetTargetAsync(long documentId, int periodKey, DateTime utcNow, CancellationToken ct);

    /// <summary>
    /// Живі рядки екземпляра таблиці документа за період ОДНИМ запитом: ключ → <c>TableRow.Id</c> (для номера рядка в
    /// відмові <c>rowAlreadyLocked</c>: порядок зрізу — шаблон за <c>Ordinal</c>, далі за Id).
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
    /// <param name="tableDefId">Таблиця версії шаблону.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<KeyValuePair<string, long>>> LiveRowsAsync(
        long documentId, int periodKey, long? sheetCopyId, int tableDefId, CancellationToken ct);

    /// <summary>Які з ключів — наявні (не видалені) рядки екземпляра таблиці документа за період.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sheetCopyId">Копія аркуша.</param>
    /// <param name="tableDefId">Таблиця шаблону.</param>
    /// <param name="rowKeys">Ключі рядків.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlySet<string>> ExistingRowKeysAsync(
        long documentId, int periodKey, long? sheetCopyId, int tableDefId, IReadOnlyCollection<string> rowKeys,
        CancellationToken ct);

    /// <summary>
    /// Ключі рядків, на які вже є незняте й НЕ прострочене (<c>LockUntil &gt; utcNow</c>) блокування, що перетинає
    /// проміжок <c>[from, until)</c>.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sheetCopyId">Копія аркуша.</param>
    /// <param name="sheetCode">Код аркуша.</param>
    /// <param name="tableCode">Код таблиці.</param>
    /// <param name="rowKeys">Ключі рядків.</param>
    /// <param name="from">Початок (UTC).</param>
    /// <param name="until">Кінець (UTC, виключно).</param>
    /// <param name="utcNow">Поточний момент: прострочене блокування не конфліктує (F13 D3).</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<string>> OverlappingAsync(
        long documentId, int periodKey, long? sheetCopyId, string sheetCode, string tableCode,
        IReadOnlyCollection<string> rowKeys, DateTime from, DateTime until, DateTime utcNow, CancellationToken ct);

    /// <summary>Незняті й не прострочені блокування документа за період (з іменем власника).</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="utcNow">Момент.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RowLockRecord>> ListAsync(long documentId, int periodKey, DateTime utcNow, CancellationToken ct);

    /// <summary>Блокування документа для зміни (відстежуване); <c>null</c> — немає.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="lockId">Блокування.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<RowLock?> FindAsync(long documentId, long lockId, CancellationToken ct);

    /// <summary>
    /// UPDLOCK на рядку документа — серіалізує створення блокувань того самого документа (перевірка перетину й вставка).
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task LockDocumentAsync(long documentId, CancellationToken ct);

    /// <summary>
    /// ✎ B75: діючі на <paramref name="utcNow"/> блокування рядків документа за період, власник яких — НЕ
    /// <paramref name="userId"/>: перевірка записувача ПІД замком аркуша (гонка «блокування між рішенням і записом»).
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період екземпляра.</param>
    /// <param name="userId">Хто пише.</param>
    /// <param name="utcNow">Момент.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RowLockKey>> LockedByOthersAsync(
        long documentId, int periodKey, int userId, DateTime utcNow, CancellationToken ct);

    /// <summary>
    /// Незняті блокування рядка (відстежувані) — їх знімає видалення рядка: ключ, якого більше немає, не мусить замикати
    /// новий рядок із тим самим ключем.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період екземпляра.</param>
    /// <param name="key">Рядок.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RowLock>> FindUnreleasedForRowAsync(
        long documentId, int periodKey, RowLockKey key, CancellationToken ct);

    /// <summary>Додає нові блокування.</summary>
    /// <param name="locks">Блокування.</param>
    public void AddRange(IEnumerable<RowLock> locks);
}

/// <summary>Факти документа за період для рішення про блокування.</summary>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="OwnerUserId">Власник документа.</param>
/// <param name="ProjectStatus">Стан проєкту.</param>
/// <param name="IsArchiving">Триває архівація.</param>
/// <param name="PeriodState">Ефективний стан періоду (з винятком документа); <c>null</c> — періоду немає.</param>
/// <param name="TimeZoneId">Часовий пояс проєкту (IANA).</param>
/// <param name="TemplateVersionId">Версія шаблону документа за період.</param>
/// <param name="BusinessKey">Номер документа.</param>
public sealed record RowLockTarget(
    int ProjectId,
    int OwnerUserId,
    ProjectStatus ProjectStatus,
    bool IsArchiving,
    PeriodState? PeriodState,
    string TimeZoneId,
    int TemplateVersionId,
    string BusinessKey);

/// <summary>Ідентичність заблокованого рядка в документі за період (F13 D2: таблиця — разом з аркушем).</summary>
/// <param name="SheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
/// <param name="SheetCode">Код аркуша.</param>
/// <param name="TableCode">Код таблиці (унікальний у межах аркуша).</param>
/// <param name="RowKey">Ключ рядка.</param>
public readonly record struct RowLockKey(long? SheetCopyId, string SheetCode, string TableCode, string RowKey);

/// <summary>Блокування з іменем власника.</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="SheetCopyId">Копія аркуша.</param>
/// <param name="SheetCode">Код аркуша.</param>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="RowKey">Ключ рядка.</param>
/// <param name="LockFrom">Початок (UTC).</param>
/// <param name="LockUntil">Кінець (UTC, виключно).</param>
/// <param name="OwnerUserId">Власник блокування.</param>
/// <param name="OwnerDisplayName">Ім'я власника; <c>null</c> — невідоме.</param>
/// <param name="Note">Примітка.</param>
public sealed record RowLockRecord(
    long Id,
    long? SheetCopyId,
    string SheetCode,
    string TableCode,
    string RowKey,
    DateTime LockFrom,
    DateTime LockUntil,
    int OwnerUserId,
    string? OwnerDisplayName,
    string? Note);
