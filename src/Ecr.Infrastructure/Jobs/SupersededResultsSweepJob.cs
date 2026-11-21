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
                if (options.NightWindowInvalid)
                {
                    LogNightWindowInvalid(logger, SupersededPurgeOptions.NightWindowKey, configuration[SupersededPurgeOptions.NightWindowKey]);
                }

                // ✎ D-560 (H1): накопичене видаляється ЛИШЕ в нічному вікні і не більше одного проходу видалення за вікно
                // (нічний бюджет). Поза вікном (наздоганяння на старті служби, ручний запуск удень) — лише лічить.
                var gate = await DeleteGateAsync(options, run.Id, ct).ConfigureAwait(false);
                var effective = options.DeleteEnabled && gate.Allowed ? options : options with { DeleteEnabled = false };
                outcome = await RunAsync(effective, progress, ct, gate.UntilUtc).ConfigureAwait(false);
                if (options.DeleteEnabled && !gate.Allowed)
                {
                    LogDeleteDeferred(logger, gate.Reason!);
                    outcome = outcome with { DeleteDeferred = gate.Reason };
                }
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

    /// <summary>Причина, з якої видалення цього проходу заборонене (<c>null</c> — дозволене), і межа часу видалення.</summary>
    /// <param name="Allowed">Чи можна видаляти.</param>
    /// <param name="Reason"><c>outsideWindow</c> або <c>nightBudgetSpent</c>.</param>
    /// <param name="UntilUtc">Кінець поточного нічного вікна (UTC): далі прохід зупиняється (<c>windowEnd</c>).</param>
    private sealed record DeleteGate(bool Allowed, string? Reason, DateTime? UntilUtc);

    /// <summary>Чи можна цьому проходу видаляти: зараз нічне вікно і в ньому ще не було проходу видалення.</summary>
    private async Task<DeleteGate> DeleteGateAsync(SupersededPurgeOptions options, long thisRunId, CancellationToken ct)
    {
        var utcNow = clock.UtcNow;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), TimeZoneInfo.Local);
        if (options.NightWindowAround(localNow) is not { } window)
        {
            return new DeleteGate(false, SupersededSweepOutcome.OutsideWindow, null);
        }

        // Межі вікна в UTC — різницею від «тепер», без перетворення інших моментів (перехід на літній час не зсуває).
        var windowStartUtc = utcNow - (localNow - window.Start);
        var untilUtc = utcNow + (window.End - localNow);

        var spent = await db.MaintenanceRuns
            .AsNoTracking()
            .AnyAsync(
                r => r.JobCode == Code && r.Id != thisRunId && r.StartedAt >= windowStartUtc && r.StartedAt <= utcNow
                     && r.DetailsJson != null && r.DetailsJson.Contains("\"mode\":\"delete\""),
                ct)
            .ConfigureAwait(false);

        return spent
            ? new DeleteGate(false, SupersededSweepOutcome.NightBudgetSpent, null)
            : new DeleteGate(true, null, untilUtc);
    }

    /// <summary>Один прохід: перевірки, dry-run (лічить) або видалення пакетами. Публічний для тестів і ручного запуску.</summary>
    /// <param name="options">Налаштування.</param>
    /// <param name="progress">Канал прогресу; <c>null</c> - без звіту.</param>
    /// <param name="ct">Скасування.</param>
    /// <param name="deleteUntilUtc">Межа часу видалення (кінець нічного вікна); <c>null</c> — без межі (тести, ручний).</param>
    /// <remarks>
    /// ⛔ Нічне вікно й нічний бюджет перевіряє <see cref="ExecuteAsync"/> (вхід задачі з розкладу й наздоганяння):
    /// поза ним сюди приходить <c>DeleteEnabled=false</c>.
    /// </remarks>
    public async Task<SupersededSweepOutcome> RunAsync(
        SupersededPurgeOptions options, IJobProgress? progress, CancellationToken ct, DateTime? deleteUntilUtc = null)
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
                ? await DeleteRunsAsync(purger, options, runs, progress, deleteUntilUtc, ct).ConfigureAwait(false)
                : await CountRunsAsync(purger, runs, ct).ConfigureAwait(false);
        }
        else
        {
            SupersededResultsPurger.LogRunIndexesMissing(logger);
            var periods = await purger.ListEligiblePeriodsAsync(ct).ConfigureAwait(false);
            outcome = options.DeleteEnabled
                ? await DeleteAsync(purger, options, periods, progress, deleteUntilUtc, ct).ConfigureAwait(false)
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
        IJobProgress? progress, DateTime? deleteUntilUtc, CancellationToken ct)
    {
        var budget = new SupersededResultsPurger.Budget(options.MaxBatchesPerRun);
        var deleted = new Dictionary<string, long>(StringComparer.Ordinal);
        var guard = await GuardAsync(purger, options, budget, deleteUntilUtc, ct).ConfigureAwait(false);
        var betweenBatches = guard.BetweenBatches;
        var touched = new SortedSet<int>();
        var finishedAll = true;
        var started = System.Diagnostics.Stopwatch.StartNew();

        // ⛔ H2: журнал і вікно перевіряються ще ДО першого пакета, а не лише після нього (fail closed).
        if (runs.Count > 0 && !await betweenBatches(ct).ConfigureAwait(false))
        {
            budget.Stopped = true;
        }

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
            CheckpointSkipped = guard.CheckpointSkipped,
        };

        var deletedSummary = string.Join("; ", deleted.Select(c => $"{c.Key}={c.Value}"));
        LogDeleted(logger, deletedSummary, budget.Done, finishedAll);

        if (finishedAll && options.RebuildAfter && touched.Count > 0)
        {
            outcome = await RebuildIfPermittedAsync(purger, touched, outcome, ct).ConfigureAwait(false);
        }

        return outcome;
    }

    /// <summary>Перевірки між пакетами одного проходу і що з них пропущено через брак права.</summary>
    private sealed class BatchGuard
    {
        /// <summary>Перевірка; <c>false</c> — зупинитися (причина — у бюджеті).</summary>
        public required Func<CancellationToken, Task<bool>> BetweenBatches { get; init; }

        /// <summary>SIMPLE, але <c>CHECKPOINT</c> пропущено: немає права (db_owner/db_backupoperator).</summary>
        public bool CheckpointSkipped { get; set; }
    }

    /// <summary>
    /// Перевірки між пакетами: межа нічного вікна; SIMPLE — <c>CHECKPOINT</c> кожні N пакетів (якщо є право);
    /// будь-яка інша модель відновлення (FULL, BULK_LOGGED, невідома) — пауза, поки журнал заповнений, а журнал, який
    /// не вдалося прочитати, — СТОП (<c>logUnknown</c>): fail closed.
    /// </summary>
    private async Task<BatchGuard> GuardAsync(
        SupersededResultsPurger purger, SupersededPurgeOptions options, SupersededResultsPurger.Budget budget,
        DateTime? deleteUntilUtc, CancellationToken ct)
    {
        var recovery = await ReadRecoveryModelAsync(purger, ct).ConfigureAwait(false);
        var simple = string.Equals(recovery, "SIMPLE", StringComparison.OrdinalIgnoreCase);
        var canCheckpoint = simple && await CanCheckpointAsync(purger, ct).ConfigureAwait(false);
        var sinceCheckpoint = 0;

        if (simple && !canCheckpoint)
        {
            LogCheckpointSkipped(logger);
        }

        var guard = new BatchGuard
        {
            CheckpointSkipped = simple && !canCheckpoint,
            BetweenBatches = async token =>
            {
                if (deleteUntilUtc is { } until && clock.UtcNow >= until)
                {
                    budget.StopReason = SupersededSweepOutcome.WindowEnd;
                    LogWindowEnd(logger);
                    return false;
                }

                if (simple)
                {
                    if (canCheckpoint && ++sinceCheckpoint >= options.CheckpointEveryBatches)
                    {
                        sinceCheckpoint = 0;
                        await CheckpointAsync(purger, token).ConfigureAwait(false);
                    }

                    return true;
                }

                return await WaitForLogAsync(options, budget, token).ConfigureAwait(false);
            },
        };

        return guard;
    }

    /// <summary>Чи може обліковий запис служби виконати <c>CHECKPOINT</c> (sysadmin, db_owner, db_backupoperator).</summary>
    private static async Task<bool> CanCheckpointAsync(SupersededResultsPurger purger, CancellationToken ct)
    {
        try
        {
            await using var command = purger.CreateCommand(
                """
                SELECT CASE WHEN IS_SRVROLEMEMBER(N'sysadmin') = 1 OR IS_ROLEMEMBER(N'db_owner') = 1
                                 OR IS_ROLEMEMBER(N'db_backupoperator') = 1 THEN 1 ELSE 0 END;
                """,
                []);
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
        }
        catch (DbException)
        {
            return false;
        }
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
        DateTime? deleteUntilUtc, CancellationToken ct)
    {
        var budget = new SupersededResultsPurger.Budget(options.MaxBatchesPerRun);
        var deleted = new Dictionary<string, long>(StringComparer.Ordinal);
        var guard = await GuardAsync(purger, options, budget, deleteUntilUtc, ct).ConfigureAwait(false);
        var betweenBatches = guard.BetweenBatches;
        var touched = new SortedSet<int>();
        var finishedAll = true;

        if (periods.Count > 0 && !await betweenBatches(ct).ConfigureAwait(false))
        {
            budget.Stopped = true;
        }

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
            CheckpointSkipped = guard.CheckpointSkipped,
        };

        var deletedSummary = string.Join("; ", deleted.Select(c => $"{c.Key}={c.Value}"));
        LogDeleted(logger, deletedSummary, budget.Done, finishedAll);

        // Перебудова - лише коли ВСЕ прибрано: частково прибрана партиція перебудовувалась би щоразу.
        if (finishedAll && options.RebuildAfter && touched.Count > 0)
        {
            outcome = await RebuildIfPermittedAsync(purger, touched, outcome, ct).ConfigureAwait(false);
        }

        return outcome;
    }

    /// <summary>
    /// Перебудова після повного прибирання — лише з правом <c>ALTER</c> на всі таблиці правила; без нього — пропуск із
    /// записом у журнал (<c>rebuildSkipped</c>), а не збій проходу: служба за замовчуванням має лише читання й запис.
    /// </summary>
    private async Task<SupersededSweepOutcome> RebuildIfPermittedAsync(
        SupersededResultsPurger purger, SortedSet<int> touched, SupersededSweepOutcome outcome, CancellationToken ct)
    {
        foreach (var table in purger.Tables)
        {
            await using var command = purger.CreateCommand(
                $"SELECT ISNULL(HAS_PERMS_BY_NAME(N'{table}', N'OBJECT', N'ALTER'), 0);", []);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) != 1)
            {
                LogRebuildSkipped(logger, table);
                return outcome with { RebuildSkipped = true };
            }
        }

        await RebuildAsync(purger, touched, ct).ConfigureAwait(false);
        return outcome with { Rebuilt = true };
    }

    private async Task<bool> WaitForLogAsync(
        SupersededPurgeOptions options, SupersededResultsPurger.Budget budget, CancellationToken ct)
    {
        for (var pause = 0; ; pause++)
        {
            var percent = await ReadLogUsedPercentAsync(ct).ConfigureAwait(false);

            // ⛔ H2: невідома заповненість журналу — СТОП, а не «продовжити» (fail closed): видалення десятків ГБ при
            // FULL без резервних копій журналу заповнило б диск.
            if (percent is not { } used)
            {
                budget.StopReason = SupersededSweepOutcome.LogUnknown;
                LogLogUnknown(logger);
                return false;
            }

            if (used <= options.LogPauseThresholdPercent)
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

        // ✎ H2: `sys.database_files` + FILEPROPERTY — доступні користувачу бази (db_datareader); `sys.dm_db_log_space_usage`
        // вимагає VIEW SERVER STATE і для служби давав збій → null → прохід продовжував наосліп.
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                SELECT CAST(SUM(CAST(FILEPROPERTY(name, 'SpaceUsed') AS bigint)) AS float) * 100.0
                       / NULLIF(SUM(CAST(size AS bigint)), 0)
                  FROM sys.database_files
                 WHERE type = 1;
                """;
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value is null or DBNull ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch (DbException)
        {
            return null;
        }
    }

    private async Task CheckpointAsync(SupersededResultsPurger purger, CancellationToken ct)
    {
        try
        {
            await using var command = purger.CreateCommand("CHECKPOINT;", []);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            // Право перевірено заздалегідь (CanCheckpointAsync); збій тут — не привід зупиняти, але не мовчки.
            LogCheckpointFailed(logger, ex.Message);
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
        Message = "Прибирання неактуальних прогонів: видалення відкладено ({Reason}) - прохід лише лічить; накопичене видаляється в нічному вікні.")]
    private static partial void LogDeleteDeferred(ILogger logger, string reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Прибирання неактуальних прогонів: {Key} = «{Value}» не розібрано (очікується HH:mm-HH:mm) - діє 01:00-05:00.")]
    private static partial void LogNightWindowInvalid(ILogger logger, string key, string? value);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Прибирання неактуальних прогонів зупинено: нічне вікно скінчилося; решту доїсть наступна ніч.")]
    private static partial void LogWindowEnd(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Прибирання неактуальних прогонів зупинено: заповненість журналу транзакцій не прочитано (logUnknown) - без неї видалення не йде.")]
    private static partial void LogLogUnknown(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Прибирання неактуальних прогонів: SIMPLE recovery, але CHECKPOINT пропущено - у служби немає права (db_owner/db_backupoperator); журнал звільняють автоматичні контрольні точки.")]
    private static partial void LogCheckpointSkipped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Прибирання неактуальних прогонів: CHECKPOINT не вдався ({Error}).")]
    private static partial void LogCheckpointFailed(ILogger logger, string error);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Прибирання неактуальних прогонів: перебудову індексів пропущено - у служби немає ALTER на {Table}; виконує DBA (runbook §4.2).")]
    private static partial void LogRebuildSkipped(ILogger logger, string table);

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

    /// <summary>Чому видалення зупинилось раніше (<c>logFull</c>, <c>logUnknown</c>, <c>maxBatches</c>, <c>windowEnd</c>), якщо так.</summary>
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

    /// <summary>Причина зупинки: скінчилося нічне вікно.</summary>
    public const string WindowEnd = "windowEnd";

    /// <summary>Причина зупинки: заповненість журналу не прочитано (fail closed).</summary>
    public const string LogUnknown = "logUnknown";

    /// <summary>Видалення відкладено: зараз поза нічним вікном.</summary>
    public const string OutsideWindow = "outsideWindow";

    /// <summary>Видалення відкладено: у цьому нічному вікні прохід видалення вже був (нічний бюджет).</summary>
    public const string NightBudgetSpent = "nightBudgetSpent";

    /// <summary>
    /// Чому прохід з увімкненим видаленням лише лічив (<see cref="OutsideWindow"/>, <see cref="NightBudgetSpent"/>);
    /// <c>null</c> — не відкладалось.
    /// </summary>
    public string? DeleteDeferred { get; init; }

    /// <summary>SIMPLE, але <c>CHECKPOINT</c> пропущено: немає права.</summary>
    public bool CheckpointSkipped { get; init; }

    /// <summary>Перебудову пропущено: немає <c>ALTER</c> на таблиці.</summary>
    public bool RebuildSkipped { get; init; }

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
                deleteDeferred = DeleteDeferred,
                checkpointSkipped = CheckpointSkipped,
                rebuildSkipped = RebuildSkipped,
            },
            System.Text.Json.JsonSerializerOptions.Web);
}
