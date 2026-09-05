using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Побудова зрізу звітності. <c>rpt.*</c> — **зріз без логіки**: агрегації
/// робить сервіс тут, вʼюха лише проєктує (ФВ-0.3).
/// </summary>
public interface IReportSnapshotBuilder
{
    /// <summary>
    /// Будує зріз. Статус успадковується від даних: <c>Draft</c>, поки аркуші
    /// не затверджені (D-65) — регуляторні вʼюхи такий зріз не віддають.
    /// </summary>
    public Task<long> BuildAsync(int reportVersionId, int projectId, PeriodKey? periodKey,
                          string? parametersJson, CancellationToken ct);

    /// <summary>Позначає зріз поданим — після цього він іммутабельний.</summary>
    public Task MarkSubmittedAsync(long snapshotId, int userId, CancellationToken ct);

    /// <summary>Перераховує статус зрізу після зміни стану затвердження аркушів.</summary>
    public Task<SnapshotStatus> RefreshStatusAsync(long snapshotId, CancellationToken ct);

    /// <summary>Перелік побудованих зрізів.</summary>
    /// <param name="projectId">Проєкт; <c>null</c> — усі.</param>
    /// <param name="periodKey">Період; <c>null</c> — усі.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ У перелік входять час побудови й контрольна сума. Споживач зрізу —
    /// SSRS і людина, яка звіряє звіт, — має бачити, <b>на яких даних</b> його
    /// побудовано: два зрізи однієї версії за один період відрізняються лише
    /// цим, і без обох полів вибрати правильний неможливо.
    /// </remarks>
    public Task<IReadOnlyList<ReportSnapshotSummary>> ListAsync(
        int? projectId, int? periodKey, CancellationToken ct);
}

/// <summary>Зріз у переліку.</summary>
/// <param name="Id">Ідентифікатор зрізу.</param>
/// <param name="ReportVersionId">Версія звіту.</param>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="PeriodKey">Період; <c>null</c> — увесь рік проєкту.</param>
/// <param name="Status">Статус даних зрізу (D-65).</param>
/// <param name="IsCurrent">Чи це поточний зріз для пари «версія × проєкт × період».</param>
/// <param name="RowCount">Скільки рядків.</param>
/// <param name="ContentHash">Контрольна сума вмісту в hex; <c>null</c> — не рахувалася.</param>
/// <param name="BuiltAt">Коли побудовано.</param>
public sealed record ReportSnapshotSummary(
    long Id,
    int ReportVersionId,
    int ProjectId,
    int? PeriodKey,
    string Status,
    bool IsCurrent,
    int RowCount,
    string? ContentHash,
    DateTime BuiltAt);
