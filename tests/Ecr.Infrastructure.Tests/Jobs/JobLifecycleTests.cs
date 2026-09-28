// tests/Ecr.Infrastructure.Tests/Jobs/JobLifecycleTests.cs
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
/// ⛔ Швидкість скасування доводиться НЕ секундоміром. Задача тут
/// (<see cref="BlockingJob"/>) без скасування висить НЕСКІНЧЕННО, тож межа
/// <see cref="StopWithin"/> на <c>WaitAsync</c> — це «зупинилась узагалі», а
/// не «встигла під навантаженням»: секундомір на 10 с червонів на
/// завантаженій машині при цілому коді, а дефект без межі перетворив би тест
/// на зависання замість червоного (<c>TimeoutException</c>).
/// </para>
/// <para>
/// ⚠ Моменти — у 2041 році: <c>AbandonedWorkSweeperTests</c> (2031-03-01) і
/// <c>JobInstanceSweepTests</c> (2031-03-05) прибирають КОЖЕН застарілий
/// активний рядок спільної бази, і рядки цього класу в 2031 році потрапляли
/// під їхній поріг. Ретенція — окрема, давня епоха (<see cref="PurgeEpoch"/>),
/// щоб її глобальне видалення досягало лише рядків цього тесту.
/// </para>
/// <para>
/// Мутаційні докази — у коментарях тестів; прогін і результат — в описі коміту.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class JobLifecycleTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2041, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Епоха тесту ретенції: раніша за будь-який момент інших тестів, тож межа
    /// <c>PurgeFinishedAsync</c> під нею досягає лише власних рядків.
    /// </summary>
    private static readonly DateTime PurgeEpoch = new(2001, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Межа «задача зупинилась». Не міра швидкості: без скасування задача не
    /// зупиняється ніколи, тож перевищення — це дефект, а не повільна машина.
    /// </summary>
    private static readonly TimeSpan StopWithin = TimeSpan.FromSeconds(15);

    /// <summary>Скільки чекати, поки ВИКОНАВЕЦЬ узагалі візьме задачу (не предмет тестів).</summary>
    private static readonly TimeSpan StartWithin = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Задача, що чекає скасування НЕСКІНЧЕННО — «довгий перерахунок».
    /// </summary>
    /// <remarks>
    /// ⚠ <see cref="Release"/> — лише прибирання в <c>finally</c>: якщо дефект
    /// лишив задачу висіти, тест уже червоний за <see cref="StopWithin"/>, а
    /// звільнена задача не тягне потік пулу й запис у базу в наступний тест.
    /// </remarks>
    private sealed class BlockingJob : IBackgroundJob
    {
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool SawCancellation { get; private set; }

        public int Calls { get; private set; }

        public void Release() => released.TrySetResult();

        public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        {
            Calls++;
            Started.TrySetResult();

            try
            {
                await released.Task.WaitAsync(ct);
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
    private static QuartzJobScheduler Jobs(ISchedulerFactory factory, EcrDbContext db, DateTime at)
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

    /// <summary>
    /// Чекає завершення виконання в межах <see cref="StopWithin"/>; зависання —
    /// червоне з <see cref="TimeoutException"/>, а не зависання прогону.
    /// </summary>
    private static async Task<Exception?> StoppedAsync(Task running)
    {
        var outcome = await Record.ExceptionAsync(() => running.WaitAsync(StopWithin));

        if (outcome is TimeoutException)
        {
            throw new TimeoutException(
                $"Задача не зупинилась за {StopWithin.TotalSeconds} с: скасування до неї не дійшло.", outcome);
        }

        return outcome;
    }

    /// <summary>Звільняє задачу, якщо дефект лишив її висіти, і дочікується виходу.</summary>
    private static async Task ReleaseAsync(BlockingJob job, Task? running)
    {
        job.Release();

        if (running is not null)
        {
            await Record.ExceptionAsync(() => running.WaitAsync(StopWithin));
        }
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
    /// <c>Cancelled</c> перед стартом → задача стартує й висить (скасування до
    /// неї не йде), <c>TimeoutException</c> за <see cref="StopWithin"/>, червоний (прогнано).
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
        var job = new BlockingJob();
        await using var provider = Provider(job, Now);
        Task? running = null;
        try
        {
            await using var db = sql.CreateContext();
            var jobId = await Jobs(factoryA, db, Now).EnqueueAsync<BlockingJob>(null, CancellationToken.None);

            await Jobs(factoryB, db, Now).CancelAsync(jobId, CancellationToken.None);
            Assert.Equal("Cancelled", await StateAsync(jobId));

            // Настав час задачі на A.
            var context = await ContextAsync(quartzA, new JobKey(jobId));
            running = new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance).Execute(context);
            Assert.Null(await StoppedAsync(running));

            Assert.Equal(0, job.Calls);
            Assert.Equal("Cancelled", await StateAsync(jobId));
            Assert.False(await quartzA.CheckExists(new JobKey(jobId)));
        }
        finally
        {
            await ReleaseAsync(job, running);
            await quartzA.Shutdown(waitForJobsToComplete: false);
            await quartzB.Shutdown(waitForJobsToComplete: false);
        }
    }

    /// <remarks>
    /// Скасування з ІНШОГО інстанса задачі, що ВИКОНУЄТЬСЯ: сигналом є рядок
    /// <c>Cancelled</c>, биття серця (тут — 200 мс) його бачить. Мутація:
    /// прибрати реакцію на неактивний рядок у <c>HeartbeatLoopAsync</c> →
    /// задача висить, <c>TimeoutException</c> за <see cref="StopWithin"/>, червоний (прогнано).
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
        var job = new BlockingJob();
        await using var provider = Provider(job, Now, heartbeat: TimeSpan.FromMilliseconds(200));
        Task? running = null;
        try
        {
            await using var db = sql.CreateContext();
            var jobId = await Jobs(factoryA, db, Now).EnqueueAsync<BlockingJob>(null, CancellationToken.None);

            var adapter = new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance);
            var context = await ContextAsync(quartzA, new JobKey(jobId));

            running = Task.Run(() => adapter.Execute(context));
            await job.Started.Task.WaitAsync(StartWithin);
            Assert.Equal("Running", await StateAsync(jobId));

            // Інстанс B задачі в пам'яті не має — лише рядок у базі.
            await Jobs(factoryB, db, Now).CancelAsync(jobId, CancellationToken.None);

            Assert.IsAssignableFrom<OperationCanceledException>(await StoppedAsync(running));
            Assert.True(job.SawCancellation, "Задача не отримала скасування.");
            Assert.Equal("Cancelled", await StateAsync(jobId));
        }
        finally
        {
            await ReleaseAsync(job, running);
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
    /// скасування, <c>Shutdown</c> чекає її вічно — <c>TimeoutException</c> за
    /// <see cref="StopWithin"/>, червоний, а не зависання прогону (прогнано).
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

        Task? stopping = null;
        try
        {
            await using var db = sql.CreateContext();
            var jobs = Jobs(factory, db, Now);
            var jobId = await jobs.EnqueueAsync<BlockingJob>(null, CancellationToken.None);

            await quartz.Start();
            await job.Started.Task.WaitAsync(StartWithin);

            Assert.Equal(1, await jobs.InterruptAllAsync(CancellationToken.None));

            // `Shutdown` чекає задачу; без сигналу вона не завершиться ніколи.
            stopping = quartz.Shutdown(waitForJobsToComplete: true);
            Assert.Null(await StoppedAsync(stopping));

            Assert.True(job.SawCancellation, "Задача не отримала сигналу зупинки.");
            Assert.Equal("Cancelled", await StateAsync(jobId));
        }
        finally
        {
            // Задачу звільнено — дочікування зупинки обмежене й не тягне її в
            // наступний тест (провайдер нижче вже буде звільнено).
            job.Release();
            stopping ??= quartz.Shutdown(waitForJobsToComplete: true);
            await Record.ExceptionAsync(() => stopping.WaitAsync(StopWithin));
        }
    }

    // ── P2: ретенція ────────────────────────────────────────────────────
    // (прибирання покинутого — U4, U11 — у `AbandonedWorkSweeperTests`)

    /// <remarks>
    /// Аудит P2. Мутація: прибрати умову <c>UpdatedAt &lt; межа</c> → щойно
    /// закритий прибиранням рядок із давнім биттям зникає одразу, червоний (прогнано).
    /// <para>
    /// ⚠ <c>PurgeFinishedAsync</c> глобальний за задумом, тож межа тут — у
    /// <see cref="PurgeEpoch"/>: під нею лише рядки цього тесту, а не завершені
    /// рядки сусідніх класів. Свої рядки видаляються в <c>finally</c>.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ретенція_видаляє_лише_завершені_старші_за_строк()
    {
        var at = PurgeEpoch;
        await using var db = sql.CreateContext();
        var store = new JobProgressStore(db);

        var old = $"old-{Guid.NewGuid():N}";
        var fresh = $"fresh-{Guid.NewGuid():N}";
        var active = $"active-{Guid.NewGuid():N}";
        var justSwept = $"swept-{Guid.NewGuid():N}";
        string[] own = [old, fresh, active, justSwept];

        try
        {
            await store.StartAsync(old, "Ecr.Test.Job", at.AddDays(-40), CancellationToken.None);
            await store.FinishAsync(old, "Succeeded", null, at.AddDays(-40), CancellationToken.None);

            await store.StartAsync(fresh, "Ecr.Test.Job", at.AddDays(-10), CancellationToken.None);
            await store.FinishAsync(fresh, "Failed", "x", at.AddDays(-10), CancellationToken.None);

            await store.StartAsync(active, "Ecr.Test.Job", at.AddDays(-40), CancellationToken.None);

            // Биття застигло 40 діб тому, але закрито прибиранням СЬОГОДНІ.
            await store.StartAsync(justSwept, "Ecr.Test.Job", at.AddDays(-40), CancellationToken.None);
            await store.FinishAsync(justSwept, "Failed", "abandoned", at, CancellationToken.None);

            await store.PurgeFinishedAsync(at - IJobProgressStore.RetainFinishedFor, 100_000, CancellationToken.None);

            Assert.Null(await StateAsync(old));
            Assert.Equal("Failed", await StateAsync(fresh));
            Assert.Equal("Running", await StateAsync(active));
            Assert.Equal("Failed", await StateAsync(justSwept));
        }
        finally
        {
            await db.JobProgresses.Where(p => own.Contains(p.JobId)).ExecuteDeleteAsync();
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
