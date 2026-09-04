using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Стежить за запасом порожніх партицій попереду.
/// </summary>
/// <remarks>
/// ⚠ Працює **на випередження**. <c>SPLIT</c> порожньої останньої партиції —
/// операція метаданих; <c>SPLIT</c> непорожньої переміщує дані з блокуванням.
/// Дізнатися про вичерпаний запас треба на старті або за розкладом, а не
/// вночі під час архівації.
/// </remarks>
public sealed class PartitionCheckJob(EcrDbContext db, ISqlCapabilities capabilities) : IBackgroundJob
{
    /// <inheritdoc />
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: порахувати, скільки меж pf_ByPeriodKey лишилося попереду поточного періоду; " +
            "менше двох → Degraded і алерт; викликати arc.usp_EnsurePartitions (@MonthsAhead = 6). " +
            "⚠ Саму процедуру виконує SQL Agent під окремим principal (D-66) — застосунок " +
            "DDL-прав не має і SPLIT робити не може.");
}
