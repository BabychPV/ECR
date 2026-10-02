namespace Ecr.Infrastructure.Observability;

/// <summary>Одна точка gauge <c>ecr.jobs.queue_depth</c>: скільки задач у лейні в стані.</summary>
/// <param name="Lane">Лейн черги (<c>JobLanes.All</c>).</param>
/// <param name="State"><c>Queued</c> або <c>Running</c>.</param>
/// <param name="Count">Кількість задач.</param>
public sealed record QueueDepthPoint(string Lane, string State, long Count);
