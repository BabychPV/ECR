// src/Ecr.Api/Observability/ConsistencyMetricsAdapter.cs
using Ecr.Application.Ports;

namespace Ecr.Api.Observability;

/// <summary>
/// Адаптер <see cref="IConsistencyMetrics"/> над <see cref="EcrMetrics"/>
/// (директива №11, T10 #41).
/// </summary>
/// <remarks>
/// ⛔ До цього класу <see cref="EcrMetrics.RecordConsistencyIssues"/> існував
/// і не мав ЖОДНОГО викликача: <c>ConsistencyCheckJob</c> пише знахідки в
/// <c>aud.ConsistencyIssue</c>, а той журнал ніхто не читає проактивно —
/// метрика мала бути вікном у знахідки без відкриття таблиці вручну, і цього
/// вікна не існувало.
/// </remarks>
public sealed class ConsistencyMetricsAdapter(EcrMetrics metrics) : IConsistencyMetrics
{
    /// <inheritdoc />
    public void RecordIssues(int count, string kind) => metrics.RecordConsistencyIssues(count, kind);
}
