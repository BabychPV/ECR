using Ecr.Application.Common;

namespace Ecr.Application.Ports;

/// <summary>Зміна комірки в журналі, як її бачить читач аудиту.</summary>
/// <param name="ChangedAt">Момент зміни в UTC.</param>
/// <param name="PeriodKey">Звітний період.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="RowKey">Ключ рядка — щоб журнал читався без join.</param>
/// <param name="ColumnDefId">Колонка.</param>
/// <param name="OldValue">Старе значення.</param>
/// <param name="NewValue">Нове значення.</param>
/// <param name="ChangedByUserId">Автор — <b>UserId</b>, не SID (R-A2, D-86).</param>
/// <param name="Origin">Звідки зміна: правка, імпорт, перерахунок, міграція.</param>
/// <param name="IsLateEdit">Зміна в <c>Grace</c> або після <c>Reopen</c> (D-70).</param>
public sealed record CellChangeView(
    DateTime ChangedAt,
    int PeriodKey,
    long DocumentId,
    string RowKey,
    int ColumnDefId,
    string? OldValue,
    string? NewValue,
    int ChangedByUserId,
    string Origin,
    bool IsLateEdit);

/// <summary>Структурна зміна в журналі, як її бачить читач.</summary>
/// <param name="ChangedAt">Момент зміни в UTC.</param>
/// <param name="EntityType">Сутність: <c>cfg.RegistryDef</c>, <c>cfg.RegistryRuleDef</c>.</param>
/// <param name="EntityId">Ідентифікатор сутності; <c>0</c> — операція над набором.</param>
/// <param name="Operation">Що зробили: <c>SaveRules</c>, <c>SwitchSourceSet</c>.</param>
/// <param name="OldJson">Стан до зміни.</param>
/// <param name="NewJson">Стан після зміни.</param>
/// <param name="ChangeReason">Причина, якщо її вимагала операція.</param>
/// <param name="ChangedByUserId">Автор — <b>UserId</b>, не SID (R-A2, D-86).</param>
public sealed record StructureChangeView(
    DateTime ChangedAt,
    string EntityType,
    int EntityId,
    string Operation,
    string? OldJson,
    string? NewJson,
    string? ChangeReason,
    int ChangedByUserId);

/// <summary>Фільтр журналу змін комірок.</summary>
/// <remarks>
/// ⛔ Вікно часу — ОБОВ'ЯЗКОВІ поля запису, а не необов'язкові параметри:
/// <c>aud.CellChange</c> партиційована за <c>ChangedAt</c>, і запит без меж
/// пішов би по всіх партиціях, включно з архівними. Решта полів лише звужує
/// вже відсічений вікном набір і тому має значення за замовчуванням.
///
/// ⚠ Чому запис, а не сім позиційних параметрів. Сигнатура
/// <c>(from, to, documentId, rowKey, columnDefId, userId, origin, lateOnly, page, ct)</c>
/// складається з чотирьох підряд «необов'язкових ідентифікаторів», два з яких
/// цілі числа: переставлені місцями <c>columnDefId</c> і <c>changedByUserId</c>
/// компілюються мовчки і дають журнал ЧУЖОЇ комірки. Іменовані поля роблять
/// таку перестановку неможливою.
/// </remarks>
/// <param name="From">Початок вікна в UTC, включно.</param>
/// <param name="To">Кінець вікна в UTC, виключно.</param>
/// <param name="DocumentId">Фільтр за документом; <c>null</c> — усі.</param>
/// <param name="RowKey">Ключ рядка; має сенс лише разом із <paramref name="DocumentId"/>.</param>
/// <param name="ColumnDefId">Колонка; має сенс лише разом із <paramref name="DocumentId"/>.</param>
/// <param name="ChangedByUserId">Автор зміни — <b>UserId</b>, не SID (R-A2, D-86).</param>
/// <param name="Origin">Походження: <c>UserEdit</c>, <c>Import</c>, <c>Recalculation</c>, <c>Migration</c>.</param>
/// <param name="LateOnly">Лише пізні правки (<c>Grace</c>/після <c>Reopen</c>, D-70).</param>
public sealed record CellChangeFilter(
    DateTime From,
    DateTime To,
    long? DocumentId = null,
    string? RowKey = null,
    int? ColumnDefId = null,
    int? ChangedByUserId = null,
    string? Origin = null,
    bool LateOnly = false)
{
    /// <summary>Фільтр адресує РІВНО ОДНУ комірку — документ, рядок і колонку.</summary>
    /// <remarks>
    /// ⚠ Це не зручність, а МЕЖА ДОСТУПУ (D15-16): історію своєї комірки
    /// бачить той, хто бачить документ, а загальний журнал — лише
    /// <c>Security.ViewAudit</c>. Адреса комірки повна тоді й лише тоді, коли
    /// задані всі три складники: без <c>ColumnDefId</c> запит віддав би весь
    /// рядок, без <c>RowKey</c> — усю колонку документа.
    /// </remarks>
    public bool IsSingleCell
        => DocumentId is not null && RowKey is not null && ColumnDefId is not null;

    /// <summary>Фільтр згадує адресу комірки, але без документа — адреси немає.</summary>
    /// <remarks>
    /// <c>RowKey</c> унікальний у межах екземпляра таблиці, а не системи:
    /// «R1» є в кожному документі. Пошук за ним без <c>DocumentId</c> зібрав
    /// би рядки з чужих документів і виглядав би як відповідь.
    /// </remarks>
    public bool IsCellAddressWithoutDocument
        => DocumentId is null && (RowKey is not null || ColumnDefId is not null);
}

/// <summary>Фільтр загального журналу структурних змін (<c>BE-16</c>).</summary>
/// <remarks>
/// ⛔ Вікно часу — ОБОВ'ЯЗКОВІ поля, з тієї самої причини, що в
/// <see cref="CellChangeFilter"/>: <c>aud.StructureChange</c> лежить на
/// <c>ps_AuditByMonth(ChangedAt)</c> з кластерним ключем <c>(ChangedAt, Id)</c>
/// (<c>11-audit-tables.sql</c>), тож саме вікно відсікає партиції й воно ж є
/// єдиним індексним доступом до таблиці.
/// </remarks>
/// <param name="From">Початок вікна в UTC, включно.</param>
/// <param name="To">Кінець вікна в UTC, виключно.</param>
/// <param name="EntityType">Тип сутності (<c>cfg.RegistryDef</c> тощо); <c>null</c> — усі.</param>
/// <param name="ChangedByUserId">Автор зміни — <b>UserId</b>, не SID (R-A2, D-86).</param>
public sealed record StructureChangeFilter(
    DateTime From,
    DateTime To,
    string? EntityType = null,
    int? ChangedByUserId = null);

/// <summary>Остання зміна однієї комірки — хто і коли (<c>BE-06</c>).</summary>
/// <remarks>
/// ⛔ Це ВІДПОВІДЬ НА ПИТАННЯ КОРИСТУВАЧА «чия правка і коли», а не рядок
/// журналу: значення комірки сюди не входить навмисно. Чинне значення живе в
/// <c>doc.CellValue</c>, і взяти його з <c>NewValue</c> аудиту означало б
/// показати те, що записали ОСТАННІМ разом у вікні, а не те, що в комірці
/// лежить зараз. Різниця видна рівно тоді, коли вона найдорожча: зміна поза
/// вікном або запис в обхід журналу.
/// </remarks>
/// <param name="ChangedAt">Момент зміни в UTC.</param>
/// <param name="ChangedByUserId">Автор — <b>UserId</b>, не SID (R-A2, D-86).</param>
/// <param name="ChangedByDisplayName">
/// Відображуване ім'я автора (<c>sec.User.DisplayName</c>); <c>null</c> —
/// запису користувача вже немає. ⛔ Саме <c>DisplayName</c>, не <c>UserName</c>:
/// логін і SID показувати людині заборонено (R-A2, D-86), а «чия це правка»
/// логіном не відповідається — у діалозі конфлікту має стояти ім'я.
/// </param>
/// <param name="Origin">
/// Походження: <c>UserEdit</c>, <c>Import</c>, <c>Recalculation</c>,
/// <c>Migration</c>. Усе, крім <c>UserEdit</c>, — не людина.
/// </param>
public sealed record LastCellChange(
    DateTime ChangedAt,
    int ChangedByUserId,
    string? ChangedByDisplayName,
    string Origin);

/// <summary>
/// Читання аудиту. Журнал **тільки читається**: методів зміни тут немає і не
/// буде — журнал, який можна відредагувати, не є доказом.
/// </summary>
public interface IAuditReader
{
    /// <summary>
    /// Історія змін комірок у вікні часу.
    /// </summary>
    /// <remarks>
    /// ⚠ Вікно <see cref="CellChangeFilter.From"/>…<see cref="CellChangeFilter.To"/>
    /// **обов'язкове**: <c>aud.CellChange</c> партиційована за <c>ChangedAt</c>,
    /// і запит без меж пішов би по всіх партиціях, включно з архівними. Місяць
    /// зміни і звітний період — різні осі: правка за січень може статися в березні.
    /// </remarks>
    /// <param name="filter">Вікно й звуження журналу.</param>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<PagedResult<CellChangeView>> ReadCellChangesAsync(
        CellChangeFilter filter, CursorRequest page, CancellationToken ct);

    /// <summary>
    /// Історія структурних змін однієї сутності конфігурації.
    /// </summary>
    /// <remarks>
    /// ⚠ Ключ пошуку — пара «тип + ідентифікатор», і типів передається
    /// <b>кілька</b>: історія довідника складається зі змін самого
    /// <c>cfg.RegistryDef</c> і його правил. Питати їх окремими запитами
    /// означало б зшивати два впорядкованих потоки в застосунку і
    /// перемішувати сторінки.
    ///
    /// ⚠ Вікно тут не обов'язкове, хоч <c>aud.StructureChange</c> і лежить на
    /// <c>ps_AuditByMonth</c>, як <c>aud.CellChange</c>: структурних змін
    /// одиниці на день. Обмежує обсяг <paramref name="limit"/>.
    /// </remarks>
    /// <param name="entityTypes">Типи сутностей; порожній набір — нічого.</param>
    /// <param name="entityId">Ідентифікатор сутності.</param>
    /// <param name="limit">Скільки останніх записів віддати.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<StructureChangeView>> ReadStructureChangesAsync(
        IReadOnlyList<string> entityTypes, int entityId, int limit, CancellationToken ct);

    /// <summary>
    /// Загальний журнал структурних змін у вікні часу (<c>BE-16</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Окремий метод, а не розширення <see cref="ReadStructureChangesAsync"/>:
    /// той відповідає на «історія ЦІЄЇ сутності» (останні N, без вікна), цей —
    /// на «що змінювали в системі за тиждень» (вікно + курсор). Злити їх
    /// означало б зробити вікно необов'язковим для обох.
    /// </remarks>
    /// <param name="filter">Вікно й звуження журналу.</param>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<PagedResult<StructureChangeView>> ReadStructureJournalAsync(
        StructureChangeFilter filter, CursorRequest page, CancellationToken ct);

    /// <summary>Кількість записів журналу за тим самим фільтром — для стелі експорту.</summary>
    /// <param name="filter">Вікно й звуження журналу.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<int> CountStructureJournalAsync(StructureChangeFilter filter, CancellationToken ct);

    /// <summary>
    /// Остання зміна кожної названої комірки — ОДНИМ запитом на весь перелік
    /// (<c>BE-06</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Вікно <paramref name="since"/> — ОБОВ'ЯЗКОВИЙ параметр, а не
    /// необов'язковий, і з тієї самої причини, що в
    /// <see cref="ReadCellChangesAsync"/>: <c>aud.CellChange</c> партиційована за
    /// <c>ChangedAt</c>, індекс <c>IX_CellChange_Cell</c> вирівняний по тій самій
    /// схемі, тож засічка за <c>(DocumentId, TableRowId, ColumnDefId)</c> без
    /// межі часу пробиває КОЖНУ партицію, включно з архівними. Адреса комірки
    /// партицію не звужує — її звужує лише час.
    ///
    /// ⚠ Комірок — перелік, а не одна: конфлікт паралельного редагування
    /// стосується батчу, і питати журнал по одній комірці означало б сотню
    /// походів у базу на одну відмову.
    ///
    /// ⚠ Комірка, яку в межах вікна ніхто не міняв, у результаті ВІДСУТНЯ.
    /// Порожнього <see cref="LastCellChange"/> тут немає навмисно: «змін не
    /// знайдено» і «змінив ніхто о нульовій даті» — різні твердження, і друге
    /// було б вигадкою.
    /// </remarks>
    /// <param name="documentId">Документ — перша колонка <c>IX_CellChange_Cell</c>.</param>
    /// <param name="cells">Адреси комірок у межах документа.</param>
    /// <param name="since">Початок вікна в UTC, включно.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyDictionary<(long TableRowId, int ColumnDefId), LastCellChange>> ReadLastChangesAsync(
        long documentId,
        IReadOnlyCollection<(long TableRowId, int ColumnDefId)> cells,
        DateTime since,
        CancellationToken ct);
}
