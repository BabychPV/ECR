// src/Ecr.Infrastructure/Jobs/QuartzDelayedJobRecovery.cs
using System.Reflection;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// ✎ D-582: на старті процесу ставить знову відкладені постановки Quartz (<see cref="QuartzDelayedJobStore"/>), які процес,
/// що зник, не встиг запустити: автоперерахунок після правки (<c>-auto</c>), ланки джерел, залежні періоди.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ ПІСЛЯ прибирання покинутих задач (<c>StartupSequence</c>, крок 5): саме воно закриває рядок-дзеркало <c>Queued</c>
/// процесу, що зник, і тим відрізняє його постановку від живої постановки сусіднього інстанса.
/// </para>
/// <para>
/// ⚠ Постановка — тим самим шляхом, що й до перезапуску (<see cref="IBackgroundJobScheduler.EnqueueCoalescedDelayedAsync{TJob}"/>
/// на ту саму ціль, решта вікна; минуле — одразу): злиття з будь-якою новою постановкою на ціль іде тими самими правилами
/// (<see cref="QuartzRecalculationMerge"/>), що й у пам'яті.
/// </para>
/// </remarks>
public static partial class QuartzDelayedJobRecovery
{
    /// <summary>Ставить знову забрані постановки.</summary>
    /// <param name="store">Сховище сліду.</param>
    /// <param name="scheduler">Планувальник цього процесу.</param>
    /// <param name="clock">Годинник застосунку.</param>
    /// <param name="logger">Журнал; <c>null</c> — без журналу.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Скільки постановок відновлено.</returns>
    public static async Task<int> RecoverAsync(
        QuartzDelayedJobStore store, IBackgroundJobScheduler scheduler, IClock clock, ILogger? logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(clock);

        var recovered = 0;
        foreach (var row in await store.ClaimOrphansAsync(ct).ConfigureAwait(false))
        {
            var target = TargetOf(row.JobId);
            var type = JobTypeOf(row.JobCode);
            if (target is null || type is null)
            {
                if (logger is not null)
                {
                    LogUnrecoverable(logger, row.JobId, row.JobCode);
                }

                continue;
            }

            try
            {
                var delay = row.DueAt - clock.UtcNow;
                using var payload = JsonDocument.Parse(row.Payload);
                var enqueue = EnqueueMethod.MakeGenericMethod(type);
                var task = (Task<string>)enqueue.Invoke(
                    scheduler,
                    [target, payload.RootElement.Clone(), delay > TimeSpan.Zero ? delay : TimeSpan.Zero, ct, row.CreatedByUserId])!;
                var jobId = await task.ConfigureAwait(false);
                recovered++;
                if (logger is not null)
                {
                    LogRecovered(logger, row.JobId, jobId);
                }
            }
#pragma warning disable CA1031 // Один невдалий рядок не зупиняє старт: він повертається і чекає наступного.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                if (logger is not null)
                {
                    LogRecoverFailed(logger, row.JobId, ex);
                }

                await store.RestoreAsync(row, clock.UtcNow, CancellationToken.None).ConfigureAwait(false);
            }
        }

        return recovered;
    }

    /// <summary>Ціль із ключа задачі <c>Тип~ціль~GUID</c>; <c>null</c> — ключ без цілі.</summary>
    /// <param name="jobId">Ключ задачі.</param>
    internal static string? TargetOf(string jobId)
    {
        if (QuartzJobScheduler.TargetPrefixOfJobId(jobId) is not { } prefix)
        {
            return null;
        }

        var first = prefix.IndexOf('~', StringComparison.Ordinal);
        return first > 0 && prefix.Length - first > 2 ? prefix[(first + 1)..^1] : null;
    }

    /// <summary>Тип задачі за кодом (повне ім'я маркера); <c>null</c> — невідомий.</summary>
    /// <param name="jobCode">Код.</param>
    internal static Type? JobTypeOf(string jobCode)
        => AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .Select(a => a.GetType(jobCode, throwOnError: false))
            .FirstOrDefault(t => t is not null && typeof(IBackgroundJob).IsAssignableFrom(t));

    private static readonly MethodInfo EnqueueMethod = typeof(IBackgroundJobScheduler)
        .GetMethod(nameof(IBackgroundJobScheduler.EnqueueCoalescedDelayedAsync))!;

    [LoggerMessage(Level = LogLevel.Information, Message = "Відкладену постановку {JobId}, загублену перезапуском, поставлено знову як {NewJobId}.")]
    private static partial void LogRecovered(ILogger logger, string jobId, string newJobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Відкладену постановку {JobId} ({JobCode}) не відновлено: невідома задача чи ключ без цілі.")]
    private static partial void LogUnrecoverable(ILogger logger, string jobId, string jobCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Відкладену постановку {JobId} не вдалося поставити знову; повтор — на наступному старті.")]
    private static partial void LogRecoverFailed(ILogger logger, string jobId, Exception ex);
}
