using Ecr.Application.Common;

namespace Ecr.Application.Ports;

/// <summary>Знахідка перевірки узгодженості, як її бачить читач.</summary>
/// <param name="Id">Ідентифікатор рядка журналу.</param>
/// <param name="DetectedAt">Момент виявлення в UTC.</param>
/// <param name="Severity">Вага: 1 інформація, 2 попередження, 3 помилка.</param>
/// <param name="RuleCode">
/// Код правила: <c>ORPHANED_CELL</c> (комірка посилається на відсутній запис
/// довідника; <c>doc.CellValue</c>, вага 2), <c>BROKEN_FK</c> (рядок посилається
/// на відсутній екземпляр таблиці; <c>doc.TableRow</c>, вага 3),
/// <c>ARCHIVE_CHECKSUM</c> (контрольні суми архіву й джерела не збіглися;
/// <c>itg.ArchiveRun</c>, вага 3), <c>UNBOUND_CALCULATED_COLUMN</c> (колонка
/// <c>Calculated</c> версії живого проєкту без чинної прив'язки методології;
/// <c>cfg.ColumnDef</c>, вага 2), <c>UNSOURCED_FORMULA_COLUMN</c> (колонка
/// <c>Formula</c> опублікованої або виведеної з обігу версії без формули шаблону
/// й без чинної прив'язки методології; <c>cfg.ColumnDef</c>, вага 2).
/// </param>
/// <param name="EntityType">Тип зачепленої сутності: <c>doc.CellValue</c>, <c>doc.TableRow</c>…</param>
/// <param name="EntityId">Ідентифікатор зачепленої сутності.</param>
/// <param name="Message">
/// Текст знахідки, як його записала задача.
/// </param>
/// <param name="ResolvedAt">
/// Момент, коли знахідку закрили; <c>null</c> — вона ще актуальна.
/// </param>
/// <param name="ResolvedByUserId">Хто закрив; <c>null</c> — ніхто.</param>
/// <param name="Where">
/// Місце знахідки в структурі (документ, аркуш, таблиця, рядок, колонка) — лише КОДИ.
/// <c>null</c> — місце невідоме або читач його не бачить (див. <c>GetConsistencyIssuesHandler</c>).
/// </param>
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
    int? ResolvedByUserId,
    ConsistencyIssueWhere? Where = null);

/// <summary>Місце знахідки консистентності: лише бізнес-коди, без значень комірок і без назв.</summary>
/// <param name="DocumentId">Документ; <c>null</c> — знахідка про структуру шаблону, а не документа.</param>
/// <param name="DocumentBusinessKey">Бізнес-ключ документа; <c>null</c> разом із <paramref name="DocumentId"/>.</param>
/// <param name="SheetCode">Код аркуша.</param>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="RowKey">Ключ рядка; <c>null</c> — знахідка не на рівні рядка.</param>
/// <param name="ColumnCode">Код колонки; <c>null</c> — колонку не визначено.</param>
public sealed record ConsistencyIssueWhere(
    long? DocumentId,
    string? DocumentBusinessKey,
    string? SheetCode,
    string? TableCode,
    string? RowKey,
    string? ColumnCode);

/// <summary>
/// Сире місце знахідки з базою: ідентифікатори, потрібні обробнику для рішення про
/// видимість, і коди для відповіді. НЕ віддається клієнтові як є.
/// </summary>
/// <param name="ProjectId">Проєкт документа; <c>null</c> — рівень шаблону.</param>
/// <param name="TemplateVersionId">Версія шаблону, чия структура задає межі читання.</param>
/// <param name="PeriodKey">Період рядка; <c>null</c> — рівень шаблону.</param>
/// <param name="SheetDefId">Аркуш.</param>
/// <param name="TableDefId">Таблиця.</param>
/// <param name="Where">Коди місця.</param>
public sealed record ConsistencyLocation(
    int? ProjectId,
    int TemplateVersionId,
    int? PeriodKey,
    int SheetDefId,
    int TableDefId,
    ConsistencyIssueWhere Where);

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
    /// <param name="severity">Вага 1..3; <c>null</c> — будь-яка.</param>
    /// <param name="query">
    /// Підрядок, уже обрізаний і непорожній, або <c>null</c>. Шукається в тексті,
    /// коді правила й ідентифікаторі сутності ЗАПИТОМ, до <c>TOP</c>: постфільтр
    /// по сторінці дав би хибні курсор і «є ще».
    /// </param>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Порядок — за <c>Id DESC</c>, і курсор іде тим самим напрямком: журнал
    /// читають із кінця («що знайшлося цієї ночі»), а не з початку.
    /// </remarks>
    public Task<ConsistencyIssuePage> ReadIssuesAsync(
        string? ruleCode, bool openOnly, byte? severity, string? query, CursorRequest page, CancellationToken ct);

    /// <summary>
    /// Лічильники знахідок за вагою (усі коди правил) — для смуги показників екрана.
    /// </summary>
    /// <param name="openOnly"><c>true</c> — лише ще не закриті.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<ConsistencySummary> ReadSummaryAsync(bool openOnly, CancellationToken ct);

    /// <summary>
    /// Розкладає сутності знахідок на місце в структурі — ПАКЕТНО, не більше одного запиту
    /// на тип сутності, незалежно від кількості знахідок.
    /// </summary>
    /// <param name="entities">Пари <c>(EntityType, EntityId)</c> знахідок однієї сторінки.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Лише розкладені пари. <c>doc.CellValue</c> (id рядка) → документ/аркуш/таблиця/рядок
    /// (колонку не визначити: знахідка пише лише id рядка); <c>cfg.ColumnDef</c> → аркуш/таблиця/колонка.
    /// <c>doc.TableRow</c> (екземпляра таблиці вже немає), <c>itg.ArchiveRun</c> і невідомі типи
    /// відсутні. Пара, чия сутність зникла, теж відсутня.
    /// </returns>
    public Task<IReadOnlyDictionary<(string EntityType, long EntityId), ConsistencyLocation>> ResolveLocationsAsync(
        IReadOnlyCollection<(string EntityType, long EntityId)> entities, CancellationToken ct);
}

/// <summary>Лічильники знахідок за вагою в межах фільтра переліку (без фільтра ваги).</summary>
/// <param name="Info">Вага 1.</param>
/// <param name="Warnings">Вага 2.</param>
/// <param name="Errors">Вага 3.</param>
public sealed record ConsistencySeverityTotals(int Info, int Warnings, int Errors);

/// <summary>Сторінка знахідок із загальними лічильниками.</summary>
/// <param name="Items">Елементи сторінки.</param>
/// <param name="NextCursor">Курсор наступної сторінки; <c>null</c> — кінець.</param>
/// <param name="TotalCount">Скільки знахідок збігається з УСІМА фільтрами (разом із вагою).</param>
/// <param name="Totals">Розбивка за вагою за фільтрами без ваги.</param>
public sealed record ConsistencyIssuePage(
    IReadOnlyList<ConsistencyIssueView> Items,
    string? NextCursor,
    int? TotalCount,
    ConsistencySeverityTotals Totals);

/// <summary>Загальні лічильники журналу знахідок.</summary>
/// <param name="Info">Знахідок ваги 1 (інформація).</param>
/// <param name="Warnings">Знахідок ваги 2 (попередження).</param>
/// <param name="Errors">Знахідок ваги 3 (помилка).</param>
/// <param name="Total">Усього (інша вага, якщо така з'явиться, потрапляє лише сюди).</param>
/// <param name="LastDetectedAt">Момент найновішої знахідки в UTC; <c>null</c> — журнал порожній.</param>
public sealed record ConsistencySummary(
    int Info, int Warnings, int Errors, int Total, DateTime? LastDetectedAt);
