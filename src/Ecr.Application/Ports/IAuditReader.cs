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
    /// ⚠ Вікно <paramref name="from"/>…<paramref name="to"/> **обов'язкове**:
    /// <c>aud.CellChange</c> партиційована за <c>ChangedAt</c>, і запит без
    /// меж пішов би по всіх партиціях, включно з архівними. Місяць зміни і
    /// звітний період — різні осі: правка за січень може статися в березні.
    /// </remarks>
    /// <param name="from">Початок вікна в UTC, включно.</param>
    /// <param name="to">Кінець вікна в UTC, виключно.</param>
    /// <param name="documentId">Фільтр за документом; <c>null</c> — усі.</param>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<PagedResult<CellChangeView>> ReadCellChangesAsync(
        DateTime from, DateTime to, long? documentId, CursorRequest page, CancellationToken ct);

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
    /// ⚠ <c>aud.StructureChange</c> не партиційована за часом, на відміну від
    /// <c>aud.CellChange</c>: структурних змін одиниці на день, і вікно тут не
    /// обов'язкове. Обмежує обсяг <paramref name="limit"/>.
    /// </remarks>
    /// <param name="entityTypes">Типи сутностей; порожній набір — нічого.</param>
    /// <param name="entityId">Ідентифікатор сутності.</param>
    /// <param name="limit">Скільки останніх записів віддати.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<StructureChangeView>> ReadStructureChangesAsync(
        IReadOnlyList<string> entityTypes, int entityId, int limit, CancellationToken ct);
}
