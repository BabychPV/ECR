// src/Ecr.Api/Observability/JobStartMetricsAdapter.cs
using Ecr.Application.Ports;

namespace Ecr.Api.Observability;

/// <summary>
/// Адаптер <see cref="IJobStartMetrics"/> над <see cref="EcrMetrics"/>
/// (<c>ФВ-12.2</c>).
/// </summary>
/// <remarks>
/// Той самий прийом, що <see cref="ConsistencyMetricsAdapter"/>: місток задач
/// живе в <c>Ecr.Infrastructure</c>, метрики — в <c>Ecr.Api</c>, і нижній шар
/// не бачить верхнього.
/// </remarks>
public sealed class JobStartMetricsAdapter(EcrMetrics metrics) : IJobStartMetrics
{
    /// <inheritdoc />
    public void RecordStartLatency(double milliseconds, string jobCode)
        => metrics.RecordJobStartLatency(milliseconds, jobCode);
}
