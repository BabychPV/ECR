// src/Ecr.Infrastructure/Jobs/SupersededResultsSweepJob.cs
using System.Data;
using System.Data.Common;
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Нічний прохід і разове прибирання: видаляє прогони, що підлягають прибиранню (<c>Superseded</c>; <c>Failed</c>,
/// старіший за актуальний свого ланцюга), — результати, кроки, входи й сам рядок <c>calc.CalculationRun</c> (Q-17;
/// <c>D-560</c> / PSS-01, рішення людини 20.11: для «документ × період» лишається лише останній ланцюг).
/// </summary>
/// <remarks>
/// Страховка швидкого шляху (<c>RunCalculationHandler.CompleteAsync</c> → <c>PurgeSupersededRunsAsync</c>) і двигун
/// разового прибирання наявної бази. Модель — <see cref="SubmissionSnapshotRetentionJob"/>: журнал
/// <c>MaintenanceRun</c> (лічильники за таблицями, прогонів, пакетів, тривалість), стеля пакетів за прохід.
/// <para>
/// ✎ <c>D-560</c>: увімкнено й видаляє за замовчуванням; <see cref="SupersededPurgeOptions.DeleteEnabled"/> =
/// <c>false</c> лишає прохід лічильником (dry-run). Невідомий статус (поза <c>Current, Running, Superseded,
/// Failed</c>) зупиняє прохід у БУДЬ-ЯКОМУ режимі: рішення про нього - за людиною, а не за виразом <c>NOT IN</c>.
/// </para>
/// <para>
/// ⚠ Відновлюваність і ідемпотентність - у самих даних: прогрес = рядки, що лишилися; повторний прохід знаходить
/// менше кандидатів. Кожен пакет - окрема коротка транзакція (<see cref="SupersededResultsPurger"/>). SIMPLE
/// recovery: <c>CHECKPOINT</c> кожні N пакетів; FULL: пауза, поки журнал заповнений вище порога.
/// </para>
/// </remarks>
public sealed partial class SupersededResultsSweepJob(
    EcrDbContext db,
    IClock clock,
    IConfiguration configuration,
    ILogger<SupersededResultsSweepJob> logger,
    Func<CancellationToken, Task<double?>>? logUsedPercent = null)
    : IBackgroundJob
{
    /// <summary>Код задачі в журналі обслуговування.</summary>
    public static string Code => "superseded-results-sweep";

    /// <summary>Статуси прогонів, яким задача знає раду. Усе інше - стоп.</summary>
    public static readonly IReadOnlyList<string> KnownStatuses = ["Current", "Running", "Superseded", "Failed"];

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var run = new MaintenanceRun(Code, clock.UtcNow);
        db.MaintenanceRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        try
        {
            var options = SupersededPurgeOptions.Read(configuration);
            SupersededSweepOutcome outcome;
            if (options.Enabled)
            {
                outcome = await RunAsync(options, progress, ct).ConfigureAwait(false);
            }
            else
            {
                LogDisabled(logger, SupersededPurgeOptions.EnabledKey);
                outcome = new SupersededSweepOutcome(SupersededSweepOutcome.DisabledMode);
            }

            run.Complete(outcome.Aborted ? "Degraded" : "Succeeded", outcome.ToJson(), clock.UtcNow);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await progress.ReportAsync(100, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await MaintenanceRunFailure.RecordAsync(db, run, ex, clock.UtcNow).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Один прохід: перевірки, dry-run (лічить) або видалення пакетами. Публічний для тестів і ручного запуску.</summary>
    /// <param name="options">Налаштування.</param>
    /// <param name="progress">Канал прогресу; <c>null</c> - без звіту.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<SupersededSweepOutcome> RunAsync(
        SupersededPurgeOptions options, IJobProgress? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        var purger = new SupersededResultsPurger(db, options, clock, logger);
        await using var scope = await SupersededResultsPurger.ConnectionScope.OpenAsync(db, ct).ConfigureAwait(false);

        var histogram = await ReadStatusHistogramAsync(purger, ct).ConfigureAwait(false);
        var unknown = histogram.Keys.Where(s => !KnownStatuses.Contains(s, StringComparer.Ordinal)).Order().ToList();
        if (unknown.Count > 0)
        {
            var unknownList = string.Join(", ", unknown);
            LogUnknownStatuses(logger, unknownList);
            return new SupersededSweepOutcome(SupersededSweepOutcome.AbortedMode)
            {
                StatusHistogram = histogram,
                AbortReason = "unknownStatus: " + string.Join(", ", unknown),
            };
        }

        var recovery = await ReadRecoveryModelAsync(purger, ct).ConfigureAwait(false);

        // ✎ D-560: основна форма — за прогоном (рядки + сам рядок прогону) по індексах з провідним CalculationRunId.
        // Без них (база без повторного 07-partition-tables.sql) — запасна keyset-форма за партиціями, лише рядки.
        SupersededSweepOutcome outcome;
        if (await purger.RunIndexesPresentAsync(ct).ConfigureAwait(false))
        {
            var runs = await purger.ListEligibleRunsAsync(ct).ConfigureAwait(false);
            outcome = options.DeleteEnabled
                ? await DeleteRunsAsync(purger, options, runs, progress, ct).ConfigureAwait(false)
                : await CountRunsAsync(purger, runs, ct).ConfigureAwait(false);
        }
        else
        {
            SupersededResultsPurger.LogRunIndexesMissing(logger);
            var periods = await purger.ListEligiblePeriodsAsync(ct).ConfigureAwait(false);
            outcome = options.DeleteEnabled
                ? await DeleteAsync(purger, options, periods, progress, ct).ConfigureAwait(false)
                : await CountAsync(purger, periods, ct).ConfigureAwait(false);
            outcome = outcome with { RunIndexes = false };
        }

        return outcome with { StatusHistogram = histogram, RecoveryModel = recovery };
    }

    /// <summary>Dry-run основної форми: скільки рядків і прогонів було б видалено.</summary>
    private async Task<SupersededSweepOutcome> CountRunsAsync(
        SupersededResultsPurger purger, List<SupersededResultsPurger.EligibleRun> runs, CancellationToken ct)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in purger.Tables)
        {
            counts[table] = runs.Count == 0 ? 0 : await purger.CountEligibleRowsAsync(table, ct).ConfigureAwait(false);
        }

        if (purger.DeletesRunRows)
        {
            counts[SupersededResultsPurger.RunTable] = runs.Count;
        }

        var summary = string.Join("; ", counts.Select(c => $"{c.Key}={c.Value}"));
        LogDryRun(logger, summary);
        return new SupersededSweepOutcome(SupersededSweepOutcome.DryRunMode)
        {
            Candidates = counts,
            Periods = runs.Select(r => r.PeriodKey).Distinct().Count(),
            RunCandidates = runs.Count,
            Complete = false,
        };
    }

    /// <summary>
    /// Видалення основної формою: прогін за прогоном від найстаршого — рядки пакетами, далі рядок прогону. Стеля пакетів
    /// і журнал можуть зупинити посередині прогону; наступний прохід продовжить (прогрес — самі дані).
    /// </summary>
    private async Task<SupersededSweepOutcome> DeleteRunsAsync(
        SupersededResultsPurger purger, SupersededPurgeOptions options, List<SupersededResultsPurger.EligibleRun> runs,
        IJobProgress? progress, CancellationToken ct)
    {
        var budget = new SupersededResultsPurger.Budget(options.MaxBatchesPerRun);
        var deleted = new Dictionary<string, long>(StringComparer.Ordinal);
        var betweenBatches = await BetweenBatchesAsync(purger, options, budget, ct).ConfigureAwait(false);
        var touched = new SortedSet<int>();
        var finishedAll = true;
        var started = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < runs.Count; i++)
        {
            if (!budget.CanContinue)
            {
                finishedAll = false;
                break;
            }

            var run = runs[i];
            var purge = await purger.PurgeRunAsync(run.RunId, budget, betweenBatches, ct).ConfigureAwait(false);
            foreach (var (table, count) in purge.Deleted)
            {
                deleted[table] = deleted.GetValueOrDefault(table) + count;
            }

            if (purge.RunDeleted)
            {
                deleted[SupersededResultsPurger.RunTable] = deleted.GetValueOrDefault(SupersededResultsPurger.RunTable) + 1;
            }

            if (purge.Rows > 0)
            {
                touched.Add(run.PeriodKey);
            }

            if (!purge.Complete)
            {
                finishedAll = false;
                break;
            }

            if (purge.Rows > 0 || purge.RunDeleted)
            {
                SupersededResultsPurger.LogRunPurged(logger, run.RunId, purge.Rows, purge.RunDeleted);
            }

            if (progress is not null && (i % 20 == 19 || i == runs.Count - 1))
            {
                await progress.ReportAsync(Math.Min(99, (i + 1) * 100 / Math.Max(1, runs.Count)), null, ct)
                    .ConfigureAwait(false);
            }
        }

        var outcome = new SupersededSweepOutcome(SupersededSweepOutcome.DeleteMode)
        {
            Deleted = deleted,
            Periods = runs.Select(r => r.PeriodKey).Distinct().Count(),
            RunCandidates = runs.Count,
            Batches = budget.Done,
            Complete = finishedAll,
            StopReason = budget.StopReason ?? (finishedAll ? null : "maxBatches"),
            ElapsedSeconds = (long)started.Elapsed.TotalSeconds,
        };

        var deletedSummary = string.Join("; ", deleted.Select(c => $"{c.Key}={c.Value}"));
        LogDeleted(logger, deletedSummary, budget.Done, finishedAll);

        if (finishedAll && options.RebuildAfter && touched.Count > 0)
        {
            await RebuildAsync(purger, touched, ct).ConfigureAwait(false);
            outcome = outcome with { Rebuilt = true };
        }

        return outcome;
    }

    /// <summary>Перевірка між пакетами: SIMPLE — CHECKPOINT кожні N пакетів; FULL — пауза, поки журнал заповнений.</summary>
    private async Task<Func<CancellationToken, Task<bool>>> BetweenBatchesAsync(
        SupersededResultsPurger purger, SupersededPurgeOptions options, SupersededResultsPurger.Budget budget,
        CancellationToken ct)
    {
        var recovery = await ReadRecoveryModelAsync(purger, ct).ConfigureAwait(false);
        var simple = string.Equals(recovery, "SIMPLE", StringComparison.OrdinalIgnoreCase);
        var full = string.Equals(recovery, "FULL", StringComparison.OrdinalIgnoreCase);
        var sinceCheckpoint = 0;

        return async token =>
        {
            if (simple && ++sinceCheckpoint >= options.CheckpointEveryBatches)
            {
                sinceCheckpoint = 0;
                await CheckpointAsync(purger, token).ConfigureAwait(false);
            }

            return !full || await WaitForLogAsync(options, budget, token).ConfigureAwait(false);
        };
    }

    private static async Task<Dictionary<string, long>> ReadStatusHistogramAsync(
        SupersededResultsPurger purger, CancellationToken ct)
    {
        await using var command = purger.CreateCommand(
            "SELECT Status, COUNT_BIG(*) FROM calc.CalculationRun GROUP BY Status;", []);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = reader.GetInt64(1);
        }

        return result;
    }

    private static async Task<string?> ReadRecoveryModelAsync(SupersededResultsPurger purger, CancellationToken ct)
    {
        try
        {
            await using var command = purger.CreateCommand(
                "SELECT recovery_model_desc FROM sys.databases WHERE name = DB_NAME();", []);
            return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }
        catch (DbException)
        {
            // Лише інформаційна перевірка: збій читання не має зупиняти прибирання.
            return null;
        }
    }

    /// <summary>Dry-run запасної форми (індексів прогону немає): за партиціями, лише рядки.</summary>
    private async Task<SupersededSweepOutcome> CountAsync(
        SupersededResultsPurger purger, List<int> periods, CancellationToken ct)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in purger.Tables)
        {
            long sum = 0;
            foreach (var period in periods)
            {
                sum += await purger.CountNonCurrentAsync(table, period, ct).ConfigureAwait(false);
            }

            counts[table] = sum;
        }

        var summary = string.Join("; ", counts.Select(c => $"{c.Key}={c.Value}"));
        LogDryRun(logger, summary);
        return new SupersededSweepOutcome(SupersededSweepOutcome.DryRunMode)
        {
            Candidates = counts,
            Periods = periods.Count,
            Complete = false,
        };
    }

    /// <summary>Видалення запасною формою (індексів прогону немає): keyset за партиціями, лише рядки.</summary>
    private async Task<SupersededSweepOutcome> DeleteAsync(
        SupersededResultsPurger purger, SupersededPurgeOptions options, List<int> periods, IJobProgress? progress,
        CancellationToken ct)
    {
        var budget = new SupersededResultsPurger.Budget(options.MaxBatchesPerRun);
        var deleted = new Dictionary<string, long>(StringComparer.Ordinal);
        var betweenBatches = await BetweenBatchesAsync(purger, options, budget, ct).ConfigureAwait(false);
        var touched = new SortedSet<int>();
        var finishedAll = true;

        foreach (var period in periods)
        {
            foreach (var table in purger.Tables)
            {
                if (budget.Stopped || budget.Remaining <= 0)
                {
                    finishedAll = false;
                    break;
                }

                var count = await purger.DeleteNonCurrentAsync(table, period, budget, betweenBatches, ct)
                    .ConfigureAwait(false);
                if (count > 0)
                {
                    deleted[table] = deleted.GetValueOrDefault(table) + count;
                    touched.Add(period);
                }

                // Таблиця не доїдена: бюджет вичерпано або проханий стоп (журнал).
                if (budget.Stopped || budget.Remaining <= 0)
                {
                    finishedAll = false;
                }
            }

            if (!finishedAll)
            {
                break;
            }

            if (progress is not null)
            {
                await progress.ReportAsync(
                        Math.Min(99, (periods.IndexOf(period) + 1) * 100 / Math.Max(1, periods.Count)), null, ct)
                    .ConfigureAwait(false);
            }
        }

        var outcome = new SupersededSweepOutcome(SupersededSweepOutcome.DeleteMode)
        {
            Deleted = deleted,
            Periods = periods.Count,
            Batches = budget.Done,
            Complete = finishedAll,
            StopReason = budget.StopReason,
        };

        var deletedSummary = string.Join("; ", deleted.Select(c => $"{c.Key}={c.Value}"));
        LogDeleted(logger, deletedSummary, budget.Done, finishedAll);

        // Перебудова - лише коли ВСЕ прибрано: частково прибрана партиція перебудовувалась би щоразу.
        if (finishedAll && options.RebuildAfter && touched.Count > 0)
        {
            await RebuildAsync(purger, touched, ct).ConfigureAwait(false);
            outcome = outcome with { Rebuilt = true };
        }

        return outcome;
    }

    private async Task<bool> WaitForLogAsync(
        SupersededPurgeOptions options, SupersededResultsPurger.Budget budget, CancellationToken ct)
    {
        for (var pause = 0; ; pause++)
        {
            var percent = await ReadLogUsedPercentAsync(ct).ConfigureAwait(false);
            if (percent is not { } used || used <= options.LogPauseThresholdPercent)
            {
                return true;
            }

            if (pause >= options.MaxLogPauses)
            {
                budget.StopReason = "logFull";
                LogLogFull(logger, used, options.LogPauseThresholdPercent);
                return false;
            }

            LogLogPause(logger, used, options.LogPauseThresholdPercent);
            await Task.Delay(TimeSpan.FromSeconds(options.LogPauseSeconds), ct).ConfigureAwait(false);
        }
    }

    private async Task<double?> ReadLogUsedPercentAsync(CancellationToken ct)
    {
        if (logUsedPercent is not null)
        {
            return await logUsedPercent(ct).ConfigureAwait(false);
        }

        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT used_log_space_in_percent FROM sys.dm_db_log_space_usage;";
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value is null or DBNull ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch (DbException)
        {
            return null;
        }
    }

    private static async Task CheckpointAsync(SupersededResultsPurger purger, CancellationToken ct)
    {
        try
        {
            await using var command = purger.CreateCommand("CHECKPOINT;", []);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (DbException)
        {
            // Немає права на CHECKPOINT - пакети все одно йдуть; журнал чистить DBA.
        }
    }

    /// <summary>
    /// Необов'язковий крок після повного прибирання: перебудова індексів зачеплених партицій і статистика.
    /// Простір повертається лише перебудовою; файли не стискаються.
    /// </summary>
    private async Task RebuildAsync(SupersededResultsPurger purger, SortedSet<int> periods, CancellationToken ct)
    {
        foreach (var table in purger.Tables)
        {
            var partitioned = await IsPartitionedAsync(purger, table, ct).ConfigureAwait(false);
            if (partitioned)
            {
                foreach (var period in periods)
                {
                    await using var command = purger.CreateCommand(
                        $"ALTER INDEX ALL ON {table} REBUILD PARTITION = $PARTITION.pf_ByPeriodKey({period.ToString(CultureInfo.InvariantCulture)});",
                        []);
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
            else
            {
                await using var command = purger.CreateCommand($"ALTER INDEX ALL ON {table} REBUILD;", []);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using var stats = purger.CreateCommand($"UPDATE STATISTICS {table};", []);
            await stats.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        LogRebuilt(logger, periods.Count);
    }

    private static async Task<bool> IsPartitionedAsync(SupersededResultsPurger purger, string table, CancellationToken ct)
    {
        await using var command = purger.CreateCommand(
            $"SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.partition_schemes ps ON ps.data_space_id = i.data_space_id WHERE i.object_id = OBJECT_ID(N'{table}')) THEN 1 ELSE 0 END;",
            []);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Прибирання рядків неактуальних прогонів (mode=disabled): вимкнено ключем {Key}, нічого не пораховано й не видалено.")]
    private static partial void LogDisabled(ILogger logger, string key);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Прибирання рядків неактуальних прогонів (dry-run, нічого не видалено): було б видалено {Counts}.")]
    private static partial void LogDryRun(ILogger logger, string counts);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Прибирання рядків неактуальних прогонів (mode=delete): видалено {Counts}; пакетів {Batches}; доїдено все: {Complete}.")]
    private static partial void LogDeleted(ILogger logger, string counts, int batches, bool complete);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Прибирання рядків неактуальних прогонів зупинено: невідомі статуси прогонів: {Statuses}. Потрібне рішення людини.")]
    private static partial void LogUnknownStatuses(ILogger logger, string statuses);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Прибирання рядків неактуальних прогонів: журнал використано на {Used:F0} % (поріг {Threshold} %), пауза.")]
    private static partial void LogLogPause(ILogger logger, double used, int threshold);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Прибирання рядків неактуальних прогонів зупинено: журнал {Used:F0} % (поріг {Threshold} %) не звільнився; наступний прохід продовжить.")]
    private static partial void LogLogFull(ILogger logger, double used, int threshold);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Прибирання рядків неактуальних прогонів: перебудовано індекси {Periods} партицій і оновлено статистику.")]
    private static partial void LogRebuilt(ILogger logger, int periods);
}

/// <summary>Підсумок проходу <see cref="SupersededResultsSweepJob"/> (для журналу й <c>MaintenanceRun.DetailsJson</c>).</summary>
/// <param name="Mode"><c>disabled</c>, <c>aborted</c> (невідомий статус), <c>dryRun</c> або <c>delete</c>.</param>
public sealed record SupersededSweepOutcome(string Mode)
{
    /// <summary>Режим: прохід вимкнено ключем.</summary>
    public const string DisabledMode = "disabled";

    /// <summary>Режим: зупинено перевіркою (невідомий статус).</summary>
    public const string AbortedMode = "aborted";

    /// <summary>Режим: лише підрахунок.</summary>
    public const string DryRunMode = "dryRun";

    /// <summary>Режим: видалення.</summary>
    public const string DeleteMode = "delete";

    /// <summary><c>true</c> - прохід зупинено перевіркою.</summary>
    public bool Aborted => Mode == AbortedMode;

    /// <summary>Причина зупинки перевіркою.</summary>
    public string? AbortReason { get; init; }

    /// <summary>Гістограма статусів <c>calc.CalculationRun</c> на початку проходу.</summary>
    public IReadOnlyDictionary<string, long> StatusHistogram { get; init; } = new Dictionary<string, long>();

    /// <summary>Dry-run: скільки рядків було б видалено, за таблицями.</summary>
    public IReadOnlyDictionary<string, long> Candidates { get; init; } = new Dictionary<string, long>();

    /// <summary>Видалено рядків за таблицями.</summary>
    public IReadOnlyDictionary<string, long> Deleted { get; init; } = new Dictionary<string, long>();

    /// <summary>Скільки партицій мають неактуальні прогони.</summary>
    public int Periods { get; init; }

    /// <summary>Скільки пакетів виконано.</summary>
    public int Batches { get; init; }

    /// <summary><c>true</c> - у режимі видалення кандидатів не лишилося (інакше стеля/журнал; наступний прохід продовжить).</summary>
    public bool Complete { get; init; }

    /// <summary>Чому видалення зупинилось раніше (<c>logFull</c>, <c>maxBatches</c>), якщо так.</summary>
    public string? StopReason { get; init; }

    /// <summary>Recovery model бази на момент проходу.</summary>
    public string? RecoveryModel { get; init; }

    /// <summary>Чи перебудовано індекси після прибирання.</summary>
    public bool Rebuilt { get; init; }

    /// <summary>Скільки прогонів підлягало прибиранню на початку проходу (основна форма).</summary>
    public int RunCandidates { get; init; }

    /// <summary>
    /// <c>false</c> — індексів за <c>CalculationRunId</c> немає: прохід ішов запасною формою (лише рядки, рядки
    /// прогонів лишилися). Потрібен повторний <c>07-partition-tables.sql</c>.
    /// </summary>
    public bool RunIndexes { get; init; } = true;

    /// <summary>Тривалість видалення, секунд (основна форма).</summary>
    public long ElapsedSeconds { get; init; }

    /// <summary>Підсумок для <c>MaintenanceRun.DetailsJson</c>.</summary>
    public string ToJson()
        => System.Text.Json.JsonSerializer.Serialize(
            new
            {
                mode = Mode,
                abortReason = AbortReason,
                statusHistogram = StatusHistogram,
                candidates = Candidates,
                runCandidates = RunCandidates,
                deleted = Deleted,
                periods = Periods,
                batches = Batches,
                complete = Complete,
                stopReason = StopReason,
                recoveryModel = RecoveryModel,
                rebuilt = Rebuilt,
                runIndexes = RunIndexes,
                elapsedSeconds = ElapsedSeconds,
            },
            System.Text.Json.JsonSerializerOptions.Web);
}
