using Ecr.Application.Ports;

namespace Ecr.Infrastructure.Jobs;

/// <summary>Побудова зрізів звітності у фоні.</summary>
/// <remarks>
/// Побудова зрізу читає багато і довго — тому у фон із прогресом, а не
/// синхронно у відповіді API.
/// </remarks>
public sealed class ReportSnapshotJob(IReportSnapshotBuilder builder) : IBackgroundJob
{
    /// <inheritdoc />
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: дістати reportVersionId, projectId, periodKey з payload; делегувати " +
            "builder.BuildAsync; повідомляти прогрес по кроках, а не одним стрибком 0 → 100.");
}
