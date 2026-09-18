using Ecr.Application.Common;

namespace Ecr.Application.Ports;

/// <summary>Знахідка перевірки узгодженості, як її бачить читач.</summary>
/// <param name="Id">Ідентифікатор рядка журналу.</param>
/// <param name="DetectedAt">Момент виявлення в UTC.</param>
/// <param name="Severity">Вага: 1 інформація, 2 попередження, 3 помилка.</param>
/// <param name="RuleCode">Код правила: <c>ORPHANED_CELL</c>, <c>BROKEN_FK</c>, <c>ARCHIVE_CHECKSUM</c>.</param>
/// <param name="EntityType">Тип зачепленої сутності: <c>doc.CellValue</c>, <c>doc.TableRow</c>…</param>
/// <param name="EntityId">Ідентифікатор зачепленої сутності.</param>
/// <param name="Message">
/// Текст знахідки, як його записала задача.
/// </param>
/// <param name="ResolvedAt">
/// Момент, коли знахідку закрили; <c>null</c> — вона ще актуальна.
/// </param>
/// <param name="ResolvedByUserId">Хто закрив; <c>null</c> — ніхто.</param>
/// <remarks>
/// ⚠ <paramref name="Message"/> приходить із <c>aud.ConsistencyIssue</c>
/// українською і НЕ локалізується: механізм каталогу рядків існує для відмов
/// API (<c>err.*</c>), а не для журналу знахідок. Поле віддається як є —
/// перекладати його означало б завести другий каталог для текстів, які пише
/// фонова задача, і це окрема робота, а не побічний ефект показу журналу.
/// </remarks>
public sealed record ConsistencyIssueView(
    long Id,
    DateTime DetectedAt,
    byte Severity,
    string RuleCode,
    string? EntityType,
    long? EntityId,
    string Message,
    DateTime? ResolvedAt,
    int? ResolvedByUserId);

/// <summary>
/// Читання журналу знахідок <c>aud.ConsistencyIssue</c>.
/// </summary>
/// <remarks>
/// ⛔ Журнал **тільки читається** — так само, як аудит (<see cref="IAuditReader"/>):
/// знахідку закриває той, хто усунув причину, а не той, хто на неї дивиться.
/// Метод зміни тут з'явився б лише разом із таким сценарієм.
///
/// ⚠ Окремий порт від <see cref="IAuditReader"/>, хоч таблиця й у схемі
/// <c>aud</c>: аудит відповідає на «хто змінив це число», а тут — «що в даних
/// зламано». Один інтерфейс на два різні питання означав би, що кожен
/// споживач тягне за собою половину, якої не питає.
/// </remarks>
public interface IConsistencyIssueReader
{
    /// <summary>
    /// Сторінка знахідок, від найновішої до найстарішої.
    /// </summary>
    /// <param name="ruleCode">Фільтр за кодом правила; <c>null</c> — усі.</param>
    /// <param name="openOnly">
    /// <c>true</c> — лише ще не закриті (<c>ResolvedAt IS NULL</c>).
    /// </param>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Порядок — за <c>Id DESC</c>, і курсор іде тим самим напрямком: журнал
    /// читають із кінця («що знайшлося цієї ночі»), а не з початку.
    /// </remarks>
    public Task<PagedResult<ConsistencyIssueView>> ReadIssuesAsync(
        string? ruleCode, bool openOnly, CursorRequest page, CancellationToken ct);
}
