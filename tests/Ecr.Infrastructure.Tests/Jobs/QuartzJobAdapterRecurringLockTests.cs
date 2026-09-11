// tests/Ecr.Infrastructure.Tests/Jobs/QuartzJobAdapterRecurringLockTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// <see cref="QuartzJobAdapter"/> пропускає тик recurring-job, коли інший
/// інстанс уже тримає той самий <see cref="SqlDistributedLock"/> — на
/// <b>реальному</b> SQL Server (Q-223, `Jobs`-секція).
/// </summary>
[Collection("SqlServer")]
public sealed class QuartzJobAdapterRecurringLockTests(SqlServerFixture sql)
{
    private const string JobId = "PartitionCheckJob:fingerprint";

    private sealed class SpyJob : IBackgroundJob
    {
        public int Calls { get; private set; }

        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private ServiceProvider BuildProvider(SpyJob job, IJobProgressStore progress)
    {
        var services = new ServiceCollection();
        services.AddDbContext<EcrDbContext>(o => o.UseSqlServer(sql.ConnectionString));
        services.AddSingleton(progress);
        services.AddSingleton<Ecr.Domain.Abstractions.IClock>(
            new TestClock(new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc)));
        services.AddSingleton(job);
        return services.BuildServiceProvider();
    }

    private static IJobExecutionContext RecurringContext(SpyJob job)
    {
        var jobData = new JobDataMap
        {
            { QuartzJobScheduler.JobCodeKey, typeof(SpyJob).FullName! },
            { QuartzJobScheduler.PayloadKey, "null" },
            { QuartzJobScheduler.RecurringKey, "1" },
        };

        var jobDetail = Substitute.For<IJobDetail>();
        jobDetail.Key.Returns(new JobKey(JobId));
        jobDetail.JobDataMap.Returns(jobData);

        var trigger = Substitute.For<ITrigger>();
        trigger.JobDataMap.Returns(new JobDataMap());

        var context = Substitute.For<IJobExecutionContext>();
        context.JobDetail.Returns(jobDetail);
        context.Trigger.Returns(trigger);
        context.Scheduler.Returns(Substitute.For<IScheduler>());
        context.CancellationToken.Returns(CancellationToken.None);

        return context;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Інший_інстанс_уже_виконує_job_нинішній_тик_пропускається()
    {
        // "Інший інстанс" — та сама сесія-лок, узята НАПЕРЕД, поза адаптером.
        await using var heldByOtherInstance = await SqlDistributedLock.TryAcquireAsync(
            sql.ConnectionString, $"Ecr.Job.{JobId}", CancellationToken.None);
        Assert.NotNull(heldByOtherInstance);

        var job = new SpyJob();
        var progress = Substitute.For<IJobProgressStore>();
        var adapter = new QuartzJobAdapter(BuildProvider(job, progress), NullLogger<QuartzJobAdapter>.Instance);

        await adapter.Execute(RecurringContext(job));

        // ⛔ Головне твердження: без локу цей виклик дублював би роботу,
        // яку "інший інстанс" уже виконує просто зараз.
        Assert.Equal(0, job.Calls);
        await progress.DidNotReceiveWithAnyArgs().StartAsync(default!, default!, default, default);
        await progress.DidNotReceiveWithAnyArgs().FinishAsync(default!, default!, default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Коли_лок_вільний_recurring_job_виконується_як_завжди()
    {
        var job = new SpyJob();
        var progress = Substitute.For<IJobProgressStore>();
        var adapter = new QuartzJobAdapter(BuildProvider(job, progress), NullLogger<QuartzJobAdapter>.Instance);

        await adapter.Execute(RecurringContext(job));

        Assert.Equal(1, job.Calls);
        await progress.Received(1).FinishAsync(
            JobId, "Succeeded", null, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }
}
