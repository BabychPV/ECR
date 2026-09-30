// tests/Ecr.Infrastructure.Tests/Jobs/RecurringJobSurvivesRunTests.cs
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
/// Задача за розкладом (<see cref="QuartzJobScheduler.ScheduleAsync{TJob}"/>)
/// переживає власний прогін — успішний, скасований і скасований через API; разова
/// задача (<see cref="QuartzJobScheduler.EnqueueAsync{TJob}"/>) після успіху
/// прибирається, як і раніше.
/// </summary>
/// <remarks>
/// ⛔ Дефект: адаптер після успіху й після скасування викликав
/// <c>DeleteJob(context.JobDetail.Key)</c> без огляду на те, чи задача разова.
/// У розкладу ключ той самий, що в крон-тригера, а <c>DeleteJob</c> прибирає
/// задачу РАЗОМ із тригерами. Сховище в пам'яті, розклади ставляться лише на
/// старті — тож кожна нічна/погодинна задача й кожен розклад збору
/// відпрацьовували рівно один раз після старту, далі тиша до рестарту.
/// <para>
/// ⚠ Планувальник СПРАВЖНІЙ (<c>RAMJobStore</c>), бо предмет — саме семантика
/// <c>DeleteJob</c> над крон-тригером; заглушка <c>IScheduler</c>, як у
/// <see cref="QuartzJobAdapterRecurringLockTests"/>, цього не бачить узагалі.
/// Власна фабрика з унікальним <c>instanceName</c> і <c>Shutdown</c> у
/// <c>finally</c>: у цьому проєкті хостів немає, тож статичний
/// <c>LogProvider</c> Quartz ні до якої закритої <c>LoggerFactory</c> не
/// прив'язаний (пастка з <c>Ecr.Api.Tests</c> тут не діє).
/// </para>
/// <para>
/// ⚠ SQL Server потрібен: recurring-задача бере міжінстансовий лок (Q-223).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class RecurringJobSurvivesRunTests(SqlServerFixture sql)
{
    private const string Hourly = "0 5 * * * ?";

    private static readonly DateTime Now = new(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Задача, що завершується успішно.</summary>
    private sealed class SucceedingJob : IBackgroundJob
    {
        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>Задача, яку скасували посеред прогону.</summary>
    private sealed class CancelledJob : IBackgroundJob
    {
        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
            => throw new OperationCanceledException("Скасовано (симуляція).");
    }

    private ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<EcrDbContext>(o => o.UseSqlServer(sql.ConnectionString));
        services.AddSingleton(Substitute.For<IJobProgressStore>());
        services.AddSingleton<Ecr.Domain.Abstractions.IClock>(new TestClock(Now));
        services.AddScoped<SucceedingJob>();
        services.AddScoped<CancelledJob>();
        return services.BuildServiceProvider();
    }

    private static StdSchedulerFactory Factory()
        => new(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-recurring-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
        });

    /// <summary>Єдиний ключ задачі в планувальнику.</summary>
    private static async Task<JobKey> SingleKeyAsync(IScheduler quartz)
        => Assert.Single(await quartz.GetJobKeys(Quartz.Impl.Matchers.GroupMatcher<JobKey>.AnyGroup()));

    /// <summary>
    /// Контекст виконання зі СПРАВЖНІМИ деталлю, тригером і планувальником —
    /// те, що Quartz передав би адаптеру на тик.
    /// </summary>
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

    /// <summary>Задача на місці, і її крон-тригер ще має наступне спрацювання.</summary>
    private static async Task AssertScheduleAliveAsync(IScheduler quartz, JobKey key)
    {
        Assert.True(await quartz.CheckExists(key), $"Розклад {key} зник після прогону.");

        var cron = Assert.Single((await quartz.GetTriggersOfJob(key)).OfType<ICronTrigger>());
        Assert.Equal(Hourly, cron.CronExpressionString);
        Assert.NotNull(cron.GetNextFireTimeUtc());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Розклад_переживає_успішний_прогін()
    {
        var factory = Factory();
        var quartz = await factory.GetScheduler();
        try
        {
            await new QuartzJobScheduler(factory).ScheduleAsync<SucceedingJob>(
                Hourly, new { tag = Guid.NewGuid() }, CancellationToken.None);
            var key = await SingleKeyAsync(quartz);

            var adapter = new QuartzJobAdapter(Provider(), NullLogger<QuartzJobAdapter>.Instance);
            await adapter.Execute(await ContextAsync(quartz, key));

            await AssertScheduleAliveAsync(quartz, key);
        }
        finally
        {
            await quartz.Shutdown(waitForJobsToComplete: false);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Розклад_переживає_скасований_прогін()
    {
        var factory = Factory();
        var quartz = await factory.GetScheduler();
        try
        {
            await new QuartzJobScheduler(factory).ScheduleAsync<CancelledJob>(
                Hourly, new { tag = Guid.NewGuid() }, CancellationToken.None);
            var key = await SingleKeyAsync(quartz);

            var adapter = new QuartzJobAdapter(Provider(), NullLogger<QuartzJobAdapter>.Instance);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await adapter.Execute(await ContextAsync(quartz, key)));

            await AssertScheduleAliveAsync(quartz, key);
        }
        finally
        {
            await quartz.Shutdown(waitForJobsToComplete: false);
        }
    }

    /// <summary>
    /// ⛔ Скасування через API (<c>POST /jobs/{jobId}/cancel</c> →
    /// <see cref="QuartzJobScheduler.CancelAsync"/>) — прохання зупинити
    /// ПОТОЧНИЙ прогін, а не зняти розклад. Прогрес розкладу пишеться під його
    /// ключем, тож власник права <c>System.ViewHealth</c> бачить погодинну задачу
    /// «Running» і може її скасувати.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Скасування_через_API_не_знімає_розклад()
    {
        var factory = Factory();
        var quartz = await factory.GetScheduler();
        try
        {
            var jobs = new QuartzJobScheduler(factory);
            await jobs.ScheduleAsync<SucceedingJob>(Hourly, new { tag = Guid.NewGuid() }, CancellationToken.None);
            var key = await SingleKeyAsync(quartz);

            await jobs.CancelAsync(key.Name, CancellationToken.None);

            await AssertScheduleAliveAsync(quartz, key);
        }
        finally
        {
            await quartz.Shutdown(waitForJobsToComplete: false);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Скасування_через_API_разової_задачі_прибирає_її_як_і_раніше()
    {
        var factory = Factory();
        var quartz = await factory.GetScheduler();
        try
        {
            var jobs = new QuartzJobScheduler(factory);
            var jobId = await jobs.EnqueueAsync<SucceedingJob>(null, CancellationToken.None);

            await jobs.CancelAsync(jobId, CancellationToken.None);

            Assert.False(await quartz.CheckExists(new JobKey(jobId)));
        }
        finally
        {
            await quartz.Shutdown(waitForJobsToComplete: false);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Разова_задача_після_успіху_прибирається_як_і_раніше()
    {
        var factory = Factory();
        var quartz = await factory.GetScheduler();
        try
        {
            var jobId = await new QuartzJobScheduler(factory).EnqueueAsync<SucceedingJob>(null, CancellationToken.None);
            var key = new JobKey(jobId);

            var adapter = new QuartzJobAdapter(Provider(), NullLogger<QuartzJobAdapter>.Instance);
            await adapter.Execute(await ContextAsync(quartz, key));

            Assert.False(await quartz.CheckExists(key));
        }
        finally
        {
            await quartz.Shutdown(waitForJobsToComplete: false);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Разова_задача_після_скасування_прибирається_як_і_раніше()
    {
        var factory = Factory();
        var quartz = await factory.GetScheduler();
        try
        {
            var jobId = await new QuartzJobScheduler(factory).EnqueueAsync<CancelledJob>(null, CancellationToken.None);
            var key = new JobKey(jobId);

            var adapter = new QuartzJobAdapter(Provider(), NullLogger<QuartzJobAdapter>.Instance);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await adapter.Execute(await ContextAsync(quartz, key)));

            Assert.False(await quartz.CheckExists(key));
        }
        finally
        {
            await quartz.Shutdown(waitForJobsToComplete: false);
        }
    }

    /// <summary>
    /// <see cref="QuartzJobScheduler.RecurringLockName{TJob}"/> — рівно той лок,
    /// який адаптер бере на тик розкладу: хто тримає його поза планувальником
    /// (стартове вирівнювання станів періодів), той блокує погодинний прогін.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Лок_поза_планувальником_за_RecurringLockName_блокує_тик_розкладу()
    {
        var factory = Factory();
        var quartz = await factory.GetScheduler();
        try
        {
            var payload = new { tag = Guid.NewGuid() };
            await new QuartzJobScheduler(factory).ScheduleAsync<CountingJob>(Hourly, payload, CancellationToken.None);
            var key = await SingleKeyAsync(quartz);

            await using var held = await SqlDistributedLock.TryAcquireAsync(
                sql.ConnectionString, QuartzJobScheduler.RecurringLockName<CountingJob>(payload), CancellationToken.None);
            Assert.NotNull(held);

            var job = new CountingJob();
            var services = new ServiceCollection();
            services.AddDbContext<EcrDbContext>(o => o.UseSqlServer(sql.ConnectionString));
            services.AddSingleton<Ecr.Domain.Abstractions.IClock>(new TestClock(Now));
            services.AddSingleton(job);
            await using var provider = services.BuildServiceProvider();

            await new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance)
                .Execute(await ContextAsync(quartz, key));

            Assert.Equal(0, job.Calls);
        }
        finally
        {
            await quartz.Shutdown(waitForJobsToComplete: false);
        }
    }

    /// <summary>Рахує прогони.</summary>
    private sealed class CountingJob : IBackgroundJob
    {
        public int Calls { get; private set; }

        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Наскрізно: ЗАПУЩЕНИЙ планувальник сам виконує задачу розкладу (позачерговий
    /// тик через <c>TriggerJob</c>), і після нього крон-тригер лишається.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Запущений_планувальник_після_тику_розкладу_лишає_крон_тригер()
    {
        var factory = Factory();
        var quartz = await factory.GetScheduler();
        using var provider = Provider();
        var executed = new TaskCompletionSource<JobExecutionException?>(TaskCreationOptions.RunContinuationsAsynchronously);

        quartz.JobFactory = new AdapterFactory(provider);
        quartz.ListenerManager.AddJobListener(new DoneListener(executed));

        try
        {
            await new QuartzJobScheduler(factory).ScheduleAsync<SucceedingJob>(
                Hourly, new { tag = Guid.NewGuid() }, CancellationToken.None);
            var key = await SingleKeyAsync(quartz);

            await quartz.Start();
            await quartz.TriggerJob(key);

            var failure = await executed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Null(failure);

            await AssertScheduleAliveAsync(quartz, key);
        }
        finally
        {
            await quartz.Shutdown(waitForJobsToComplete: true);
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

    /// <summary>Сигналізує, що Quartz завершив виконання (після <c>Execute</c> адаптера).</summary>
    private sealed class DoneListener(TaskCompletionSource<JobExecutionException?> done) : IJobListener
    {
        public string Name => "ecr-recurring-done";

        public Task JobToBeExecuted(IJobExecutionContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task JobExecutionVetoed(IJobExecutionContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task JobWasExecuted(
            IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken = default)
        {
            done.TrySetResult(jobException);
            return Task.CompletedTask;
        }
    }
}
