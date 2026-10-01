// tests/Ecr.Infrastructure.Tests/Jobs/CoalescedRequeueGuaranteeQuartzTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Quartz.Impl;
using Quartz.Spi;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Гарантія «хоч одне виконання ПІСЛЯ останнього запиту» для
/// <see cref="QuartzJobScheduler.EnqueueCoalescedAsync{TJob}"/> наскрізь: справжня
/// задача через <see cref="QuartzJobAdapter"/>, виконання блокується бар'єром, а
/// запити приходять, поки вона виконується («шапку змінили після того, як задача її
/// прочитала»). Діагностика гіпотези №2 флейку HeaderScenarios.HDR_у_формулі_…
/// </summary>
/// <remarks>
/// Висновок (2026-10-01): на рівні планувальника гарантія ДІЄ — повторне виконання є.
/// Мутація: слухач не перепоставляє брудну задачу (прибрати <c>await requeue(...)</c>
/// у <c>JobWasExecuted</c>) — червоніють «один запит» і «три запити» (виконань 1).
/// </remarks>
public sealed class CoalescedRequeueGuaranteeQuartzTests : IAsyncLifetime
{
    private const string Target = "doc9-p202601";

    private readonly List<IScheduler> schedulers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var scheduler in schedulers)
        {
            await scheduler.Shutdown(waitForJobsToComplete: false).ConfigureAwait(false);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Запит_під_час_виконання_дає_друге_виконання_після_завершення_першого()
    {
        var job = new GatedRecalculationJob();
        var (jobs, quartz) = await SchedulerAsync(job);

        await jobs.EnqueueCoalescedAsync<IRecalculationJob>(Target, new { n = 1 }, CancellationToken.None);
        await quartz.Start();
        await job.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // «Шапка змінилась» — задача вже читала дані.
        await jobs.EnqueueCoalescedAsync<IRecalculationJob>(Target, new { n = 2 }, CancellationToken.None);
        Assert.Equal(1, job.Count);

        job.Release.TrySetResult();
        await job.WaitForCountAsync(2);
        await WaitIdleAsync(quartz);

        Assert.Equal(["{\"n\":1}", "{\"n\":2}"], job.Payloads);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Три_запити_під_час_виконання_дають_рівно_одне_повторне_виконання_з_останнім_payload()
    {
        var job = new GatedRecalculationJob();
        var (jobs, quartz) = await SchedulerAsync(job);

        await jobs.EnqueueCoalescedAsync<IRecalculationJob>(Target, new { n = 1 }, CancellationToken.None);
        await quartz.Start();
        await job.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        for (var n = 2; n <= 4; n++)
        {
            await jobs.EnqueueCoalescedAsync<IRecalculationJob>(Target, new { n }, CancellationToken.None);
        }

        job.Release.TrySetResult();
        await job.WaitForCountAsync(2);
        await WaitIdleAsync(quartz);

        Assert.Equal(["{\"n\":1}", "{\"n\":4}"], job.Payloads);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Запит_після_завершення_дає_нове_виконання_без_зайвих()
    {
        var job = new GatedRecalculationJob();
        var (jobs, quartz) = await SchedulerAsync(job);

        job.Release.TrySetResult();
        await jobs.EnqueueCoalescedAsync<IRecalculationJob>(Target, new { n = 1 }, CancellationToken.None);
        await quartz.Start();
        await job.WaitForCountAsync(1);
        await WaitIdleAsync(quartz);

        await jobs.EnqueueCoalescedAsync<IRecalculationJob>(Target, new { n = 2 }, CancellationToken.None);
        await job.WaitForCountAsync(2);
        await WaitIdleAsync(quartz);

        Assert.Equal(["{\"n\":1}", "{\"n\":2}"], job.Payloads);
    }

    private async Task<(QuartzJobScheduler Jobs, IScheduler Quartz)> SchedulerAsync(GatedRecalculationJob job)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Ecr.Domain.Abstractions.IClock>(new TestClock(new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc)));
        services.AddSingleton<IRecalculationJob>(job);
        var provider = services.BuildServiceProvider();

        var factory = new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-tests-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
        });

        var scheduler = await factory.GetScheduler().ConfigureAwait(false);
        scheduler.JobFactory = new AdapterFactory(provider);
        schedulers.Add(scheduler);

        return (new QuartzJobScheduler(factory), scheduler);
    }

    /// <summary>Спокій: ніщо не виконується й жодна задача не має триґера (опитування стану, не затримка).</summary>
    private static async Task WaitIdleAsync(IScheduler quartz)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var calm = 0;
        while (calm < 5 && DateTime.UtcNow < deadline)
        {
            var busy = (await quartz.GetCurrentlyExecutingJobs()).Count > 0
                       || (await quartz.GetTriggerKeys(Quartz.Impl.Matchers.GroupMatcher<TriggerKey>.AnyGroup())).Count > 0;
            calm = busy ? 0 : calm + 1;
            await Task.Yield();
            await Task.Delay(40);
        }

        Assert.True(calm >= 5, "планувальник не заспокоївся за 30 с");
    }

    private sealed class AdapterFactory(IServiceProvider provider) : IJobFactory
    {
        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler)
            => new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance);

        public void ReturnJob(IJob job)
        {
        }
    }

    /// <summary>Перше виконання тримає бар'єр; решта проходять одразу. Пам'ятає payload кожного.</summary>
    private sealed class GatedRecalculationJob : IRecalculationJob
    {
        private readonly object gate = new();
        private readonly List<string?> payloads = [];

        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Count
        {
            get { lock (gate) { return payloads.Count; } }
        }

        public List<string?> Payloads
        {
            get { lock (gate) { return [.. payloads]; } }
        }

        public async Task WaitForCountAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Count < count)
            {
                Assert.True(DateTime.UtcNow < deadline, $"виконань {Count}, очікувано {count}");
                await Task.Delay(25);
            }
        }

        public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        {
            bool first;
            lock (gate)
            {
                payloads.Add(payload as string);
                first = payloads.Count == 1;
            }

            if (first)
            {
                FirstStarted.TrySetResult();
                await Release.Task.WaitAsync(ct).ConfigureAwait(false);
            }
        }
    }
}
