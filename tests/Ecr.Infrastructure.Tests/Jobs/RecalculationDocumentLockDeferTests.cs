// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationDocumentLockDeferTests.cs
using System.Diagnostics;
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Quartz.Impl;
using Quartz.Spi;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// O1 (I2 ФВ-9.8): задача перерахунку, що не взяла лок документа, не тримає слот
/// виконавця — вона відкладається, слот бере інша задача, а відкладена
/// виконується пізніше й не витрачає спроби.
/// </summary>
/// <remarks>
/// ⛔ Дефект, який доводять тести. Інкрементна задача формул чекала лок документа
/// до 2 хв (повна — до 15 хв) у слоті виконавця: у замірі I2 під навантаженням усі
/// 10 потоків Quartz стояли за локом гарячого документа, і перерахунок проєкту
/// простояв 630 с. Тут виконавець з ОДНИМ слотом: поки лок зайнятий, інша задача
/// мусить виконатися.
/// <para>
/// Мутації: (1) <c>BusyWait</c> = 2 хв (блокуюче очікування) — червоні обидва
/// «звільняє слот» (свідок не виконався за 20 с); (2) <c>DeferAsync</c> без
/// відновлення <c>Attempt</c> — червоний «Database» (Attempt ≥ 2); (3) адаптер
/// трактує відкладення як ретрай — червоний «триґер відкладення» (спроба 3 замість 2).
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "ФВ-9.8")]
public sealed class RecalculationDocumentLockDeferTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Database_задача_без_лока_звільняє_слот_виконується_пізніше_і_не_рахує_спроби()
    {
        var doc = await ArrangeAsync();

        // Документ рахує «інша задача» — лок тримає тест.
        var held = await SqlDistributedLock.AcquireAsync(
            Sql.ConnectionString, RecalculationDocumentLock.Resource(doc.DocumentId), TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(held);

        var probe = new WorkerProbe();
        await using var provider = BuildHost(doc, probe);

        // Задача формул першою в черзі, свідок — за нею.
        string formulaJob;
        string witnessJob;
        await using (var scope = provider.CreateAsyncScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<DbBackgroundJobScheduler>();
            formulaJob = await jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(
                FormulaRecalculationTarget.Of(doc.DocumentId, doc.PeriodKey.Value, 7), Payload(doc), CancellationToken.None, 7);
            witnessJob = await jobs.EnqueueAsync<WorkerProbeJob>(new { n = 1 }, CancellationToken.None);
        }

        var worker = new JobWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new JobWorkerOptions
            {
                Lanes = JobLanes.All,
                Role = JobProgressStore.CurrentRole,
                MaxConcurrency = 1,
                PollInterval = TimeSpan.FromMilliseconds(50),
                RenewInterval = TimeSpan.FromMilliseconds(200),
            },
            provider.GetRequiredService<JobQueueSignal>(),
            NullLogger<JobWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        try
        {
            // ⛔ Єдиний слот не зайнятий очікуванням лока: свідок виконується, ПОКИ лок тримають.
            await WaitForStateAsync(witnessJob, "Succeeded");
            Assert.Single(probe.Runs);

            await held.DisposeAsync();

            // Відкладена виконується після звільнення лока — і рахує від останнього входу.
            var row = await WaitForStateAsync(formulaJob, "Succeeded");
            Assert.Equal(60m, await OutputAsync(doc));

            // ⛔ Відкладення — не спроба: одна зарахована (остання), жодного переклейму.
            Assert.Equal(1, row.Attempt);
            Assert.Equal(0, row.ReclaimCount);
        }
        finally
        {
            await held.DisposeAsync();
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    [Fact]
    public async Task Quartz_задача_без_лока_звільняє_потік_і_виконується_пізніше()
    {
        var doc = await ArrangeAsync();

        var held = await SqlDistributedLock.AcquireAsync(
            Sql.ConnectionString, RecalculationDocumentLock.Resource(doc.DocumentId), TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(held);

        var witness = new WitnessJob();
        var services = new ServiceCollection();
        services.AddScoped(_ => Sql.CreateContext());
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IFormulaRecalculationJob>(sp =>
            FormulaRecalculationDocumentLockTests.IncrementalJob(sp.GetRequiredService<EcrDbContext>(), doc));
        services.AddSingleton(witness);
        await using var provider = services.BuildServiceProvider();

        var factory = new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-tests-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
        });
        var quartz = await factory.GetScheduler();
        quartz.JobFactory = new AdapterFactory(provider);

        try
        {
            var jobs = new QuartzJobScheduler(factory);
            await jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(
                FormulaRecalculationTarget.Of(doc.DocumentId, doc.PeriodKey.Value, 7), Payload(doc), CancellationToken.None, 7);

            // Свідок стартує ПІЗНІШЕ за задачу формул: єдиний потік першою бере її.
            await quartz.ScheduleJob(
                JobBuilder.Create<QuartzJobAdapter>()
                    .WithIdentity($"witness-{Guid.NewGuid():N}")
                    .UsingJobData(QuartzJobScheduler.JobCodeKey, typeof(WitnessJob).FullName!)
                    .UsingJobData(QuartzJobScheduler.PayloadKey, "null")
                    .Build(),
                TriggerBuilder.Create()
                    .StartAt(new DateTimeOffset(new SystemClock().UtcNow, TimeSpan.Zero).AddMilliseconds(300))
                    .Build());

            await quartz.Start();

            // ⛔ Єдиний потік не тримається очікуванням лока.
            await witness.Executed.Task.WaitAsync(Patience);
            Assert.NotEqual(60m, await OutputAsync(doc));

            await held.DisposeAsync();

            var clock = Stopwatch.StartNew();
            while (await OutputAsync(doc) != 60m)
            {
                Assert.True(clock.Elapsed < Patience, "Відкладена задача формул не виконалась після звільнення лока.");
                await Task.Delay(100);
            }
        }
        finally
        {
            await held.DisposeAsync();
            await quartz.Shutdown(waitForJobsToComplete: false);
        }
    }

    [Fact]
    public async Task Quartz_відкладення_ставить_триґер_через_відступ_з_тією_самою_спробою()
    {
        var now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        var progress = Substitute.For<IJobProgressStore>();
        var services = new ServiceCollection();
        services.AddSingleton(progress);
        services.AddSingleton<IClock>(new TestClock(now));
        services.AddScoped<DeferringJob>();
        await using var provider = services.BuildServiceProvider();

        var detail = Substitute.For<IJobDetail>();
        detail.Key.Returns(new JobKey("deferred-job-1"));
        detail.JobDataMap.Returns(new JobDataMap
        {
            { QuartzJobScheduler.JobCodeKey, typeof(DeferringJob).FullName! },
            { QuartzJobScheduler.PayloadKey, "null" },
        });

        var trigger = Substitute.For<ITrigger>();
        trigger.JobDataMap.Returns(new JobDataMap { { QuartzJobScheduler.RetryAttemptKey, "2" } });

        var scheduler = Substitute.For<IScheduler>();
        var context = Substitute.For<IJobExecutionContext>();
        context.JobDetail.Returns(detail);
        context.Trigger.Returns(trigger);
        context.Scheduler.Returns(scheduler);

        await new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance).Execute(context);

        var scheduled = (ITrigger)Assert.Single(
            scheduler.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IScheduler.ScheduleJob)).GetArguments()[0]!;
        Assert.Equal("2", scheduled.JobDataMap.GetString(QuartzJobScheduler.RetryAttemptKey));
        Assert.Equal(new DateTimeOffset(now, TimeSpan.Zero).Add(RecalculationDocumentLock.DeferDelay), scheduled.StartTimeUtc);

        // Не провал і не «повтор»: стан не закривається, текст ретраю не пишеться.
        await progress.DidNotReceive().FinishAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>(),
            Arg.Any<string?>());
        await progress.DidNotReceive().ReportAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    // ── Світ ─────────────────────────────────────────────────────────────────

    private static object Payload(TestDocument doc)
        => new
        {
            doc.DocumentId,
            doc.TableInstanceId,
            PeriodKey = doc.PeriodKey.Value,
            Cells = new[] { new { RowId = doc.RowIds[0], ColumnDefId = doc.ColumnDefIds[1] } },
        };

    /// <summary>Вхід 30 уже зафіксовано PATCH, обчислене — старе (20).</summary>
    private async Task<TestDocument> ArrangeAsync()
    {
        var doc = await new TestDocumentBuilder(Sql.ConnectionString)
            .BuildAsync(columnCount: 3, rowCount: 1, rowMode: TableRowMode.Dynamic);

        await ExecuteAsync(
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
            $"({doc.PeriodKey.Value}, {doc.RowIds[0]}, {doc.ColumnDefIds[1]}, {doc.TableDefId}, 30, 0, 0), " +
            $"({doc.PeriodKey.Value}, {doc.RowIds[0]}, {doc.ColumnDefIds[2]}, {doc.TableDefId}, 20, 1, 0);");

        return doc;
    }

    private ServiceProvider BuildHost(TestDocument doc, WorkerProbe probe)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Sql.CreateContext());
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IJobQueue>(sp => new DbJobQueue(sp.GetRequiredService<EcrDbContext>(), sp.GetRequiredService<IClock>()));
        services.AddScoped<IJobProgressStore>(sp => new JobProgressStore(sp.GetRequiredService<EcrDbContext>()));
        services.AddScoped<JobLeaseContext>();
        services.AddScoped<IJobLeaseContext>(sp => sp.GetRequiredService<JobLeaseContext>());
        services.AddSingleton<JobQueueSignal>();
        services.AddScoped(sp => new QuartzJobScheduler(null, sp.GetService<IJobProgressStore>(), sp.GetService<IClock>()));
        services.AddScoped<DbBackgroundJobScheduler>();
        services.AddSingleton(probe);
        services.AddScoped<WorkerProbeJob>();
        services.AddScoped<IFormulaRecalculationJob>(sp =>
            FormulaRecalculationDocumentLockTests.IncrementalJob(sp.GetRequiredService<EcrDbContext>(), doc));
        return services.BuildServiceProvider();
    }

    private async Task<JobProgress> WaitForStateAsync(string jobId, string state)
    {
        var clock = Stopwatch.StartNew();

        while (true)
        {
            var row = await RowAsync(jobId);
            if (row?.State == state)
            {
                return row;
            }

            Assert.True(clock.Elapsed < Patience, $"{jobId}: очікувався {state}, стан {row?.State ?? "—"}.");
            await Task.Delay(50);
        }
    }

    private async Task<decimal?> OutputAsync(TestDocument doc)
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = string.Create(
            CultureInfo.InvariantCulture,
            $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} AND TableRowId = {doc.RowIds[0]} AND ColumnDefId = {doc.ColumnDefIds[2]}");
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : (decimal)result;
    }

    private async Task ExecuteAsync(string statement)
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class AdapterFactory(IServiceProvider provider) : IJobFactory
    {
        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler)
            => new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance);

        public void ReturnJob(IJob job)
        {
        }
    }
}

/// <summary>Свідок Quartz: сигналить, що отримав потік.</summary>
public sealed class WitnessJob : IBackgroundJob
{
    public TaskCompletionSource Executed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        Executed.TrySetResult();
        return Task.CompletedTask;
    }
}

/// <summary>Задача, чий ресурс зайнятий.</summary>
public sealed class DeferringJob : IBackgroundJob
{
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        => throw new JobDeferredException(RecalculationDocumentLock.DeferDelay, "Документ зайнятий.");
}
