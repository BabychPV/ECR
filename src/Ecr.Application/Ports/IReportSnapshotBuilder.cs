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
}
