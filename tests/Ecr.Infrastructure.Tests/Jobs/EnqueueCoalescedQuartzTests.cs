// tests/Ecr.Infrastructure.Tests/Jobs/EnqueueCoalescedQuartzTests.cs
using System.Collections.Concurrent;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// <see cref="QuartzJobScheduler.EnqueueCoalescedAsync{TJob}"/>: «виконати після»
/// без витіснення (HSE301 A4).
/// </summary>
/// <remarks>
/// Мутація: тіло методу делегує в <c>EnqueueExclusiveAsync</c> — червоніють
/// «виконувана не перервана» (задача отримує скасування) і «Queued не
/// дублюється» (інший jobId). Мутація: прибрати пошук наявної — червоні обидва
/// тести злиття.
/// </remarks>
public sealed class EnqueueCoalescedQuartzTests : IAsyncLifetime
{
    private const string Target = "doc700-p202601";

    private static readonly string Prefix = $"{nameof(IRecalculationJob)}~{Target}~";

    private readonly List<IScheduler> schedulers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var scheduler in schedulers)
        {
            await scheduler.Shutdown(waitForJobsToComplete: false).ConfigureAwait(false);
        }
    }

    private async Task<(QuartzJobScheduler Jobs, IScheduler Quartz)> SchedulerAsync()
    {
        var factory = new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-tests-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
        });

        var scheduler = await factory.GetScheduler().ConfigureAwait(false);
        schedulers.Add(scheduler);

        return (new QuartzJobScheduler(factory), scheduler);
    }

    private static async Task<List<string>> KeysAsync(IScheduler scheduler)
        => [.. (await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup()).ConfigureAwait(false)).Select(k => k.Name)];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Виконувана_задача_на_ціль_не_переривається_і_нової_не_з_являється()
    {
        var (jobs, quartz) = await SchedulerAsync();
        await quartz.Start();

        var runningId = Prefix + Guid.NewGuid().ToString("N");
        var probe = BlockingJob.Register(runningId);

        try
        {
            await quartz.ScheduleJob(
                JobBuilder.Create<BlockingJob>().WithIdentity(runningId).StoreDurably().Build(),
                TriggerBuilder.Create().WithIdentity(runningId + "-trigger").StartNow().Build());

            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var returned = await jobs.EnqueueCoalescedAsync<IRecalculationJob>(
                Target, new { DocumentId = 700L }, CancellationToken.None);

            // ⛔ Регресія: постановка «після» витіснила б перерахунок, що йде, —
            // на «гарячому» документі він не доходив би до кінця ніколи.
            Assert.False(probe.Token.IsCancellationRequested, "виконувана задача отримала скасування");
            Assert.Equal(runningId, returned);
            Assert.Equal([runningId], await KeysAsync(quartz));
        }
        finally
        {
            probe.Release.TrySetResult();
            BlockingJob.Probes.TryRemove(runningId, out _);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Задача_що_чекає_на_ціль_поглинає_другу_постановку()
    {
        var (jobs, quartz) = await SchedulerAsync();

        var first = await jobs.EnqueueCoalescedAsync<IRecalculationJob>(
            Target, new { DocumentId = 700L }, CancellationToken.None);
        var second = await jobs.EnqueueCoalescedAsync<IRecalculationJob>(
            Target, new { DocumentId = 700L }, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal([first], await KeysAsync(quartz));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Без_задачі_на_ціль_ставиться_нова_з_ключем_цілі()
    {
        var (jobs, quartz) = await SchedulerAsync();

        // Інша ціль і дурабельна деталь, що впала остаточно (без триґера), — не заважають.
        var other = await jobs.EnqueueCoalescedAsync<IRecalculationJob>(
            "doc700-p202602", new { DocumentId = 700L }, CancellationToken.None);
        var failedId = Prefix + Guid.NewGuid().ToString("N");
        await quartz.AddJob(JobBuilder.Create<BlockingJob>().WithIdentity(failedId).StoreDurably().Build(), replace: false);

        var jobId = await jobs.EnqueueCoalescedAsync<IRecalculationJob>(
            Target, new { DocumentId = 700L }, CancellationToken.None);

        Assert.StartsWith(Prefix, jobId, StringComparison.Ordinal);
        Assert.NotEqual(failedId, jobId);
        Assert.NotEqual(other, jobId);
        Assert.Contains(jobId, await KeysAsync(quartz));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Exclusive_і_Coalesced_бачать_задачі_одне_одного()
    {
        var (jobs, _) = await SchedulerAsync();

        var exclusive = await jobs.EnqueueExclusiveAsync<IRecalculationJob>(
            Target, new { DocumentId = 700L }, CancellationToken.None);

        Assert.Equal(exclusive, await jobs.EnqueueCoalescedAsync<IRecalculationJob>(
            Target, new { DocumentId = 700L }, CancellationToken.None));
    }

    /// <summary>Задача, що тримає потік, доки тест її не відпустить, і пам'ятає свій токен.</summary>
    public sealed class BlockingJob : IJob
    {
        public static readonly ConcurrentDictionary<string, Probe> Probes = new(StringComparer.Ordinal);

        public static Probe Register(string jobId) => Probes[jobId] = new Probe();

        public async Task Execute(IJobExecutionContext context)
        {
            if (!Probes.TryGetValue(context.JobDetail.Key.Name, out var probe))
            {
                return;
            }

            probe.Token = context.CancellationToken;
            probe.Started.TrySetResult();

            await probe.Release.Task.ConfigureAwait(false);
        }
    }

    public sealed class Probe
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken Token { get; set; }
    }
}
