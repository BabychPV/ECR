// tests/Ecr.Infrastructure.Tests/Jobs/JobAttemptCorrelationTests.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// BE-08: номер спроби і кореляція, які адаптер передає у сховище прогресу.
/// </summary>
/// <remarks>
/// ⚠ Спроби — ЛІТЕРАЛАМИ (1, 2): твердження через <c>RetryAttemptKey + 1</c>
/// рухалося б разом із помилкою в самій арифметиці.
/// </remarks>
public sealed class JobAttemptCorrelationTests
{
    private const string JobId = "be08-job";
    private const string RequestCorrelation = "be08req0123456789abcdef";

    private static readonly DateTime Now = new(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);

    private sealed class FailingJob : IBackgroundJob
    {
        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
            => throw new InvalidOperationException("транзієнтна");
    }

    private sealed class NoopFormulaJob : IFormulaRecalculationJob
    {
        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
            => Task.CompletedTask;
    }

    private static (QuartzJobAdapter Adapter, IJobProgressStore Progress) Adapter()
    {
        var progress = Substitute.For<IJobProgressStore>();

        var services = new ServiceCollection();
        services.AddSingleton(progress);
        services.AddSingleton<IClock>(new TestClock(Now));
        services.AddScoped<FailingJob>();
        services.AddScoped<IFormulaRecalculationJob, NoopFormulaJob>();

        return (new QuartzJobAdapter(services.BuildServiceProvider(), NullLogger<QuartzJobAdapter>.Instance), progress);
    }

    private static (IJobExecutionContext Context, IScheduler Scheduler) Context(
        int retries, string? jobCorrelation, IJobDetail? detail = null)
    {
        var jobData = new JobDataMap
        {
            { QuartzJobScheduler.JobCodeKey, typeof(FailingJob).FullName! },
            { QuartzJobScheduler.PayloadKey, "null" },
        };

        if (jobCorrelation is not null)
        {
            jobData.Put(QuartzJobScheduler.CorrelationKey, jobCorrelation);
        }

        if (detail is null)
        {
            detail = Substitute.For<IJobDetail>();
            detail.Key.Returns(new JobKey(JobId));
            detail.JobDataMap.Returns(jobData);
        }

        var triggerData = new JobDataMap();
        if (retries > 0)
        {
            triggerData.Put(QuartzJobScheduler.RetryAttemptKey, retries.ToString(CultureInfo.InvariantCulture));
        }

        var trigger = Substitute.For<ITrigger>();
        trigger.JobDataMap.Returns(triggerData);

        var scheduler = Substitute.For<IScheduler>();
        var context = Substitute.For<IJobExecutionContext>();
        context.JobDetail.Returns(detail);
        context.Trigger.Returns(trigger);
        context.Scheduler.Returns(scheduler);
        context.CancellationToken.Returns(CancellationToken.None);

        return (context, scheduler);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "BE-08")]
    public async Task Перший_запуск_це_спроба_1_а_повтор_після_збою_спроба_2()
    {
        var (adapter, progress) = Adapter();

        // Перший прогін провалюється й планує ретрай; другий — сам ретрай.
        await adapter.Execute(Context(retries: 0, "corr-1").Context);
        await adapter.Execute(Context(retries: 1, "corr-1").Context);

        // ⛔ Мутація: `CurrentAttempt(context) + 1` → `CurrentAttempt(context)`
        // дає 0 і 1 — обидва твердження червоні.
        await progress.Received(1).StartAsync(
            JobId, Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>(), 1, "corr-1");
        await progress.Received(1).StartAsync(
            JobId, Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>(), 2, "corr-1");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "BE-08")]
    public async Task Ретрай_несе_ту_саму_кореляцію_навіть_коли_її_згенеровано_на_прогоні()
    {
        var (adapter, progress) = Adapter();

        // Задача за розкладом: кореляції в даних задачі немає.
        var (context, scheduler) = Context(retries: 0, jobCorrelation: null);
        await adapter.Execute(context);

        var started = progress.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IJobProgressStore.StartAsync))
            .GetArguments()[5] as string;

        // Нова, а не порожня: нічний прогін теж має бути знайденим у лозі.
        Assert.Matches("^[0-9a-f]{32}$", started);

        await scheduler.Received(1).ScheduleJob(
            Arg.Is<ITrigger>(t => t.JobDataMap.GetString(QuartzJobScheduler.CorrelationKey) == started),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "BE-08")]
    public async Task Кореляція_запиту_доїжджає_від_постановки_до_старту_задачі()
    {
        var factory = new Quartz.Impl.StdSchedulerFactory(
            new System.Collections.Specialized.NameValueCollection
            {
                ["quartz.scheduler.instanceName"] = $"ecr-be08-{Guid.NewGuid():N}",
                ["quartz.threadPool.threadCount"] = "1",
            });

        // Планувальник не запускається: предмет — вміст деталі й записи прогресу.
        var scheduler = await factory.GetScheduler();
        var queued = Substitute.For<IJobProgressStore>();
        var request = Substitute.For<ICorrelationIdAccessor>();
        request.CorrelationId.Returns(RequestCorrelation);

        var jobs = new QuartzJobScheduler(factory, queued, new TestClock(Now), request);
        var jobId = await jobs.EnqueueAsync<IFormulaRecalculationJob>(null, CancellationToken.None, 7);

        await queued.Received(1).QueueAsync(
            jobId, Arg.Any<string>(), Now, Arg.Any<CancellationToken>(), 7, RequestCorrelation);

        var detail = await scheduler.GetJobDetail(new JobKey(jobId));
        Assert.NotNull(detail);

        var (adapter, progress) = Adapter();
        await adapter.Execute(Context(retries: 0, jobCorrelation: null, detail).Context);

        await progress.Received(1).StartAsync(
            jobId, Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>(), 1, RequestCorrelation);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "BE-08")]
    public async Task Документ_задачі_береться_з_payload_при_постановці()
    {
        var factory = new Quartz.Impl.StdSchedulerFactory(
            new System.Collections.Specialized.NameValueCollection
            {
                ["quartz.scheduler.instanceName"] = $"ecr-be08d-{Guid.NewGuid():N}",
                ["quartz.threadPool.threadCount"] = "1",
            });
        var queued = Substitute.For<IJobProgressStore>();
        var jobs = new QuartzJobScheduler(factory, queued, new TestClock(Now));

        var withDoc = await jobs.EnqueueAsync<IFormulaRecalculationJob>(
            new { DocumentId = 9_000_000_001L, PeriodKey = "2026-09" }, CancellationToken.None);
        var noDoc = await jobs.EnqueueAsync<IFormulaRecalculationJob>(
            new { TableInstanceId = 5L }, CancellationToken.None);

        await queued.Received(1).QueueAsync(
            withDoc, Arg.Any<string>(), Now, Arg.Any<CancellationToken>(), Arg.Any<int?>(), Arg.Any<string?>(),
            9_000_000_001L);
        await queued.Received(1).QueueAsync(
            noDoc, Arg.Any<string>(), Now, Arg.Any<CancellationToken>(), Arg.Any<int?>(), Arg.Any<string?>(),
            Arg.Is<long?>(d => d == null));
    }
}
