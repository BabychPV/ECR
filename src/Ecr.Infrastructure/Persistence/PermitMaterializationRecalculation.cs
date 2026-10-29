// src/Ecr.Infrastructure/Persistence/PermitMaterializationRecalculation.cs
using Ecr.Application.Documents;
using Ecr.Application.Permits.Bindings;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// PM-10b: постановка перерахунку формул після того, як відкриття документа створило рядки й заблоковані ліміти з дозволу.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Лише <see cref="IBackgroundJobScheduler.EnqueueCoalescedAsync{TJob}"/> (як «Оновити з дозволу» і перерахунок після
/// правки): <c>EnqueueExclusive</c> витісняв би виконувану задачу, а це автотригер. Ціль - документ × період без автора
/// (системна постановка, як в імпорті книги): злиття дописує насіння в задачу, що чекає.
/// </para>
/// <para>
/// ⚠ Ставиться ПІСЛЯ коміту створення екземплярів: черга в Quartz стартувала б до коміту й не побачила б рядків. Збій
/// постановки відкриття не ламає (рядки й ліміти вже збережено; похідні комірки оновляться при наступному перерахунку,
/// як було до PM-10b) - він журналюється.
/// </para>
/// </remarks>
internal static partial class PermitMaterializationRecalculation
{
    /// <summary>Ставить одну coalesced-задачу на документ, якщо дозвіл щось записав; інакше нічого не робить.</summary>
    public static async Task EnqueueAsync(
        IBackgroundJobScheduler? jobs,
        ILogger? log,
        long documentId,
        PeriodKey periodKey,
        PermitMaterialization? materialized,
        CancellationToken ct)
    {
        if (jobs is null || materialized is not { ChangedCells: { Count: > 0 } cells, EntryTableInstanceId: not 0 })
        {
            return;
        }

        try
        {
            await jobs
                .EnqueueCoalescedAsync<IFormulaRecalculationJob>(
                    FormulaRecalculationTarget.Of(documentId, periodKey.Value, createdByUserId: null),
                    new
                    {
                        DocumentId = documentId,
                        TableInstanceId = materialized.EntryTableInstanceId,
                        PeriodKey = periodKey.Value,
                        Cells = RecalculationSeed.Stamp(
                            cells.Select(c => new RecalculationSeed(c.RowId, c.ColumnId)),
                            periodKey.Value, materialized.EntryTableInstanceId),
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (log is not null)
            {
                LogEnqueueFailed(log, ex, documentId);
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Recalculation after permit rows of document {DocumentId} was not enqueued; dependants stay stale until the next recalculation.")]
    private static partial void LogEnqueueFailed(ILogger logger, Exception exception, long documentId);
}
