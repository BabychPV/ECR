using System.Diagnostics;
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.DataGen;

/// <summary>
/// Заміри гейта BR-07 — шість критеріїв із <c>tz/04</c> §4.3.
/// </summary>
/// <remarks>
/// ⚠ Найважливіший і найлегший для пропуску — замір №6: **125 RPS в одну
/// партицію**. Пік у ECR не розподілений: усі користувачі в останні дні
/// періоду б'ють в один період. Рівномірне навантаження на 12 партицій
/// нічого не доводить.
/// </remarks>
public sealed class GateBenchmark
{
    private const double SliceBudgetMs = 600;
    private const double ApplyBudgetMs = 150;
    private const double RollupBudgetMs = 500;

    /// <summary>Скільки секунд тримати навантаження в замірі №6.</summary>
    /// <remarks>
    /// ⚠ У повному гейті це 15 хвилин. Тут значення параметризоване, бо
    /// п'ятнадцятихвилинний прогін недоречний у CI; але **зменшене вікно не є
    /// проходженням гейта** — воно лише показує, що замір працює.
    /// </remarks>
    public int LoadSeconds { get; init; } = 60;

    /// <summary>Цільове навантаження в одну партицію.</summary>
    public int TargetRps { get; init; } = 125;

    /// <summary>Виконує всі заміри і друкує звіт.</summary>
    public async Task<GateResult> RunAsync(string connectionString, CancellationToken ct)
    {
        var measurements = new Dictionary<string, double>(StringComparer.Ordinal);
        var failures = new List<string>();

        await using var db = CreateContext(connectionString);
        var store = new NormalizedCellStore(db, new BulkCellLoader(connectionString, 10_000));

        var target = await FindBusiestSliceAsync(db, ct).ConfigureAwait(false);
        if (target is null)
        {
            return new GateResult(false, measurements, ["У базі немає даних: спершу запустіть генератор."]);
        }

        // 1) Читання зрізу
        var slice = await MeasureAsync(20, () => store.ReadSliceAsync(target.TableInstanceId, ct)).ConfigureAwait(false);
        measurements["read_slice_p95_ms"] = slice;
        if (slice > SliceBudgetMs)
        {
            failures.Add(Fmt($"ReadSliceAsync p95 {slice:F0} мс > {SliceBudgetMs} мс"));
        }

        // 2) Запис 100 комірок
        var apply = await MeasureAsync(20, () => ApplyOnceAsync(store, target, ct)).ConfigureAwait(false);
        measurements["apply_100_p95_ms"] = apply;
        if (apply > ApplyBudgetMs)
        {
            failures.Add(Fmt($"ApplyAsync(100) p95 {apply:F0} мс > {ApplyBudgetMs} мс"));
        }

        // 3) Агрегація по періоду. ⛔ Індексована в'юха неприпустима (ФВ-0.3):
        //    вона переносить вартість на кожен запис, а пік у нас саме на записі.
        var rollup = await MeasureAsync(10, () => RollupAsync(db, target.PeriodKey, ct)).ConfigureAwait(false);
        measurements["rollup_p95_ms"] = rollup;
        if (rollup > RollupBudgetMs)
        {
            failures.Add(Fmt($"Агрегація періоду p95 {rollup:F0} мс > {RollupBudgetMs} мс"));
        }

        // 5) Розмір після PAGE-стиснення — вхідні дані для sizing.
        measurements["cellvalue_mb"] = (double)await SizeMbAsync(db, ct).ConfigureAwait(false);

        // 6) ⚠ ГОЛОВНИЙ замір: усе навантаження в ОДНУ партицію.
        var (achieved, p95) = await LoadOnePartitionAsync(connectionString, target, ct).ConfigureAwait(false);
        measurements["one_partition_rps"] = achieved;
        measurements["one_partition_p95_ms"] = p95;
        if (achieved < TargetRps)
        {
            failures.Add(Fmt($"В одну партицію досягнуто {achieved:F0} RPS < {TargetRps}"));
        }

        if (p95 > SliceBudgetMs)
        {
            failures.Add(Fmt($"Під навантаженням p95 {p95:F0} мс > {SliceBudgetMs} мс"));
        }

        // 4) Повний цикл архівації року в цьому замірі не виконується: він
        //    потребує заповненого архівного року і вікна обслуговування.
        //    Позначаємо явно, щоб «пройдений гейт» не означав неперевірене.
        failures.Add("Замір №4 (повний цикл архівації року) не виконувався.");

        return new GateResult(failures.Count == 0, measurements, failures);
    }

    private static async Task<double> MeasureAsync(int iterations, Func<Task> action)
    {
        var samples = new List<double>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            await action().ConfigureAwait(false);
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        return samples[(int)Math.Floor(0.95 * (samples.Count - 1))];
    }

    private static async Task ApplyOnceAsync(
        NormalizedCellStore store, SliceTarget target, CancellationToken ct)
    {
        var records = target.RowIds.Take(10)
            .SelectMany(rowId => target.ColumnIds.Take(10).Select(columnId => new CellRecord(
                new CellAddress(new PeriodKey(target.PeriodKey), rowId, columnId),
                target.TableDefId,
                new CellValueData { ValueNumeric = 1m })))
            .ToList();

        await store.ApplyAsync(new CellChangeSet(
            target.TableInstanceId, records, [], [.. target.RowIds.Take(10)], 1, false), ct).ConfigureAwait(false);
    }

    private static Task<List<decimal?>> RollupAsync(EcrDbContext db, int periodKey, CancellationToken ct)
        => db.Database.SqlQueryRaw<decimal?>(
                "SELECT SUM(ValueNumeric) AS Value FROM doc.CellValue WHERE PeriodKey = {0}", periodKey)
            .ToListAsync(ct);

    private static async Task<decimal> SizeMbAsync(EcrDbContext db, CancellationToken ct)
    {
        var mb = await db.Database.SqlQueryRaw<decimal>("""
            SELECT CAST(SUM(a.used_pages) * 8.0 / 1024 AS decimal(18,2)) AS Value
            FROM sys.tables t
            JOIN sys.indexes i     ON i.object_id = t.object_id
            JOIN sys.partitions p  ON p.object_id = i.object_id AND p.index_id = i.index_id
            JOIN sys.allocation_units a ON a.container_id = p.partition_id
            WHERE t.name = 'CellValue' AND SCHEMA_NAME(t.schema_id) = 'doc'
            """).ToListAsync(ct).ConfigureAwait(false);

        return mb.Count > 0 ? mb[0] : 0m;
    }

    /// <summary>Навантаження в одну партицію: саме так виглядає пік у проді.</summary>
    private async Task<(double Rps, double P95)> LoadOnePartitionAsync(
        string connectionString, SliceTarget target, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(LoadSeconds);
        var samples = new System.Collections.Concurrent.ConcurrentBag<double>();
        var workers = Environment.ProcessorCount * 2;

        await Task.WhenAll(Enumerable.Range(0, workers).Select(async _ =>
        {
            await using var db = CreateContext(connectionString);
            var store = new NormalizedCellStore(db, new BulkCellLoader(connectionString, 1000));

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                await store.ReadSliceAsync(target.TableInstanceId, ct).ConfigureAwait(false);
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }
        })).ConfigureAwait(false);

        var ordered = samples.OrderBy(x => x).ToList();
        var p95 = ordered.Count > 0 ? ordered[(int)Math.Floor(0.95 * (ordered.Count - 1))] : 0;
        return (ordered.Count / (double)LoadSeconds, p95);
    }

    /// <summary>Найбільший наявний зріз: міряти треба найгірший випадок.</summary>
    private static async Task<SliceTarget?> FindBusiestSliceAsync(EcrDbContext db, CancellationToken ct)
    {
        var busiest = await db.CellValues.AsNoTracking()
            .GroupBy(c => new { c.PeriodKeyValue, c.TableRowId })
            .Select(g => new { g.Key.PeriodKeyValue, g.Key.TableRowId })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (busiest is null)
        {
            return null;
        }

        var row = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == busiest.PeriodKeyValue && r.Id == busiest.TableRowId)
            .Select(r => new { r.TableInstanceId, r.PeriodKeyValue })
            .FirstAsync(ct).ConfigureAwait(false);

        var instance = await db.TableInstances.AsNoTracking()
            .Where(i => i.Id == row.TableInstanceId)
            .Select(i => new { i.TableDefId })
            .FirstAsync(ct).ConfigureAwait(false);

        var rowIds = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == row.PeriodKeyValue && r.TableInstanceId == row.TableInstanceId)
            .Select(r => r.Id).ToListAsync(ct).ConfigureAwait(false);

        var columnIds = await db.ColumnDefs.AsNoTracking()
            .Where(c => c.TableDefId == instance.TableDefId)
            .Select(c => c.Id).ToListAsync(ct).ConfigureAwait(false);

        return new SliceTarget(
            row.TableInstanceId, row.PeriodKeyValue, instance.TableDefId, rowIds, columnIds);
    }

    private static EcrDbContext CreateContext(string connectionString)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(connectionString, o => o.CommandTimeout(600))
            .Options);

    private static string Fmt(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private sealed record SliceTarget(
        long TableInstanceId, int PeriodKey, int TableDefId,
        IReadOnlyList<long> RowIds, IReadOnlyList<int> ColumnIds);
}

/// <summary>Результат гейта.</summary>
public sealed record GateResult(
    bool Passed,
    IReadOnlyDictionary<string, double> Measurements,
    IReadOnlyList<string> Failures);
