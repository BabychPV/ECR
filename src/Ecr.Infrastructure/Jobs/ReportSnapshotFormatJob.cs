using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Разова класифікація старих зрізів: визначає формат суми (<c>current</c>/<c>legacy</c>)
/// для зрізів, у яких <c>rpt.ReportSnapshot.HashFormat</c> ще <c>NULL</c> (рішення 2026-09-21).
/// </summary>
/// <remarks>
/// ⛔ Визначення формату — те саме, що в звірці
/// (<see cref="VerifyReportSnapshotHandler.MatchedFormat"/>): перерахунок
/// <see cref="IReportSnapshotBuilder.VerifyAsync"/> і запис
/// <see cref="IReportSnapshotBuilder.RecordHashFormatAsync"/>. Кожен зріз — окреме
/// читання й однорядковий UPDATE в автокоміті: довгої транзакції на великому
/// зрізі немає. Зріз без суми не вибирається; вміст, що не збігся за жодним
/// форматом, лишається <c>NULL</c> і рахується в підсумку як <c>unmatched</c>.
/// <para>
/// Нові зрізи пишуться <c>current</c> при побудові, тож після першого проходу
/// нічний прогін бачить лише неспівпалі — порожній у здоровій базі.
/// </para>
/// </remarks>
public sealed class ReportSnapshotFormatJob(
    EcrDbContext db, IReportSnapshotBuilder snapshots, IClock clock) : IBackgroundJob
{
    /// <summary>Код задачі в журналі обслуговування.</summary>
    public static string Code => "report-snapshot-format";

    /// <summary>Скільки ідентифікаторів зрізів вибирає один батч.</summary>
    private const int BatchSize = 100;

    /// <summary>Стеля батчів одного прогону: решту доробить наступна ніч.</summary>
    private const int MaxBatchesPerRun = 500;

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var run = new MaintenanceRun(Code, clock.UtcNow);
        db.MaintenanceRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        try
        {
            await RunAsync(run, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await MaintenanceRunFailure.RecordAsync(db, run, ex, clock.UtcNow).ConfigureAwait(false);
            throw;
        }
    }

    private async Task RunAsync(MaintenanceRun run, IJobProgress progress, CancellationToken ct)
    {
        int current = 0, legacy = 0, unmatched = 0, batches = 0;
        long after = 0;

        while (batches < MaxBatchesPerRun)
        {
            var ids = await db.ReportSnapshots
                .AsNoTracking()
                .Where(s => s.Id > after && s.HashFormat == null && s.ContentHash != null)
                .OrderBy(s => s.Id)
                .Select(s => s.Id)
                .Take(BatchSize)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (ids.Count == 0)
            {
                break;
            }

            foreach (var id in ids)
            {
                var hashes = await snapshots.VerifyAsync(id, ct).ConfigureAwait(false);
                var format = hashes is null ? null : VerifyReportSnapshotHandler.MatchedFormat(hashes);

                if (format is null)
                {
                    unmatched++;
                    continue;
                }

                // `false` — формат тим часом записала звірка; рахувати нема чого.
                if (!await snapshots.RecordHashFormatAsync(id, format, ct).ConfigureAwait(false))
                {
                    continue;
                }

                if (format == VerifyReportSnapshotHandler.FormatLegacy)
                {
                    legacy++;
                }
                else
                {
                    current++;
                }
            }

            after = ids[^1];
            batches++;

            await progress.ReportAsync(Math.Min(99, batches * 100 / MaxBatchesPerRun), null, ct)
                .ConfigureAwait(false);

            if (ids.Count < BatchSize)
            {
                break;
            }
        }

        run.Complete(
            "Succeeded",
            $$"""{"current":{{current}},"legacy":{{legacy}},"unmatched":{{unmatched}},"batches":{{batches}}}""",
            clock.UtcNow);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await progress.ReportAsync(100, null, ct).ConfigureAwait(false);
    }
}
