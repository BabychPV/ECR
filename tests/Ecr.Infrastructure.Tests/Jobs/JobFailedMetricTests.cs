using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Observability;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// <c>ecr.job.failed</c> (ФВ-12.7, НФ-8.6.2): остаточно провалена задача дає +1 з тегами
/// <c>job</c> і <c>reason</c> в ОБОХ виконавцях; успішна — нуль. Реальний MeterListener.
/// </summary>
/// <remarks>
/// Мутації: прибрати <c>RecordJobFailed</c> у <c>JobWorker</c> — 1-й і 2-й червоні; у
/// <c>QuartzJobAdapter</c> — 3-й; писати метрику й на успіху — 2-й і 4-й.
/// </remarks>
public sealed class JobFailedMetricTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task JobWorker_провал_без_ретраїв_дає_один_ecr_job_failed_з_кодом_і_причиною()
    {
        var code = typeof(WorkerFailingJob).FullName!;
        using var capture = new InfrastructureMetricsCapture(code);

        await RunWorkerOnceAsync<WorkerFailingJob>(code, failed: true);

        var failed = Assert.Single(capture.Of(InfrastructureMetrics.JobFailed));
        Assert.Equal(code, failed["job"]);
        Assert.Equal("error", failed["reason"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task JobWorker_успішна_задача_метрику_провалу_не_пише()
    {
        var code = typeof(WorkerOkJob).FullName!;
        using var capture = new InfrastructureMetricsCapture(code);

        await RunWorkerOnceAsync<WorkerOkJob>(code, failed: false);

        Assert.Empty(capture.Of(InfrastructureMetrics.JobFailed));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task QuartzJobAdapter_остаточний_провал_дає_один_ecr_job_failed()
    {
        var code = typeof(QuartzFailingJob).FullName!;
        using var capture = new InfrastructureMetricsCapture(code);

        await Assert.ThrowsAsync<JobExecutionException>(() => RunQuartzAsync<QuartzFailingJob>(code));

        var failed = Assert.Single(capture.Of(InfrastructureMetrics.JobFailed));
        Assert.Equal(code, failed["job"]);
        Assert.Equal("error", failed["reason"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task QuartzJobAdapter_успіх_метрику_провалу_не_пише()
    {
        var code = typeof(QuartzOkJob).FullName!;
        using var capture = new InfrastructureMetricsCapture(code);

        await RunQuartzAsync<QuartzOkJob>(code);

        Assert.Empty(capture.Of(InfrastructureMetrics.JobFailed));
    }

    /// <remarks>
    /// <c>ecr.job.run.duration</c> (ФВ-12.7): одна спроба задачі з черги — один запис з тегами
    /// <c>job</c> і <c>outcome</c>. Мутації: прибрати <c>RecordJobRun</c> у <c>JobWorker</c> —
    /// обидва червоні; завжди писати <c>ok</c> — другий червоний.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData(false, "ok")]
    [InlineData(true, "error")]
    public async Task JobWorker_кожна_спроба_дає_один_запис_тривалості_з_наслідком(bool fails, string outcome)
    {
        var code = fails ? typeof(WorkerFailingJob).FullName! : typeof(WorkerOkJob).FullName!;
        using var capture = new InfrastructureMetricsCapture(code);

        if (fails)
        {
            await RunWorkerOnceAsync<WorkerFailingJob>(code, failed: true);
        }
        else
        {
            await RunWorkerOnceAsync<WorkerOkJob>(code, failed: false);
        }

        var run = Assert.Single(capture.Of(InfrastructureMetrics.JobRunDuration));
        Assert.Equal(code, run["job"]);
        Assert.Equal(outcome, run["outcome"]);
        Assert.True((double)run["value"]! >= 0d);
    }

    private static async Task RunQuartzAsync<TJob>(string code)
        where TJob : class, IBackgroundJob
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<TJob>();
        await using var provider = services.BuildServiceProvider();

        var jobData = new JobDataMap
        {
            { QuartzJobScheduler.JobCodeKey, code },
            { QuartzJobScheduler.PayloadKey, "null" },
        };
        var jobDetail = Substitute.For<IJobDetail>();
        jobDetail.Key.Returns(new JobKey($"failmetric-{Guid.NewGuid():N}"));
        jobDetail.JobDataMap.Returns(jobData);
        var trigger = Substitute.For<ITrigger>();
        trigger.JobDataMap.Returns(new JobDataMap());
        var context = Substitute.For<IJobExecutionContext>();
        context.JobDetail.Returns(jobDetail);
        context.Trigger.Returns(trigger);
        context.Scheduler.Returns(Substitute.For<IScheduler>());
        context.CancellationToken.Returns(CancellationToken.None);

        await new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance).Execute(context);
    }

    private static async Task RunWorkerOnceAsync<TJob>(string code, bool failed)
        where TJob : class, IBackgroundJob
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claimed = new ClaimedJob(
            new JobClaimToken($"failmetric-{Guid.NewGuid():N}", Guid.NewGuid()), code, JobLanes.Default, null, null, 1, 0,
            Reclaimed: false, DateTime.UtcNow.AddMinutes(5), null, null, null, 0);

        var queue = Substitute.For<IJobQueue>();
        var handedOut = 0;
        queue.ClaimAsync(default!, default!, default, default).ReturnsForAnyArgs(_ =>
            Task.FromResult(Interlocked.Increment(ref handedOut) == 1 ? claimed : null));
        queue.RenewAsync(default!, default, default).ReturnsForAnyArgs(Task.FromResult(LeaseState.Held));
        queue.CompleteAsync(default!, default).ReturnsForAnyArgs(_ =>
        {
            settled.TrySetResult();
            return Task.FromResult(true);
        });
        queue.FailAsync(default!, default!, default, default).ReturnsForAnyArgs(_ =>
        {
            settled.TrySetResult();
            return Task.FromResult(true);
        });

        var services = new ServiceCollection();
        services.AddScoped(_ => queue);
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<JobLeaseContext>();
        services.AddScoped<TJob>();
        await using var provider = services.BuildServiceProvider();

        using var worker = new JobWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new JobWorkerOptions
            {
                Lanes = JobLanes.All,
                Role = JobProgressStore.CurrentRole,
                PollInterval = TimeSpan.FromMilliseconds(10),
            },
            new JobQueueSignal(),
            NullLogger<JobWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(failed, queue.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IJobQueue.FailAsync)));
    }

    public sealed class WorkerFailingJob : IBackgroundJob
    {
        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
            => throw new NotFoundException("ECR-PRD-0404", "Періоду немає.");
    }

    public sealed class WorkerOkJob : IBackgroundJob
    {
        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct) => Task.CompletedTask;
    }

    public sealed class QuartzFailingJob : IBackgroundJob
    {
        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
            => throw new NotFoundException("ECR-PRD-0404", "Періоду немає.");
    }

    public sealed class QuartzOkJob : IBackgroundJob
    {
        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct) => Task.CompletedTask;
    }
}
