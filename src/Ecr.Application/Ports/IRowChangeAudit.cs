using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>Що сталося з рядком таблиці документа (<c>aud.RowChange.Action</c>).</summary>
public enum RowChangeAction : byte
{
    /// <summary>Рядок додано (POST row, новий рядок PATCH або книги Excel).</summary>
    Added = 1,

    /// <summary>Рядок м'яко видалено (DELETE row).</summary>
    Deleted = 2,

    /// <summary>Зарезервовано: шляху відновлення рядка документа в продукті ще немає.</summary>
    Restored = 3,
}

/// <summary>Запис журналу рядків (<c>aud.RowChange</c>).</summary>
/// <param name="ChangedAt">Момент у UTC — партиційний ключ.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="PeriodKey">Звітний період екземпляра таблиці.</param>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="TableDefId">Таблиця версії шаблону — межі читання журналу.</param>
/// <param name="TableRowId">Рядок (<c>doc.TableRow.Id</c>).</param>
/// <param name="RowKey">Ключ рядка.</param>
/// <param name="Action">Дія.</param>
/// <param name="ChangedByUserId">Автор — <b>UserId</b>, не SID (R-A2).</param>
/// <param name="Origin">Походження за переліком <c>CellChangeOrigins</c> (<c>UserEdit</c>, <c>Import</c>…).</param>
/// <param name="CorrelationId">Наскрізний ідентифікатор запиту.</param>
public sealed record RowChangeRecord(
    DateTime ChangedAt,
    long DocumentId,
    int PeriodKey,
    long TableInstanceId,
    int TableDefId,
    long TableRowId,
    string RowKey,
    RowChangeAction Action,
    int ChangedByUserId,
    string Origin,
    string? CorrelationId);

/// <summary>Рядок журналу рядків, як його бачить читач.</summary>
/// <param name="Id">Ідентифікатор (другий складник курсора).</param>
/// <param name="ChangedAt">Момент у UTC.</param>
/// <param name="PeriodKey">Звітний період.</param>
/// <param name="RowKey">Ключ рядка.</param>
/// <param name="Action">Дія.</param>
/// <param name="ChangedByUserId">Автор.</param>
/// <param name="ChangedByDisplayName">Ім'я автора; <c>null</c> — запису користувача вже немає.</param>
/// <param name="Origin">Походження.</param>
/// <param name="TableCode">Код таблиці; <c>null</c> — таблиці вже немає.</param>
/// <param name="TableNameL10n">Назва таблиці.</param>
public sealed record RowChangeView(
    long Id,
    DateTime ChangedAt,
    int PeriodKey,
    string RowKey,
    RowChangeAction Action,
    int ChangedByUserId,
    string? ChangedByDisplayName,
    string Origin,
    string? TableCode,
    LocalizedText? TableNameL10n);

/// <summary>Фільтр журналу рядків одного документа за період, найновіші першими.</summary>
/// <param name="From">Початок вікна (UTC, включно) — відсікає партиції.</param>
/// <param name="To">Кінець вікна (UTC, виключно).</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="PeriodKey">Звітний період.</param>
/// <param name="VisibleTableIds">
/// ⛔ Межі читання: таблиці, які читач БАЧИТЬ; застосовуються в SQL до <c>TOP</c>. Порожній перелік — жодного рядка.
/// </param>
/// <param name="ChangedByUserId">Лише дії цього користувача; <c>null</c> — усіх.</param>
/// <param name="HumanOnly">Лише походження «людина» (<c>CellChangeOrigins.HumanOriginsSql</c>).</param>
public sealed record RowChangeFilter(
    DateTime From,
    DateTime To,
    long DocumentId,
    int PeriodKey,
    IReadOnlyList<int> VisibleTableIds,
    int? ChangedByUserId = null,
    bool HumanOnly = true);

/// <summary>
/// Журнал додавання й видалення рядків таблиць документа (<c>aud.RowChange</c>, <c>11-audit-tables.sql</c>).
/// </summary>
/// <remarks>
/// ⛔ Запис — результатна подія (C4, див. <see cref="IAuditWriter"/>): у ТІЙ САМІЙ транзакції, що й зміна рядка,
/// щоб відкат не лишав у журналі рядка, якого не було. Окремий порт, а не метод <see cref="IAuditWriter"/>: обгортки
/// того порту в тестах не мусять знати про новий журнал.
/// </remarks>
public interface IRowChangeAudit
{
    /// <summary>Записує зміни рядків одним походом, у поточній транзакції. Порожній перелік — нічого.</summary>
    /// <param name="changes">Записи.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task WriteRowChangesAsync(IReadOnlyList<RowChangeRecord> changes, CancellationToken ct);

    /// <summary>Сторінка журналу, найновіші першими, keyset «строго після» <paramref name="after"/>.</summary>
    /// <param name="filter">Фільтр (вже авторизований викликачем).</param>
    /// <param name="take">Скільки рядків прочитати.</param>
    /// <param name="after">Позиція <c>(At, Id)</c> останнього виданого рядка; <c>null</c> — початок.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RowChangeView>> ReadRowChangesAsync(
        RowChangeFilter filter, int take, (DateTime At, long Id)? after, CancellationToken ct);
}
