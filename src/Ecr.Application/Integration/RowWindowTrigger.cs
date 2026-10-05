// src/Ecr.Application/Integration/RowWindowTrigger.cs
using Ecr.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Ecr.Application.Integration;

/// <summary>
/// Ставить підтягування вікон рядків, коли правка зачепила Початок, Кінець чи селектор (HSE301 A1, §4.4).
/// </summary>
/// <remarks>
/// ⛔ Гарячий шлях запису комірок: без зачепленої колонки вікна — нуль звернень до бази (знімок
/// колонок у <see cref="IRowWindowColumnIndex"/> живе в пам'яті), і лише збіг ставить задачу.
/// Постановка — <see cref="IBackgroundJobScheduler.EnqueueCoalescedAsync{TJob}"/> з ціллю
/// «екземпляр таблиці», а не <c>EnqueueExclusiveAsync</c>: сплеск правок (вставка з Excel) не переривати.
/// <para>
/// ⚠ Збій постановки НЕ валить запис комірок — людина вже зберегла дані, а вікно однаково доганяє
/// щогодинне <c>RowWindowRefetchJob</c>; але й не мовчить — журнал попереджень.
/// </para>
/// </remarks>
public sealed partial class RowWindowTrigger(
    IRowWindowColumnIndex index,
    IBackgroundJobScheduler jobs,
    ILogger<RowWindowTrigger> logger) : IRowWindowTrigger
{
    /// <inheritdoc />
    public async Task RowsChangedAsync(RowWindowChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);

        if (change.ChangedColumnDefIds.Count == 0)
        {
            return;
        }

        // ⛔ L6-11: індекс колонок — теж у try. Хук кличуть ПІСЛЯ коміту запису; збій
        // читання індексу (перше звернення до бази на холодному кеші) давав 500 на вже
        // збережених даних, і людина повторювала успішну правку.
        try
        {
            var windowColumns = await index.WindowColumnsAsync(change.TableDefId, ct).ConfigureAwait(false);
            if (windowColumns.Count == 0 || !change.ChangedColumnDefIds.Any(windowColumns.Contains))
            {
                return;
            }

            await jobs
                .EnqueueCoalescedAsync<IRowWindowFetchJob>(
                    RowWindowFetchTarget.Of(change.TableInstanceId),
                    new RowWindowFetchRequest(change.TableInstanceId, change.PeriodKey),
                    ct,
                    createdByUserId: null)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogEnqueueFailed(logger, change.TableInstanceId, ex.Message);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Підтягування вікон рядків екземпляра {TableInstanceId} не поставлено ({Reason}); дожене щогодинна задача.")]
    private static partial void LogEnqueueFailed(ILogger logger, long tableInstanceId, string reason);
}
