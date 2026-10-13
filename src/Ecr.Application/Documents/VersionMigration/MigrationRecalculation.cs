using System.Globalization;
using Ecr.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Ecr.Application.Documents.VersionMigration;

/// <summary>
/// LP-41: перерахунок проєкту після переносу на версію, що додала або змінила формули.
/// </summary>
/// <remarks>
/// ⛔ Викликається ПІСЛЯ коміту переносу: задача не мусить бачити вікно структури (409), а виняток тут дав би
/// клієнтові 500 при вже комітнутому переносі — тому збій лише в журнал (кнопка «Перерахувати» лишається).
/// <para>
/// ⚠ Саме <see cref="IRecalculationJob"/> на проєкт (<c>PeriodKey = null</c>, <c>DocumentId</c> відсутній →
/// розклад на документи, <c>RecalculationJob.FanOutAsync</c>), а не <c>RunCalculationHandler</c>: той кидає
/// відмову, коли серед періодів є закритий. Закриті й подані періоди відсіює сама задача
/// (<c>ScopePeriodsAsync</c>, ФВ-9.7/9.17) — їхні нові колонки лишаються порожніми, що правильно.
/// </para>
/// <para>
/// ⚠ <c>EnqueueCoalescedAsync</c>, не <c>EnqueueExclusiveAsync</c>: витіснення перервало б перерахунок, що
/// вже йде (нічний, ручний). Повторний перенос, поки задача в черзі, зливається. Автор — <c>null</c>: системна
/// задача (як <c>CalculationTrigger</c>), людина-ініціатор лишається в аудиті переносу.
/// </para>
/// </remarks>
public static partial class MigrationRecalculation
{
    /// <summary>Ціль злиття перерахунку проєкту після переносу (рівень проєкту, не документа).</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <returns>Ключ цілі.</returns>
    public static string TargetOf(int projectId)
        => string.Create(CultureInfo.InvariantCulture, $"project{projectId}-migrate-recalc");

    /// <summary>Ставить один перерахунок проєкту; ніколи не кидає.</summary>
    /// <param name="jobs">Планувальник.</param>
    /// <param name="log">Журнал.</param>
    /// <param name="requestedAt">Момент запиту.</param>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="formulaTables">Скільки таблиць додали/змінили формули (для журналу).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ідентифікатор задачі; <c>null</c> — не поставлено (збій).</returns>
    public static async Task<string?> EnqueueAsync(
        IBackgroundJobScheduler jobs, ILogger log, DateTime requestedAt, int projectId, int formulaTables,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            var jobId = await jobs
                .EnqueueCoalescedAsync<IRecalculationJob>(
                    TargetOf(projectId),
                    new
                    {
                        projectId,
                        periodKey = (int?)null,
                        triggeredByUserId = (int?)null,
                        requestedAt,
                    },
                    ct,
                    createdByUserId: null)
                .ConfigureAwait(false);
            LogQueued(log, projectId, formulaTables, jobId);

            return jobId;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            LogFailed(log, ex, projectId);

            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Перенос версії проєкту {ProjectId}: формули додано/змінено в таблицях: {Tables}; поставлено перерахунок {JobId}.")]
    private static partial void LogQueued(ILogger log, int projectId, int tables, string jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Перенос версії проєкту {ProjectId}: перерахунок після переносу не поставлено; потрібне ручне «Перерахувати».")]
    private static partial void LogFailed(ILogger log, Exception ex, int projectId);
}
