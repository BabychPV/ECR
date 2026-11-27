// tests/Ecr.Infrastructure.Tests/Jobs/QuartzDelayedRecoveryTests.cs
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Quartz.Impl;
using Quartz.Spi;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ D-582: відкладена постановка Quartz (автоперерахунок після правки, ланки джерел), що чекала вікна злиття, переживає
/// перезапуск процесу — новий процес на тій самій базі ставить її знову РІВНО раз зі злитим тілом; виконана, злита чи знята
/// постановка не повторюється.
/// </summary>
/// <remarks>
/// МУТАЦІЇ (локально): (1) <c>QuartzDelayedJobRecovery.RecoverAsync</c> одразу <c>return 0</c> — червоний «перезапуск»;
/// (2) у <c>QuartzJobScheduler.EnqueueCoreAsync</c> прибрати <c>delayed.SaveAsync</c> — червоний «перезапуск»; (3) у
/// <c>CoalesceAsync</c> прибрати <c>PersistMergeAsync</c> після злиття — червоний «перезапуск» (тіло першої постановки);
/// (4) у <c>ClaimOrphansAsync</c> прибрати перше <c>DELETE</c> (виконані) — червоний «виконана не повторюється».
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "D-582")]
public sealed class QuartzDelayedRecoveryTests(SqlServerFixture sql) : IAsyncLifetime
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(30);

    private static readonly int[] MonthsA = [3];

    private static readonly int[] MonthsB = [5];

    private static readonly int[] SheetsA = [1, 2];

    private static readonly int[] SheetsB = [2, 3];

    private readonly List<IScheduler> schedulers = [];

    /// <summary>Таблицю пишуть лише ці тести (колекція <c>SqlServer</c> — послідовно): чистий старт кожного.</summary>
    public async Task InitializeAsync()
    {
        await using var db = Context();
        await db.PendingDelayedJobs.ExecuteDeleteAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var scheduler in schedulers)
        {
            await scheduler.Shutdown(waitForJobsToComplete: false);
        }
    }

    [Fact]
    public async Task Перезапуск_у_вікні_злиття_відновлює_задачу_один_раз_зі_злитим_тілом()
    {
        var document = Random.Shared.NextInt64(1_000_000_000, 9_000_000_000);
        var target = CalculationTrigger.AutoTargetOf(document, new PeriodKey(202610));

        // Процес 1: дві правки в межах вікна — одна задача, що чекає; годинник процесу на дві години позаду, тож строк уже минув.
        var factory1 = Factory();
        var quartz1 = await factory1.GetScheduler();
        schedulers.Add(quartz1);
        var clock1 = new FixedClock(DateTime.UtcNow.AddHours(-2));
        string first;
        await using (var db = Context())
        {
            var jobs1 = new QuartzJobScheduler(
                factory1, new JobProgressStore(db), clock1, delayed: new QuartzDelayedJobStore(db));
            first = await jobs1.EnqueueCoalescedDelayedAsync<IRecalculationJob>(
                target,
                new { DocumentId = document, PeriodKey = 202610, SourceMonths = MonthsA, SourceHop = 2, SkipIfFresh = true, ApproverSheets = SheetsA },
                CalculationTrigger.EditDebounce,
                CancellationToken.None);
            Assert.Equal(first, await jobs1.EnqueueCoalescedDelayedAsync<IRecalculationJob>(
                target,
                new { DocumentId = document, PeriodKey = 202610, SourceMonths = MonthsB, SourceHop = 1, ApproverSheets = SheetsB },
                CalculationTrigger.EditDebounce,
                CancellationToken.None));
        }

        // Процес 1 зник, не стартувавши задачі; прибирання на старті процесу 2 закрило його рядок-дзеркало (Failed, без спроби).
        await quartz1.Shutdown(waitForJobsToComplete: false);
        await SweepAsync(first);

        // Процес 2 на тій самій базі.
        var (jobs2, job) = await StartedAsync();
        await using (var db = Context())
        {
            Assert.Equal(1, await QuartzDelayedJobRecovery.RecoverAsync(
                new QuartzDelayedJobStore(db), jobs2(db), new SystemClock(), null, CancellationToken.None));
        }

        var payload = JsonDocument.Parse(await job.First.Task.WaitAsync(Safety)).RootElement;
        Assert.Equal(document, payload.GetProperty("documentId").GetInt64());
        Assert.Equal("3,5", string.Join(',', payload.GetProperty("sourceMonths").EnumerateArray().Select(e => e.GetInt32())));
        Assert.Equal(1, payload.GetProperty("sourceHop").GetInt32());
        Assert.Equal("2", string.Join(',', payload.GetProperty("approverSheets").EnumerateArray().Select(e => e.GetInt32())));
        Assert.False(payload.TryGetProperty("skipIfFresh", out var skip) && skip.GetBoolean());

        // Другий старт (чи сусідній інстанс) нічого не повторює.
        await using (var db = Context())
        {
            Assert.Equal(0, await QuartzDelayedJobRecovery.RecoverAsync(
                new QuartzDelayedJobStore(db), jobs2(db), new SystemClock(), null, CancellationToken.None));
            Assert.False(await db.PendingDelayedJobs.AnyAsync(p => p.JobId == first));
        }

        await Task.Delay(500);
        Assert.Equal(1, job.Runs);
    }

    [Fact]
    public async Task Виконана_знята_чи_жива_постановка_не_повторюється()
    {
        var (jobs, job) = await StartedAsync();
        var clock = new SystemClock();

        // (а) Виконана в цьому процесі: слід знято на старті задачі.
        var document = Random.Shared.NextInt64(1_000_000_000, 9_000_000_000);
        string done;
        await using (var db = Context())
        {
            done = await jobs(db).EnqueueCoalescedDelayedAsync<IRecalculationJob>(
                CalculationTrigger.AutoTargetOf(document, new PeriodKey(202610)),
                new { DocumentId = document, PeriodKey = 202610 },
                TimeSpan.FromMilliseconds(200),
                CancellationToken.None);
            Assert.True(await db.PendingDelayedJobs.AnyAsync(p => p.JobId == done));
        }

        await job.First.Task.WaitAsync(Safety);
        await using (var db = Context())
        {
            Assert.False(await db.PendingDelayedJobs.AnyAsync(p => p.JobId == done));
        }

        // (б) Знята з черги (скасування на ціль): слід знято.
        var cancelledDocument = Random.Shared.NextInt64(1_000_000_000, 9_000_000_000);
        var cancelledTarget = CalculationTrigger.AutoTargetOf(cancelledDocument, new PeriodKey(202610));
        string cancelled;
        await using (var db = Context())
        {
            cancelled = await jobs(db).EnqueueCoalescedDelayedAsync<IRecalculationJob>(
                cancelledTarget, new { DocumentId = cancelledDocument, PeriodKey = 202610 }, TimeSpan.FromHours(1), CancellationToken.None);
            Assert.True(await jobs(db).CancelQueuedAsync<IRecalculationJob>(cancelledTarget, null, CancellationToken.None));
            Assert.False(await db.PendingDelayedJobs.AnyAsync(p => p.JobId == cancelled));
        }

        // (в) Слід, що лишився від задачі, яка вже стартувала й завершилась (збій видалення), — видаляється без повтору;
        // (г) слід живої задачі іншого інстанса (дзеркало Queued) — не чіпається.
        var stale = $"{nameof(IRecalculationJob)}~doc{document}-p202611-auto~{Guid.NewGuid():N}";
        var alive = $"{nameof(IRecalculationJob)}~doc{document}-p202612-auto~{Guid.NewGuid():N}";
        await using (var db = Context())
        {
            var progress = new JobProgressStore(db);
            var store = new QuartzDelayedJobStore(db);
            foreach (var id in new[] { stale, alive })
            {
                await progress.QueueAsync(id, typeof(IRecalculationJob).FullName!, clock.UtcNow, CancellationToken.None);
                await store.SaveAsync(
                    id, typeof(IRecalculationJob).FullName!, $"{{\"documentId\":{document}}}", clock.UtcNow.AddHours(-1), null,
                    clock.UtcNow, CancellationToken.None);
            }

            await db.JobProgresses.Where(p => p.JobId == stale)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.State, "Succeeded").SetProperty(p => p.Attempt, 1));

            Assert.Equal(0, await QuartzDelayedJobRecovery.RecoverAsync(store, jobs(db), clock, null, CancellationToken.None));
            Assert.False(await db.PendingDelayedJobs.AnyAsync(p => p.JobId == stale));
            Assert.True(await db.PendingDelayedJobs.AnyAsync(p => p.JobId == alive));
            await store.DeleteAsync(alive, CancellationToken.None);
        }

        await Task.Delay(500);
        Assert.Equal(1, job.Runs);
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    /// <summary>Те, що лишає прибирання на старті для черги процесу, що зник: <c>Failed</c>, спроби не було.</summary>
    private async Task SweepAsync(string jobId)
    {
        await using var db = Context();
        Assert.Equal(1, await db.JobProgresses.Where(p => p.JobId == jobId && p.State == "Queued" && p.Attempt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.State, "Failed")));
    }

    /// <summary>Запущений планувальник «процесу 2» з адаптером, що резолвить слід відкладених постановок.</summary>
    private async Task<(Func<EcrDbContext, QuartzJobScheduler> Jobs, RecordingRecalculationJob Job)> StartedAsync()
    {
        var job = new RecordingRecalculationJob();
        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IRecalculationJob>(job);
        services.AddScoped(_ => Context());
        services.AddScoped<QuartzDelayedJobStore>();
        var provider = services.BuildServiceProvider();

        var factory = Factory();
        var quartz = await factory.GetScheduler();
        schedulers.Add(quartz);
        quartz.JobFactory = new AdapterFactory(provider);
        await quartz.Start();

        return (db => new QuartzJobScheduler(factory, new JobProgressStore(db), new SystemClock(), delayed: new QuartzDelayedJobStore(db)), job);
    }

    private static StdSchedulerFactory Factory()
        => new(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-d582-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "2",
        });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class AdapterFactory(IServiceProvider provider) : IJobFactory
    {
        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler)
            => new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance);

        public void ReturnJob(IJob job)
        {
        }
    }

    private sealed class RecordingRecalculationJob : IRecalculationJob
    {
        private int runs;

        public int Runs => Volatile.Read(ref runs);

        public ConcurrentQueue<string> Payloads { get; } = new();

        public TaskCompletionSource<string> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        {
            Interlocked.Increment(ref runs);
            var body = payload?.ToString() ?? string.Empty;
            Payloads.Enqueue(body);
            First.TrySetResult(body);
            return Task.CompletedTask;
        }
    }
}
