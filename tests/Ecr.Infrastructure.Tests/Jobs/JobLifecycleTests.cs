// tests/Ecr.Infrastructure.Tests/Jobs/JobLifecycleTests.cs
using System.Diagnostics;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Quartz.Impl;
using Quartz.Spi;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Життєвий цикл задачі за межами «щасливого» шляху: скасування в черзі й з
/// іншого інстанса (U3), зупинка хоста (U8) і ретенція завершених записів
/// прогресу (аудит P2). Прибирання покинутого (U4, U11) —
/// <c>AbandonedWorkSweeperTests</c>.
/// </summary>
/// <remarks>
/// ⚠ Усе СПРАВЖНЄ: SQL Server (<see cref="JobProgressStore"/>), планувальник
/// Quartz (<c>RAMJobStore</c>, власна фабрика з унікальним <c>instanceName</c>,
/// <c>Shutdown</c> у <c>finally</c> — як у <see cref="RecurringJobSurvivesRunTests"/>).
/// <c>JobCancelTests</c> (Api) ганяв підмінений планувальник, що сам
/// виставляв <c>Cancelled</c>, — через це дефект U3 і не був видний.
/// <para>
/// ⚠ Ретенція глобальна (видаляє КОЖЕН давній завершений рядок спільної
/// бази), тому моменти тут — у 2031 році, і тест перевіряє стан СВОЇХ рядків,
/// а не лічильники.
/// </para>
/// <para>
/// Мутаційні докази — у коментарях тестів; прогін і результат — в описі коміту.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class JobLifecycleTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2031, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Задача, що чекає скасування (або 20 с) — «довгий перерахунок».</summary>
    private sealed class BlockingJob : IBackgroundJob
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool SawCancellation { get; private set; }

        public int Calls { get; private set; }

        public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        {
            Calls++;
            Started.TrySetResult();

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
            }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
                throw;
            }
        }
    }

    private static StdSchedulerFactory Factory()
        => new(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-lifecycle-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
        });

    private ServiceProvider Provider(BlockingJob job, DateTime at, TimeSpan? heartbeat = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<EcrDbContext>(o => o.UseSqlServer(sql.ConnectionString));
        services.AddScoped<IJobProgressStore, JobProgressStore>();
        services.AddSingleton<Ecr.Domain.Abstractions.IClock>(new TestClock(at));
        services.AddSingleton(job);

        if (heartbeat is { } interval)
        {
            services.AddSingleton(new JobHeartbeatSettings(interval));
        }

        return services.BuildServiceProvider();
    }

    /// <summary>Планувальник-порт над фабрикою зі справжнім сховищем прогресу.</summary>
    private QuartzJobScheduler Jobs(ISchedulerFactory factory, EcrDbContext db, DateTime at)
        => new(factory, new JobProgressStore(db), new TestClock(at));

    private static async Task<IJobExecutionContext> ContextAsync(IScheduler quartz, JobKey key)
    {
        var detail = await quartz.GetJobDetail(key);
        Assert.NotNull(detail);

        var context = Substitute.For<IJobExecutionContext>();
        context.JobDetail.Returns(detail);
        context.Trigger.Returns((await quartz.GetTriggersOfJob(key)).First());
        context.Scheduler.Returns(quartz);
        context.CancellationToken.Returns(CancellationToken.None);
        return context;
    }

    private async Task<string?> StateAsync(string jobId)
    {
        await using var db = sql.CreateContext();
        return (await new JobProgressStore(db).FindAsync(jobId, CancellationToken.None))?.State;
    }

    // ── U3: скасування ──────────────────────────────────────────────────

    /// <remarks>
    /// ⛔ U3. Мутація: прибрати <c>CancelActiveAsync</c> з
    /// <c>QuartzJobScheduler.CancelAsync</c> → стан лишається <c>Queued</c>,
    /// червоний (прогнано).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Скасування_задачі_в_черзі_пише_Cancelled_а_не_лишає_Queued()
    {
        var factory = Factory();
        var quartz = await factory.GetScheduler();
        try
        {
            await using var db = sql.CreateContext();
            var jobs = Jobs(factory, db, Now);

            // Планувальник не запущено — задача стоїть у черзі.
            var jobId = await jobs.EnqueueAsync<BlockingJob>(null, CancellationToken.None);
            Assert.Equal("Queued", await StateAsync(jobId));

            await jobs.CancelAsync(jobId, CancellationToken.None);

            Assert.Equal("Cancelled", await StateAsync(jobId));
            Assert.False(await quartz.CheckExists(new JobKey(jobId)));
        }
        finally
        {
            await quartz.Shutdown(waitForJobsToComplete: false);
        }
    }

    /// <remarks>
    /// ⛔ U3, пауза ретраю: між спробами рядок <c>Running</c>, а задача — лише
    /// тригер у черзі. Мутація та сама, що вище → <c>Running</c> назавжди.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Скасування_в_паузі_ретраю_пише_Cancelled_а_не_лишає_Running()
    {
        var factory = Factory();
        var quartz = await factory.GetScheduler();
        try
        {
            await using var db = sql.CreateContext();
            var jobs = Jobs(factory, db, Now);
            var jobId = await jobs.EnqueueAsync<BlockingJob>(null, CancellationToken.None);

            // Перша спроба впала, адаптер поставив ретрай: рядок `Running`.
            var store = new JobProgressStore(db);
            await store.StartAsync(jobId, typeof(BlockingJob).FullName!, Now, CancellationToken.None);
            await store.ReportAsync(jobId, 0, "retry", Now, CancellationToken.None);

            await jobs.CancelAsync(jobId, CancellationToken.None);

            Assert.Equal("Cancelled", await StateAsync(jobId));
        }
        finally
        {
            await quartz.Shutdown(waitForJobsToComplete: false);
        }
    }

    /// <remarks>
    /// Скасування з ІНШОГО інстанса: задача в пам'яті інстанса A, скасування
    /// прийшло на B. Мутація: прибрати в <c>QuartzJobAdapter</c> перевірку
    /// <c>Cancelled</c> перед стартом → задача виконується (<c>Calls = 1</c>), а
    /// <c>Begin</c> переписує <c>Cancelled</c> на <c>Running</c>, червоний (прогнано).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Скасована_з_іншого_інстанса_задача_з_черги_не_стартує()
    {
        var factoryA = Factory();
        var factoryB = Factory();
        var quartzA = await factoryA.GetScheduler();
        var quartzB = await factoryB.GetScheduler();
        try
        {
            await using var db = sql.CreateContext();
            var jobId = await Jobs(factoryA, db, Now).EnqueueAsync<BlockingJob>(null, CancellationToken.None);

            await Jobs(factoryB, db, Now).CancelAsync(jobId, CancellationToken.None);
            Assert.Equal("Cancelled", await StateAsync(jobId));

            // Настав час задачі на A.
            var job = new BlockingJob();
            await using var provider = Provider(job, Now);
            await new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance)
                .Execute(await ContextAsync(quartzA, new JobKey(jobId)));

            Assert.Equal(0, job.Calls);
            Assert.Equal("Cancelled", await StateAsync(jobId));
            Assert.False(await quartzA.CheckExists(new JobKey(jobId)));
        }
        finally
        {
            await quartzA.Shutdown(waitForJobsToComplete: false);
            await quartzB.Shutdown(waitForJobsToComplete: false);
        }
    }

    /// <remarks>
    /// Скасування з ІНШОГО інстанса задачі, що ВИКОНУЄТЬСЯ: сигналом є рядок
    /// <c>Cancelled</c>, биття серця (тут — 200 мс) його бачить. Мутація:
    /// прибрати реакцію на неактивний рядок у <c>HeartbeatLoopAsync</c> →
    /// задача добігає 20 с і пише <c>Succeeded</c>, червоний (прогнано).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Скасування_з_іншого_інстанса_зупиняє_задачу_що_виконується()
    {
        var factoryA = Factory();
        var factoryB = Factory();
        var quartzA = await factoryA.GetScheduler();
        var quartzB = await factoryB.GetScheduler();
        try
        {
            await using var db = sql.CreateContext();
            var jobId = await Jobs(factoryA, db, Now).EnqueueAsync<BlockingJob>(null, CancellationToken.None);

            var job = new BlockingJob();
            await using var provider = Provider(job, Now, heartbeat: TimeSpan.FromMilliseconds(200));
            var adapter = new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance);
            var context = await ContextAsync(quartzA, new JobKey(jobId));

            var watch = Stopwatch.StartNew();
            var running = Task.Run(() => adapter.Execute(context));
            await job.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("Running", await StateAsync(jobId));

            // Інстанс B задачі в пам'яті не має — лише рядок у базі.
            await Jobs(factoryB, db, Now).CancelAsync(jobId, CancellationToken.None);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(30)));

            Assert.True(job.SawCancellation, "Задача не отримала скасування.");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Зупинка забрала {watch.Elapsed}.");
            Assert.Equal("Cancelled", await StateAsync(jobId));
        }
        finally
        {
            await quartzA.Shutdown(waitForJobsToComplete: false);
            await quartzB.Shutdown(waitForJobsToComplete: false);
        }
    }

    // ── U8: зупинка хоста ───────────────────────────────────────────────

    /// <remarks>
    /// ⛔ U8. Запущений планувальник справді виконує задачу; зупинка —
    /// <c>InterruptAllAsync</c> (його кличе <c>ApplicationStopping</c>) і
    /// <c>Shutdown(waitForJobsToComplete: true)</c>, як у хості. Мутація:
    /// <c>InterruptAllAsync</c> рахує задачі, але не перериває → задача не бачить
    /// скасування, тримає зупинку ~20 с і пише <c>Succeeded</c>, червоний (прогнано).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зупинка_хоста_надсилає_скасування_задачі_що_виконується_і_стан_не_Running()
    {
        var factory = Factory();
        var quartz = await factory.GetScheduler();
        var job = new BlockingJob();
        await using var provider = Provider(job, Now);
        quartz.JobFactory = new AdapterFactory(provider);

        var shutDown = false;
        try
        {
            await using var db = sql.CreateContext();
            var jobs = Jobs(factory, db, Now);
            var jobId = await jobs.EnqueueAsync<BlockingJob>(null, CancellationToken.None);

            await quartz.Start();
            await job.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

            var watch = Stopwatch.StartNew();
            Assert.Equal(1, await jobs.InterruptAllAsync(CancellationToken.None));
            await quartz.Shutdown(waitForJobsToComplete: true);
            shutDown = true;

            Assert.True(job.SawCancellation, "Задача не отримала сигналу зупинки.");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Зупинка забрала {watch.Elapsed}.");
            Assert.Equal("Cancelled", await StateAsync(jobId));
        }
        finally
        {
            if (!shutDown)
            {
                await quartz.Shutdown(waitForJobsToComplete: false);
            }
        }
    }

    // ── P2: ретенція ────────────────────────────────────────────────────
    // (прибирання покинутого — U4, U11 — у `AbandonedWorkSweeperTests`)

    /// <remarks>
    /// Аудит P2. Мутація: прибрати умову <c>UpdatedAt &lt; межа</c> → щойно
    /// закритий прибиранням рядок із давнім биттям зникає одразу, червоний (прогнано).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ретенція_видаляє_лише_завершені_старші_за_строк()
    {
        var at = Now.AddDays(90);
        await using var db = sql.CreateContext();
        var store = new JobProgressStore(db);

        var old = $"old-{Guid.NewGuid():N}";
        var fresh = $"fresh-{Guid.NewGuid():N}";
        var active = $"active-{Guid.NewGuid():N}";
        var justSwept = $"swept-{Guid.NewGuid():N}";

        await store.StartAsync(old, "Ecr.Test.Job", at.AddDays(-40), CancellationToken.None);
        await store.FinishAsync(old, "Succeeded", null, at.AddDays(-40), CancellationToken.None);

        await store.StartAsync(fresh, "Ecr.Test.Job", at.AddDays(-10), CancellationToken.None);
        await store.FinishAsync(fresh, "Failed", "x", at.AddDays(-10), CancellationToken.None);

        await store.StartAsync(active, "Ecr.Test.Job", at.AddDays(-40), CancellationToken.None);

        // Биття застигло 40 діб тому, але закрито прибиранням СЬОГОДНІ.
        await store.StartAsync(justSwept, "Ecr.Test.Job", at.AddDays(-40), CancellationToken.None);
        await store.FinishAsync(justSwept, "Failed", "abandoned", at, CancellationToken.None);

        try
        {
            await store.PurgeFinishedAsync(at - IJobProgressStore.RetainFinishedFor, 100_000, CancellationToken.None);

            Assert.Null(await StateAsync(old));
            Assert.Equal("Failed", await StateAsync(fresh));
            Assert.Equal("Running", await StateAsync(active));
            Assert.Equal("Failed", await StateAsync(justSwept));
        }
        finally
        {
            await store.FinishAsync(active, "Succeeded", null, at, CancellationToken.None);
        }
    }

    /// <summary>Фабрика, що дає Quartz справжній адаптер із тестовими службами.</summary>
    private sealed class AdapterFactory(IServiceProvider services) : IJobFactory
    {
        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler)
            => new QuartzJobAdapter(services, NullLogger<QuartzJobAdapter>.Instance);

        public void ReturnJob(IJob job)
        {
            // Адаптер нічого не тримає.
        }
    }
}
