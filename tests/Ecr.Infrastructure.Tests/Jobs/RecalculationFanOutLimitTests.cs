// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationFanOutLimitTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Quartz.Impl;
using Quartz.Spi;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// F1 (WW-02, паритет-доказ 13.10): після переносу версії розклад перерахунку проєкту на 91 документ ставив 91
/// задач, пул Quartz (16 потоків) брав їх паралельно, і 70 із 91 падали <c>ECR-DOC-4091</c>. Діти розкладу
/// (<see cref="RecalculationJob.IsFanOutChild"/>) мають межу <see cref="QuartzJobTypeLimiter.RecalcFanOutMaxConcurrency"/>
/// з FIFO-чергою; ручний перерахунок і інкрементний після PATCH її не бачать.
/// </summary>
/// <remarks>
/// Без реального часу: сигнали — <see cref="TaskCompletionSource"/>, таймаут лише запобіжний.
/// МУТАЦІЇ: (1) у <see cref="QuartzJobAdapter"/> передати <c>recalcFanOutChild: false</c> — тест адаптера
/// червоний (<c>MaxConcurrent</c> = 16); (2) у <c>TryEnter</c> прибрати гілку <c>recalcFanOutChild</c> —
/// червоні обидва.
/// </remarks>
public sealed class RecalculationFanOutLimitTests : IAsyncLifetime
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(60);

    private IScheduler? quartz;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (quartz is not null)
        {
            await quartz.Shutdown(waitForJobsToComplete: false);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "F1")]
    public void Діти_розкладу_не_перевищують_межу_а_ручний_перерахунок_і_PATCH_не_стоять_за_ними()
    {
        var limiter = new QuartzJobTypeLimiter(2, recalcFanOutMaxConcurrency: 4);

        var held = new List<IDisposable>();
        for (var i = 0; i < 4; i++)
        {
            var slot = limiter.TryEnter(typeof(RecalculationJob), $"child-{i}", recalcFanOutChild: true);
            Assert.NotNull(slot);
            held.Add(slot!);
        }

        Assert.Equal(4, limiter.RecalcFanOutRunning);
        Assert.Null(limiter.TryEnter(typeof(RecalculationJob), "child-4", recalcFanOutChild: true));
        Assert.Equal(1, limiter.RecalcFanOutWaiting);

        // Ручний «Перерахувати» документа (без позначки батька) і інкрементний після PATCH — поза межею.
        using var manual = limiter.TryEnter(typeof(RecalculationJob), "manual", recalcFanOutChild: false);
        using var patch = limiter.TryEnter(typeof(FormulaRecalculationJob), "patch", recalcFanOutChild: false);
        Assert.NotNull(manual);
        Assert.NotNull(patch);
        Assert.Equal(4, limiter.RecalcFanOutRunning);

        // Межа Excel окрема: її місця діти розкладу не займають.
        using var excel = limiter.TryEnter(typeof(ExcelExportJob), "book", recalcFanOutChild: false);
        Assert.NotNull(excel);
        Assert.Equal(1, limiter.ExcelRunning);

        // Звільнене місце дістається голові черги (FIFO), а не тому, хто прийшов пізніше.
        held[0].Dispose();
        Assert.Null(limiter.TryEnter(typeof(RecalculationJob), "child-5", recalcFanOutChild: true));
        using var head = limiter.TryEnter(typeof(RecalculationJob), "child-4", recalcFanOutChild: true);
        Assert.NotNull(head);

        foreach (var slot in held)
        {
            slot.Dispose();
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "F1")]
    public void Двадцять_дітей_розкладу_ніколи_не_виконуються_понад_межу_і_всі_завершуються()
    {
        const int Children = 24;
        const int Limit = 4;
        var limiter = new QuartzJobTypeLimiter(2, recalcFanOutMaxConcurrency: Limit);

        var pending = Enumerable.Range(0, Children).Select(i => $"doc-{i}").ToList();
        var running = new List<(string Id, IDisposable Slot)>();
        var finishedOrder = new List<string>();
        var maxRunning = 0;

        // Тік: кожна ще не завершена задача пробує вхід (як відкладені триґери), далі завершується найстаріша
        // з виконуваних - детермінований заміщувач «потік пулу відпрацював».
        for (var guard = 0; guard < 1_000 && finishedOrder.Count < Children; guard++)
        {
            foreach (var id in pending.ToList())
            {
                var slot = limiter.TryEnter(typeof(RecalculationJob), id, recalcFanOutChild: true);
                if (slot is null)
                {
                    continue;
                }

                running.Add((id, slot));
                pending.Remove(id);
                maxRunning = Math.Max(maxRunning, limiter.RecalcFanOutRunning);
                Assert.True(limiter.RecalcFanOutRunning <= Limit, "одночасних дітей розкладу більше за межу");
            }

            if (running.Count > 0)
            {
                var (id, slot) = running[0];
                running.RemoveAt(0);
                slot.Dispose();
                finishedOrder.Add(id);
            }
        }

        Assert.Equal(Children, finishedOrder.Count);
        Assert.Equal(Limit, maxRunning);
        Assert.Equal(0, limiter.RecalcFanOutRunning);
        Assert.Equal(0, limiter.RecalcFanOutWaiting);

        // FIFO: порядок завершення збігається з порядком першої появи.
        Assert.Equal(Enumerable.Range(0, Children).Select(i => $"doc-{i}"), finishedOrder);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "F1")]
    [InlineData(null, QuartzJobTypeLimiter.DefaultRecalcFanOutMaxConcurrency)]
    [InlineData("", QuartzJobTypeLimiter.DefaultRecalcFanOutMaxConcurrency)]
    [InlineData("8", 8)]
    [InlineData("0", QuartzJobTypeLimiter.DefaultRecalcFanOutMaxConcurrency)]
    public void Межа_дітей_розкладу_з_конфігурації(string? value, int expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { [QuartzJobTypeLimiter.RecalcFanOutMaxConcurrencyKey] = value })
            .Build();

        Assert.Equal(expected, QuartzJobTypeLimiter.ReadRecalcFanOutMaxConcurrency(configuration));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "F1")]
    [InlineData("{\"projectId\":1,\"documentId\":7,\"periodKey\":null,\"fanOutParentJobId\":\"IRecalculationJob-1\"}", true)]
    [InlineData("{\"projectId\":1,\"documentId\":7,\"periodKey\":202601}", false)]
    [InlineData("{\"projectId\":1,\"periodKey\":null,\"fanOutParentJobId\":\"IRecalculationJob-1\"}", false)]
    [InlineData("{\"projectId\":1,\"documentId\":7,\"fanOutParentJobId\":\"\"}", false)]
    [InlineData("не json", false)]
    [InlineData(null, false)]
    public void Дитину_розкладу_впізнає_лише_позначка_батька_у_документній_задачі(string? payload, bool expected)
        => Assert.Equal(expected, RecalculationJob.IsFanOutChild(typeof(RecalculationJob), payload));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "F1")]
    public void Інший_тип_задачі_з_позначкою_батька_не_є_дитиною_розкладу()
        => Assert.False(RecalculationJob.IsFanOutChild(
            typeof(FormulaRecalculationJob), "{\"documentId\":7,\"fanOutParentJobId\":\"x\"}"));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "F1")]
    public void Повтор_після_вичерпаного_блокування_довший_за_паузу_зайнятого_лока_і_розсіяний_за_документом()
    {
        var delays = Enumerable.Range(1, 10).Select(id => RecalculationDocumentLock.LockWaitDeferDelay(id)).ToList();

        Assert.All(delays, d => Assert.True(d >= RecalculationDocumentLock.LockWaitDeferBase));
        Assert.All(delays, d => Assert.True(d > RecalculationDocumentLock.DeferDelay));
        Assert.True(delays.Distinct().Count() > 5, "повтори мають розходитися, а не вдаряти в таблицю разом");
        Assert.Equal(
            RecalculationDocumentLock.LockWaitDeferDelay(91), RecalculationDocumentLock.LockWaitDeferDelay(91));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "F1")]
    public void Лише_lockTimeout_із_кодом_4091_є_очікуванням_блокування_а_не_інші_409()
    {
        static Ecr.Application.Errors.ConcurrencyConflictException Conflict(string code, string key)
            => new(code, "m", new Dictionary<string, object?> { ["messageKey"] = key });

        Assert.True(RecalculationDocumentLock.IsLockWaitTimeout(
            Conflict("ECR-DOC-4091", Ecr.Infrastructure.Persistence.LockWaitGuard.MessageKey)));

        // «Аркуш подається» і «структуру змінено» — інші відмови, розбираються окремо.
        Assert.False(RecalculationDocumentLock.IsLockWaitTimeout(
            Conflict("ECR-DOC-4091", "err.ECR-DOC-4091.sheetBeingSubmitted")));
        Assert.False(RecalculationDocumentLock.IsLockWaitTimeout(
            Conflict("ECR-DOC-0409", Ecr.Infrastructure.Persistence.LockWaitGuard.MessageKey)));
        Assert.False(RecalculationDocumentLock.IsLockWaitTimeout(new InvalidOperationException("x")));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "F1")]
    public async Task Адаптер_Quartz_не_пускає_понад_межу_дітей_розкладу_одночасно_і_довиконує_усіх()
    {
        const int Children = 20;
        var job = new CountingRecalculationJob(Children);
        var factory = new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-tests-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "16",
        });
        quartz = await factory.GetScheduler();

        var services = new ServiceCollection();
        services.AddSingleton<Ecr.Domain.Abstractions.IClock, SystemClock>();
        services.AddSingleton<IRecalculationJob>(job);
        services.AddSingleton(new QuartzJobTypeLimiter(2, recalcFanOutMaxConcurrency: 4)
        {
            RetryDelay = TimeSpan.FromMilliseconds(20),
        });
        services.AddSingleton<ISchedulerFactory>(factory);
        services.AddScoped(sp => new QuartzJobScheduler(
            sp.GetService<ISchedulerFactory>(), null, sp.GetService<Ecr.Domain.Abstractions.IClock>()));

        await using var provider = services.BuildServiceProvider();
        var adapters = new CountingFactory(provider);
        quartz.JobFactory = adapters;

        using (var scope = provider.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<QuartzJobScheduler>();
            for (var doc = 1; doc <= Children; doc++)
            {
                await jobs.EnqueueAsync<IRecalculationJob>(
                    new
                    {
                        projectId = 1,
                        documentId = doc,
                        periodKey = (int?)null,
                        fanOutParentJobId = "IRecalculationJob-parent",
                    },
                    CancellationToken.None);
            }
        }

        await quartz.Start();

        // Чотири стартували й стоять на бар'єрі; решту 16 потоків пулу не заняли — їх відкладено триґером.
        await job.FourStarted.Task.WaitAsync(Safety);
        // Відкладення тривають (адаптер створювано знову й знову); без межі відповідь не потрібна — тут же
        // впаде перевірка `MaxConcurrent` нижче, а не таймаут на 60 с.
        await Task.WhenAny(adapters.Reached(Children + 24), Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Equal(4, job.Running);
        Assert.Equal(4, job.MaxConcurrent);

        job.Release.TrySetResult();
        await job.AllRan.Task.WaitAsync(Safety);

        Assert.Equal(Children, job.Runs);
        Assert.Equal(4, job.MaxConcurrent);
    }

    /// <summary>Рахує створення адаптера (кожен триґер, зокрема відкладений).</summary>
    private sealed class CountingFactory(IServiceProvider provider) : IJobFactory
    {
        private readonly object gate = new();
        private readonly List<(int Count, TaskCompletionSource Signal)> waits = [];
        private int created;

        public Task Reached(int count)
        {
            lock (gate)
            {
                if (created >= count)
                {
                    return Task.CompletedTask;
                }

                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waits.Add((count, signal));
                return signal.Task;
            }
        }

        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler)
        {
            lock (gate)
            {
                created++;
                foreach (var (count, signal) in waits.Where(w => created >= w.Count))
                {
                    signal.TrySetResult();
                }
            }

            return new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance);
        }

        public void ReturnJob(IJob job)
        {
        }
    }

    /// <summary>Підроблений виконавець: стоїть на бар'єрі, рахує одночасні прогони.</summary>
    private sealed class CountingRecalculationJob(int expectedRuns) : IRecalculationJob
    {
        private int running;
        private int runs;
        private int maxConcurrent;

        public TaskCompletionSource FourStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllRan { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Running => Volatile.Read(ref running);

        public int Runs => Volatile.Read(ref runs);

        public int MaxConcurrent => Volatile.Read(ref maxConcurrent);

        public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref running);
            var seen = Volatile.Read(ref maxConcurrent);
            while (now > seen && Interlocked.CompareExchange(ref maxConcurrent, now, seen) != seen)
            {
                seen = Volatile.Read(ref maxConcurrent);
            }

            if (now >= 4)
            {
                FourStarted.TrySetResult();
            }

            try
            {
                await Release.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref running);
                if (Interlocked.Increment(ref runs) >= expectedRuns)
                {
                    AllRan.TrySetResult();
                }
            }
        }
    }
}
