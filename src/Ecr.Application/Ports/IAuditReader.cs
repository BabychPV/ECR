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
}
