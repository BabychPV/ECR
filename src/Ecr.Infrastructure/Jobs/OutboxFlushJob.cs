// src/Ecr.Infrastructure/Jobs/OutboxFlushJob.cs
using Ecr.Application.Ports;
using Ecr.Application.Workflow;
using Ecr.Infrastructure.Integration;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// ✎ D-451: разове «відправити зараз» листів аркуша з черги <c>itg.NotificationOutbox</c> (<see cref="IOutboxFlushJob"/>).
/// </summary>
/// <remarks>
/// ⚠ Відправник — той самий <see cref="OutboxDispatcher"/>, що в щогодинного <c>NotificationJob</c>: захоплення
/// рядків атомарне (Q-241), тож одночасний прогін зведення лист не задублює. Пошта не налаштована —
/// <c>FlushAsync</c> нічого не робить, рядки чекають (як і в зведенні).
/// </remarks>
public sealed class OutboxFlushJob(OutboxDispatcher dispatcher) : IOutboxFlushJob
{
    /// <summary>Коди подій, які задача відправляє (лише листи аркуша, J1-01).</summary>
    public static readonly IReadOnlyList<string> EventCodes =
        [SheetWorkflowNotifier.SubmittedEventCode, SheetWorkflowNotifier.ReturnedEventCode];

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        foreach (var code in EventCodes)
        {
            await dispatcher.FlushAsync(code, ct).ConfigureAwait(false);
        }
    }
}
