// src/Ecr.Infrastructure/Jobs/RowWindowRefetchJob.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Щогодинний повтор підтягування вікон рядків (HSE301 A1, §4.4): пізні дані PI та вікна, що на момент читання
/// ще тривали.
/// </summary>
/// <remarks>
/// ⚠ Сама нічого не читає з PI: знаходить екземпляри таблиць з чинними записами <c>NoData</c>/<c>Partial</c>/
/// <c>SourceError</c> у межах <c>RefetchWithinDays</c> прив'язки (від кінця вікна) і записами, де
/// <c>ToUtc &gt; RetrievedAt</c>, — і ставить на кожен <see cref="IRowWindowFetchJob"/> (злиття за екземпляром).
/// Що саме підтягувати, вирішує та задача тим самим правилом (<c>RowWindowFetch.NeedsFetch</c>).
/// </remarks>
public sealed class RowWindowRefetchJob(EcrDbContext db, IBackgroundJobScheduler jobs, IClock clock) : IBackgroundJob
{
    /// <summary>Код задачі в розкладі.</summary>
    public static string Code => "row-window-refetch";

    /// <summary>Стеля екземплярів за прогін: решта — наступної години.</summary>
    public const int MaxInstances = 1_000;

    private static readonly RowWindowValueStatus[] RetryStatuses =
    [
        RowWindowValueStatus.NoData,
        RowWindowValueStatus.Partial,
        RowWindowValueStatus.SourceError,
    ];

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var now = clock.UtcNow;

        var instances = await (
                from v in db.RowWindowValues.AsNoTracking()
                join m in db.RowWindowMaps.AsNoTracking() on v.RowWindowMapId equals m.Id
                where v.IsCurrent && m.IsActive
                      && ((RetryStatuses.Contains(v.Status) && v.ToUtc.AddDays(m.RefetchWithinDays) >= now)
                          || v.ToUtc > v.RetrievedAt)
                select new { v.TableInstanceId, v.PeriodKey })
            .Distinct()
            .OrderBy(i => i.TableInstanceId)
            .Take(MaxInstances)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var instance in instances)
        {
            await jobs
                .EnqueueCoalescedAsync<IRowWindowFetchJob>(
                    RowWindowFetchTarget.Of(instance.TableInstanceId),
                    new RowWindowFetchRequest(instance.TableInstanceId, instance.PeriodKey),
                    ct,
                    createdByUserId: null)
                .ConfigureAwait(false);
        }

        await progress
            .ReportKeyAsync(
                100,
                "jobs.rowWindowRefetchDone",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["instances"] = instances.Count.ToString(CultureInfo.InvariantCulture),
                },
                ct)
            .ConfigureAwait(false);
    }
}
