// tests/Ecr.Infrastructure.Tests/Jobs/QuartzDocumentFifoTests.cs
using System.Collections.Concurrent;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Quartz.Impl;
using Quartz.Spi;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ M11 (репетиція W15z, JQ-FIFO у режимі Quartz/InProcess): перерахунки одного документа стартують у порядку
/// постановки — пізніша задача не обганяє ранішу ні тоді, коли раніша чекає лок документа (відкладення), ні тоді, коли
/// обидві поставлено поспіль (та сама мілісекунда).
/// </summary>
/// <remarks>
/// Лок документа імітує задача-двійник: зайнятий — <see cref="JobDeferredException"/>, як
/// <see cref="RecalculationDocumentLock.AcquireAsync"/>. Сигнали — <see cref="TaskCompletionSource"/>, таймаут лише запобіжний.
/// МУТАЦІЯ: прибрати перевірку <see cref="QuartzDocumentFifo.EarlierPendingAsync"/> з <see cref="QuartzJobAdapter"/> —
/// <c>B</c> стартує раніше за <c>A</c> (перший тест — детерміновано), червоний.
/// </remarks>
public sealed class QuartzDocumentFifoTests : IAsyncLifetime
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(30);

    private IScheduler? quartz;

    private AdapterFactory? adapters;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (quartz is not null)
        {
            await quartz.Shutdown(waitForJobsToComplete: false);
        }
    }

    /// <summary>
    /// W15z seq 39/40: <c>A</c> (жовтень) чекає лок, який тримає <c>H</c>, і відкладається; <c>B</c> (листопад) поставлено
    /// в мить, коли лок звільнився, — без порядку він брав лок одразу, а <c>A</c> чекав свого триґера.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "M11")]
    public async Task Пізніша_задача_документа_не_обганяє_відкладену_ранішу()
    {
        // Повтор A — через секунду і за локом (до виправлення), і за порядком (після): B приходить у цю паузу.
        var job = new LockingRecalculationJob(deferDelay: TimeSpan.FromSeconds(1));
        var (provider, jobs) = await StartAsync(job, fifoDelay: TimeSpan.FromSeconds(1));
        await using var _ = provider;

        await jobs.EnqueueAsync<IRecalculationJob>(Payload(96, "H"), CancellationToken.None);
        await job.Started("H").WaitAsync(Safety);

        // A пройшов адаптер і відклався (лок зайнятий або раніша H ще виконується) — перший завершений прогін (H ще йде).
        await jobs.EnqueueAsync<IRecalculationJob>(Payload(96, "A"), CancellationToken.None);
        await adapters!.Returned(1).WaitAsync(Safety);

        job.Release("H");
        await job.Finished("H").WaitAsync(Safety);
        await jobs.EnqueueAsync<IRecalculationJob>(Payload(96, "B"), CancellationToken.None);

        await Task.WhenAll(job.Finished("A"), job.Finished("B")).WaitAsync(Safety);

        Assert.Equal("H,A,B", string.Join(",", job.StartOrder(96)));
    }

    /// <summary>W15z seq 29/30: дві постановки поспіль (та сама мілісекунда) — старт у порядку постановки, на кожному документі.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "M11")]
    public async Task Дві_постановки_поспіль_стартують_у_порядку_постановки()
    {
        var job = new LockingRecalculationJob(deferDelay: TimeSpan.FromMilliseconds(300), hold: TimeSpan.FromMilliseconds(30));
        var (provider, jobs) = await StartAsync(job);
        await using var _ = provider;

        var documents = Enumerable.Range(1, 8).Select(d => (long)d).ToList();
        foreach (var document in documents)
        {
            await jobs.EnqueueAsync<IRecalculationJob>(Payload(document, "A"), CancellationToken.None);
            await jobs.EnqueueAsync<IRecalculationJob>(Payload(document, "B"), CancellationToken.None);
        }

        await Task.WhenAll(documents.SelectMany(d => new[] { job.Finished("A", d), job.Finished("B", d) })).WaitAsync(Safety);

        foreach (var document in documents)
        {
            Assert.Equal("A,B", string.Join(",", job.StartOrder(document)));
        }
    }

    /// <summary>Задача, що впала остаточно (без триґера), і задача іншого документа нікого не тримають.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "M11")]
    public async Task Раніша_без_триґера_й_інший_документ_не_тримають()
    {
        var job = new LockingRecalculationJob(deferDelay: TimeSpan.FromSeconds(1));
        var (provider, jobs) = await StartAsync(job);
        await using var _ = provider;

        // Дурабельна деталь без триґера — як задача, що вичерпала ретраї.
        var dead = JobBuilder.Create<QuartzJobAdapter>()
            .WithIdentity($"{nameof(IRecalculationJob)}-{Guid.NewGuid():N}")
            .UsingJobData(QuartzDocumentFifo.DocumentKey, 5L)
            .UsingJobData(QuartzDocumentFifo.SequenceKey, 0L)
            .StoreDurably()
            .Build();
        await quartz!.AddJob(dead, replace: false);

        await jobs.EnqueueAsync<IRecalculationJob>(Payload(6, "H"), CancellationToken.None);
        await job.Started("H", 6).WaitAsync(Safety);

        await jobs.EnqueueAsync<IRecalculationJob>(Payload(5, "A"), CancellationToken.None);
        await job.Finished("A", 5).WaitAsync(Safety);

        Assert.Equal(0, job.DeferredCount("A", 5));
        job.Release("H", 6);
        await job.Finished("H", 6).WaitAsync(Safety);
    }

    /// <summary>
    /// ✎ Огляд b11 (MEDIUM-3): перевірка — голова індексу документа, без перебору ключів планувальника й без
    /// <c>GetJobDetail</c> на кожен; голова, що вже не чекає (без триґера й не виконується), випадає, і береться наступна.
    /// </summary>
    /// <remarks>Мутації: <c>Track</c> — порожній (раніша не знайдена) — червоний; не викидати мертву голову — червоний (зависання).</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "M11")]
    public async Task Перевірка_бере_голову_індексу_без_перебору_ключів()
    {
        var scheduler = Substitute.For<IScheduler>();
        scheduler.SchedulerName.Returns($"ecr-fifo-index-{Guid.NewGuid():N}");
        scheduler.GetCurrentlyExecutingJobs(Arg.Any<CancellationToken>()).Returns(Array.Empty<IJobExecutionContext>());

        var document = Random.Shared.NextInt64(1_000_000, 9_000_000);
        IJobDetail Detail(string id, long sequence) => JobBuilder.Create<QuartzJobAdapter>()
            .WithIdentity(id)
            .UsingJobData(QuartzDocumentFifo.DocumentKey, document)
            .UsingJobData(QuartzDocumentFifo.SequenceKey, sequence)
            .StoreDurably()
            .Build();

        var dead = Detail("IRecalculationJob-dead", 1);
        var alive = Detail("IRecalculationJob-alive", 2);
        var me = Detail("IRecalculationJob-me", 3);
        foreach (var detail in new[] { dead, alive, me })
        {
            QuartzDocumentFifo.Track(scheduler.SchedulerName, detail);
        }

        scheduler.GetTriggersOfJob(dead.Key, Arg.Any<CancellationToken>()).Returns(Array.Empty<ITrigger>());
        scheduler.GetTriggersOfJob(alive.Key, Arg.Any<CancellationToken>())
            .Returns(new[] { TriggerBuilder.Create().ForJob(alive.Key).StartNow().Build() });

        Assert.Equal(alive.Key.Name, await QuartzDocumentFifo.EarlierPendingAsync(scheduler, me, CancellationToken.None).WaitAsync(Safety));
        Assert.Null(await QuartzDocumentFifo.EarlierPendingAsync(scheduler, dead, CancellationToken.None));

        await scheduler.DidNotReceive().GetJobKeys(Arg.Any<Quartz.Impl.Matchers.GroupMatcher<JobKey>>(), Arg.Any<CancellationToken>());
        await scheduler.DidNotReceive().GetJobDetail(Arg.Any<JobKey>(), Arg.Any<CancellationToken>());

        // Мертва голова випала з індексу; живу знято — далі нікого.
        QuartzDocumentFifo.Forget(scheduler.SchedulerName, alive.Key.Name);
        Assert.Null(await QuartzDocumentFifo.EarlierPendingAsync(scheduler, me, CancellationToken.None).WaitAsync(Safety));
        QuartzDocumentFifo.Forget(scheduler.SchedulerName, me.Key.Name);
    }
    private async Task<(ServiceProvider Provider, QuartzJobScheduler Jobs)> StartAsync(LockingRecalculationJob job, TimeSpan? fifoDelay = null)
    {
        var factory = new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-tests-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "8",
        });
        quartz = await factory.GetScheduler();

        var services = new ServiceCollection();
        services.AddSingleton<Ecr.Domain.Abstractions.IClock, SystemClock>();
        services.AddSingleton<IRecalculationJob>(job);
        services.AddSingleton(new QuartzDocumentFifoSettings(fifoDelay ?? TimeSpan.FromMilliseconds(50)));

        var provider = services.BuildServiceProvider();
        adapters = new AdapterFactory(provider);
        quartz.JobFactory = adapters;
        await quartz.Start();

        return (provider, new QuartzJobScheduler(factory, null, provider.GetRequiredService<Ecr.Domain.Abstractions.IClock>()));
    }

    private static object Payload(long documentId, string name) => new { documentId, periodKey = 202610, name };

    /// <summary>Створює адаптери й рахує ЗАВЕРШЕНІ прогони (кожен триґер, зокрема відкладений).</summary>
    private sealed class AdapterFactory(IServiceProvider provider) : IJobFactory
    {
        private readonly object gate = new();
        private readonly List<(int Count, TaskCompletionSource Signal)> waits = [];
        private int returned;

        public Task Returned(int count)
        {
            lock (gate)
            {
                if (returned >= count)
                {
                    return Task.CompletedTask;
                }

                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waits.Add((count, signal));
                return signal.Task;
            }
        }

        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler)
            => new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance);

        public void ReturnJob(IJob job)
        {
            lock (gate)
            {
                returned++;
                foreach (var (count, signal) in waits.Where(w => returned >= w.Count))
                {
                    signal.TrySetResult();
                }
            }
        }
    }

    /// <summary>Перерахунок-двійник: лок документа в пам'яті; зайнятий — відкладення, як справжній.</summary>
    private sealed class LockingRecalculationJob(TimeSpan deferDelay, TimeSpan? hold = null) : IRecalculationJob
    {
        private readonly ConcurrentDictionary<long, SemaphoreSlim> locks = new();
        private readonly ConcurrentDictionary<long, ConcurrentQueue<string>> order = new();
        private readonly ConcurrentDictionary<(string, long), TaskCompletionSource> started = new();
        private readonly ConcurrentDictionary<(string, long), TaskCompletionSource> finished = new();
        private readonly ConcurrentDictionary<(string, long), TaskCompletionSource> deferred = new();
        private readonly ConcurrentDictionary<(string, long), TaskCompletionSource> release = new();
        private readonly ConcurrentDictionary<(string, long), int> deferrals = new();

        public Task Started(string name, long document = 96) => Signal(started, name, document).Task;

        public Task Finished(string name, long document = 96) => Signal(finished, name, document).Task;

        public Task Deferred(string name, long document = 96) => Signal(deferred, name, document).Task;

        public void Release(string name, long document = 96) => Signal(release, name, document).TrySetResult();

        public int DeferredCount(string name, long document) => deferrals.GetValueOrDefault((name, document));

        public IReadOnlyList<string> StartOrder(long document) => [.. order.GetOrAdd(document, _ => new())];

        public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(payload?.ToString() ?? "{}");
            var document = body.RootElement.GetProperty("documentId").GetInt64();
            var name = body.RootElement.GetProperty("name").GetString()!;

            var gate = locks.GetOrAdd(document, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
            {
                deferrals.AddOrUpdate((name, document), 1, (_, n) => n + 1);
                Signal(deferred, name, document).TrySetResult();
                throw new JobDeferredException(deferDelay, $"Документ {document} перераховує інша задача.");
            }

            try
            {
                order.GetOrAdd(document, _ => new()).Enqueue(name);
                Signal(started, name, document).TrySetResult();

                if (name == "H")
                {
                    await Signal(release, name, document).Task.WaitAsync(ct).ConfigureAwait(false);
                }
                else if (hold is { } wait)
                {
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
                Signal(finished, name, document).TrySetResult();
            }
        }

        private static TaskCompletionSource Signal(
            ConcurrentDictionary<(string, long), TaskCompletionSource> map, string name, long document)
            => map.GetOrAdd((name, document), _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
