// tests/Ecr.Api.Tests/CalculationStepTraceLogVolumeTests.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Calculations;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Api.Tests;

/// <summary>
/// ✎ D-565 (PERF-03): замір журналу транзакцій на кроках трейсу ОДНОГО прогону — до (EF-пакети, нестиснений
/// <c>TraceJson</c>) і після (масовий запис, стиснений; без прапора — нуль кроків), разом із прибиранням (D-560).
/// </summary>
/// <remarks>
/// ⚠ Вимірювач, а не сторож: без вибірки (<c>ECR_TRACE_SAMPLE</c> — JSON кроків реального прогону, експорт лише
/// <c>SELECT</c> зі стенда) тест нічого не робить. Числа — у вивід тесту (і в звіт lane), порогів немає: обсяг журналу
/// залежить від сервера. Власна база (<c>_trlog</c>): у спільній базі колекції журнал писали б і сусідні тести.
/// Міра — <c>database_transaction_log_bytes_used</c> транзакції запису (<c>sys.dm_tran_database_transactions</c>) і
/// приріст записаних байтів файлу журналу (<c>sys.dm_io_virtual_file_stats</c>); <c>fn_dblog</c> не вживається.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationStepTraceLogVolumeTests(ITestOutputHelper output)
{
    private static readonly DateTime Now = new(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-565")]
    public async Task Журнал_одного_прогону_до_і_після()
    {
        var samplePath = Environment.GetEnvironmentVariable("ECR_TRACE_SAMPLE");
        if (string.IsNullOrWhiteSpace(samplePath) || !File.Exists(samplePath))
        {
            output.WriteLine("ECR_TRACE_SAMPLE не задано — замір пропущено.");
            return;
        }

        var database = SqlServerFixture.WithOwnDatabase("_trlog");
        await database.InitializeAsync().ConfigureAwait(true);
        try
        {
            await MeasureAsync(database.ConnectionString, samplePath).ConfigureAwait(true);
        }
        finally
        {
            await database.DisposeAsync().ConfigureAwait(true);
        }
    }

    private async Task MeasureAsync(string connectionString, string samplePath)
    {
        var builder = new TestDocumentBuilder(connectionString);
        var document = await builder.BuildAsync(periodKey: 209912).ConfigureAwait(true);
        var period = document.PeriodKey.Value;
        var sample = LoadSample(samplePath, document.DocumentId);
        var steps = sample.Sum(o => o.Trace.Count);
        output.WriteLine($"Вибірка: {steps} кроків, {sample.Count} рядків; TraceJson (UTF-16) "
            + $"{sample.Sum(o => o.Trace.Sum(s => 2L * ((s.Detail ?? s.TraceJson)?.Length ?? 0))) / 1024 / 1024.0:F1} МБ.");

        // До: як писав EF-шлях до D-565 — сутності, TraceJson текстом, SaveChanges пакетами по 5 000 рядків.
        var before = await RunAsync(connectionString, document.ProjectId, period, async (db, runId, ct) =>
        {
            var id = runId * 1_000_000;
            foreach (var chunk in sample.SelectMany(o => o.Trace.Select(s => (o, s))).Chunk(CalculationOutputWriter.DefaultMaxRowsPerSave))
            {
                foreach (var (o, s) in chunk)
                {
                    var step = new CalculationStep(runId, period, s.StepOrder, s.StepCode);
                    typeof(Domain.Abstractions.Entity<long>).GetProperty(nameof(Domain.Abstractions.Entity<long>.Id))!
                        .SetValue(step, ++id);
                    step.SetAddress(o.DocumentId, o.SourceRowKey, s.SubstanceEntryId);
                    step.Describe(s.Expression, s.Value, traceJson: null, resultId: null, s.Masked);
                    db.CalculationSteps.Add(step);
                    db.Entry(step).Property(nameof(CalculationStep.TraceJsonText)).CurrentValue = s.Detail ?? s.TraceJson;
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);

        // Після, прапор увімкнено: записувач → масовий запис, стиснений TraceJsonGz.
        var on = await RunAsync(connectionString, document.ProjectId, period, (db, runId, ct) =>
            new CalculationOutputWriter(new CalculationResultStore(db, new TestClock(Now)), new UnitOfWork(db, new TestClock(Now)))
                .WriteAsync(runId, sample, TraceLevel.Full, ct)).ConfigureAwait(true);

        // Після, прапор вимкнено (типово): кроків немає.
        var off = await RunAsync(connectionString, document.ProjectId, period, (db, runId, ct) =>
            new CalculationOutputWriter(new CalculationResultStore(db, new TestClock(Now)), new UnitOfWork(db, new TestClock(Now)))
                .WriteAsync(runId, sample, TraceLevel.Full, ct, recordSteps: false)).ConfigureAwait(true);

        Report("До (EF, текст)", before);
        Report("Після, прапор увімкнено (bulk, стиснено)", on);
        Report("Після, прапор вимкнено (типово)", off);

        Assert.Equal(steps, before.Rows);
        Assert.Equal(steps, on.Rows);
        Assert.Equal(0, off.Rows);
        Assert.True(on.WriteLogBytes < before.WriteLogBytes, "масовий стиснений запис мусить писати менше журналу");
    }

    private void Report(string name, Measurement m)
        => output.WriteLine(
            $"{name}: кроків {m.Rows}; EF-команд {m.Commands}; журнал запису {m.WriteLogBytes / 1048576.0:F2} МБ "
            + $"(файл журналу +{m.WriteLogFileBytes / 1048576.0:F2} МБ), {m.WriteMs} мс; дані кроків "
            + $"{m.StoredBytes / 1048576.0:F2} МБ (TraceJson {m.TextBytes / 1048576.0:F2} + TraceJsonGz {m.GzBytes / 1048576.0:F2}); "
            + $"прибирання кроків (D-560) — журнал {m.PurgeLogBytes / 1048576.0:F2} МБ.");

    private static async Task<Measurement> RunAsync(
        string connectionString, int projectId, int period, Func<EcrDbContext, long, CancellationToken, Task> write)
    {
        var counter = new CommandCounter();
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo").CommandTimeout(600))
            .AddInterceptors(counter)
            .Options);

        var run = new CalculationRun(projectId, period, triggeredByUserId: null, Now);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync().ConfigureAwait(true);
        db.ChangeTracker.Clear();
        counter.Reset();

        var fileBefore = await LogFileBytesAsync(db).ConfigureAwait(true);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long logBytes;
        await using (var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(true))
        {
            await write(db, run.Id, CancellationToken.None).ConfigureAwait(true);
            logBytes = await TransactionLogBytesAsync(db).ConfigureAwait(true);
            await tx.CommitAsync().ConfigureAwait(true);
        }

        watch.Stop();
        var commands = counter.Count;
        var fileAfter = await LogFileBytesAsync(db).ConfigureAwait(true);
        db.ChangeTracker.Clear();

        var sizes = (await db.Database.SqlQuery<Sizes>($"""
            SELECT COUNT_BIG(*) AS Rows,
                   ISNULL(SUM(CAST(DATALENGTH(TraceJson) AS bigint)), 0) AS TextBytes,
                   ISNULL(SUM(CAST(DATALENGTH(TraceJsonGz) AS bigint)), 0) AS GzBytes,
                   ISNULL(SUM(CAST(ISNULL(DATALENGTH(TraceJson), 0) AS bigint) + ISNULL(DATALENGTH(TraceJsonGz), 0)
                       + ISNULL(DATALENGTH(Expression), 0) + DATALENGTH(StepCode) + ISNULL(DATALENGTH(SourceRowKey), 0)), 0) AS StoredBytes
              FROM calc.CalculationStep WHERE PeriodKey = {period} AND CalculationRunId = {run.Id}
            """).ToListAsync().ConfigureAwait(true))[0];

        // D-560: прибирання витісненого прогону видаляє ті самі кроки — друга половина журналу трейсу.
        long purgeBytes;
        await using (var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(true))
        {
            await db.Database.ExecuteSqlAsync(
                $"DELETE FROM calc.CalculationStep WHERE PeriodKey = {period} AND CalculationRunId = {run.Id}").ConfigureAwait(true);
            purgeBytes = await TransactionLogBytesAsync(db).ConfigureAwait(true);
            await tx.CommitAsync().ConfigureAwait(true);
        }

        return new Measurement(
            (int)sizes.Rows, commands, logBytes, fileAfter - fileBefore, watch.ElapsedMilliseconds,
            sizes.StoredBytes, sizes.TextBytes, sizes.GzBytes, purgeBytes);
    }

    private static async Task<long> TransactionLogBytesAsync(EcrDbContext db)
        => (await db.Database.SqlQuery<long>($"""
            SELECT ISNULL(SUM(t.database_transaction_log_bytes_used), 0) AS Value
              FROM sys.dm_tran_database_transactions t
              JOIN sys.dm_tran_session_transactions s ON s.transaction_id = t.transaction_id
             WHERE s.session_id = @@SPID AND t.database_id = DB_ID()
            """).ToListAsync().ConfigureAwait(true))[0];

    private static async Task<long> LogFileBytesAsync(EcrDbContext db)
        => (await db.Database.SqlQuery<long>($"""
            SELECT SUM(v.num_of_bytes_written) AS Value
              FROM sys.dm_io_virtual_file_stats(DB_ID(), NULL) v
              JOIN sys.database_files f ON f.file_id = v.file_id
             WHERE f.type_desc = N'LOG'
            """).ToListAsync().ConfigureAwait(true))[0];

    /// <summary>Кроки вибірки як виходи рядків (без значень — міряється лише трейс).</summary>
    private static List<CalculationOutput> LoadSample(string path, long documentId)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return [.. json.RootElement.EnumerateArray()
            .Select(e => (Row: e.GetProperty("k").GetString(), Step: new CalculationTraceStep(
                e.GetProperty("o").GetInt32(),
                e.GetProperty("c").GetString()!,
                e.GetProperty("e").GetString(),
                e.GetProperty("v").GetString() is { } v ? decimal.Parse(v, CultureInfo.InvariantCulture) : null,
                TraceJson: null,
                (MaskedZeroReason)e.GetProperty("m").GetInt32(),
                Detail: e.GetProperty("j").GetString(),
                SubstanceEntryId: e.GetProperty("s").ValueKind == JsonValueKind.Number ? e.GetProperty("s").GetInt32() : null)))
            .GroupBy(x => x.Row)
            .Select(g => new CalculationOutput(documentId, g.Key, [], [.. g.Select(x => x.Step)]))];
    }

    private sealed record Measurement(
        int Rows, int Commands, long WriteLogBytes, long WriteLogFileBytes, long WriteMs,
        long StoredBytes, long TextBytes, long GzBytes, long PurgeLogBytes);

    private sealed record Sizes(long Rows, long TextBytes, long GzBytes, long StoredBytes);

    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int count;

        public int Count => Volatile.Read(ref count);

        public void Reset() => Interlocked.Exchange(ref count, 0);

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
