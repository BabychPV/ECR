// tests/Ecr.Infrastructure.Tests/Jobs/QuartzJobTypeLimiterTests.cs
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
/// AN-116 (P1-06, режим Quartz): експорт/імпорт Excel — не більше <c>Jobs:Excel:MaxConcurrency</c>
/// одночасно, і задача понад межу не тримає потоку пулу, а відкладається триґером.
/// </summary>
/// <remarks>
/// Без пауз «на дочекатися»: сигнали — <see cref="TaskCompletionSource"/>, таймаут лише запобіжний.
/// Мутація: прибрати перевірку <see cref="QuartzJobTypeLimiter"/> з <see cref="QuartzJobAdapter"/> — друга
/// книга стартує поруч із першою, <c>MaxConcurrent</c> = 2, тест червоний.
/// </remarks>
public sealed class QuartzJobTypeLimiterTests : IAsyncLifetime
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(30);

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
    [Trait("Finding", "AN-116")]
    public void Межа_діє_лише_на_Excel_і_звільняється_один_раз()
    {
        var limiter = new QuartzJobTypeLimiter(1);

        using var first = limiter.TryEnter(typeof(ExcelExportJob));
        Assert.NotNull(first);
        Assert.Null(limiter.TryEnter(typeof(ExcelImportJob)));

        // Інші типи межа не чіпає.
        using (var other = limiter.TryEnter(typeof(FormulaRecalculationJob)))
        {
            Assert.NotNull(other);
        }

        Assert.Equal(1, limiter.ExcelRunning);

        first!.Dispose();
        first.Dispose();
        Assert.Equal(0, limiter.ExcelRunning);

        using var again = limiter.TryEnter(typeof(ExcelImportJob));
        Assert.NotNull(again);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData(null, QuartzJobTypeLimiter.DefaultThreadCount)]
    [InlineData("", QuartzJobTypeLimiter.DefaultThreadCount)]
    [InlineData("24", 24)]
    [InlineData("0", QuartzJobTypeLimiter.DefaultThreadCount)]
    public void Кількість_потоків_Quartz_з_конфігурації(string? value, int expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [QuartzJobTypeLimiter.ThreadCountKey] = value })
            .Build();

        Assert.Equal(expected, QuartzJobTypeLimiter.ReadThreadCount(configuration));
        Assert.True(QuartzJobTypeLimiter.DefaultThreadCount > 10, "типово пул більший за вбудовані 10 потоків Quartz");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "AN-116")]
    public async Task Друга_книга_понад_межу_відкладається_і_стартує_після_першої()
    {
        var job = new GatedExcelJob();
        var factory = new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-tests-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "4",
        });
        quartz = await factory.GetScheduler();

        var services = new ServiceCollection();
        services.AddSingleton<Ecr.Domain.Abstractions.IClock, SystemClock>();
        services.AddSingleton<IExcelExportJob>(job);
        services.AddSingleton(new QuartzJobTypeLimiter(1) { RetryDelay = TimeSpan.FromMilliseconds(50) });
        services.AddSingleton<ISchedulerFactory>(factory);
        services.AddScoped(sp => new QuartzJobScheduler(
            sp.GetService<ISchedulerFactory>(), null, sp.GetService<Ecr.Domain.Abstractions.IClock>()));

        await using var provider = services.BuildServiceProvider();
        var adapters = new CountingFactory(provider);
        quartz.JobFactory = adapters;

        using (var scope = provider.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<QuartzJobScheduler>();
            await jobs.EnqueueAsync<IExcelExportJob>(new { n = 1 }, CancellationToken.None);
        }

        await quartz.Start();
        await job.FirstStarted.Task.WaitAsync(Safety);

        using (var scope = provider.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<QuartzJobScheduler>();
            await jobs.EnqueueAsync<IExcelExportJob>(new { n = 2 }, CancellationToken.None);
        }

        // Друга книга вже кілька разів дійшла до адаптера (відкладення через 50 мс) — і не стартувала.
        await adapters.Reached(4).WaitAsync(Safety);
        Assert.Equal(1, job.Runs);

        job.Release.TrySetResult();
        await job.SecondRan.Task.WaitAsync(Safety);

        Assert.Equal(1, job.MaxConcurrent);
        Assert.Equal(2, job.Runs);
    }

    /// <summary>Рахує прогони адаптера (кожен триґер, зокрема відкладений).</summary>
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

    /// <summary>Перша книга чекає бар'єра; рахує одночасні прогони.</summary>
    private sealed class GatedExcelJob : IExcelExportJob
    {
        private int running;
        private int runs;
        private int maxConcurrent;

        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondRan { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

            try
            {
                if (Interlocked.Increment(ref runs) == 1)
                {
                    FirstStarted.TrySetResult();
                    await Release.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    SecondRan.TrySetResult();
                }
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        }
    }
}
