// tests/Ecr.Api.Tests/CollectionScheduleStartupTests.cs
using Ecr.Api.Startup;
using Ecr.Application.Integration;
using Ecr.Domain.Entities.External;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Extensions.Logging;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Постановка розкладів збору на старті (ФВ-14.3): описка в одному cron не
/// валить застосунок, а стеля переліку не мовчить.
/// </summary>
/// <remarks>
/// ⚠ Планувальник — справжній Quartz у пам'яті, не запущений; бази не треба:
/// <c>ApplyCollectionSchedulesAsync</c> приймає вже прочитані розклади.
/// </remarks>
public sealed class CollectionScheduleStartupTests
{
    private const string Hourly = "0 5 * * * ?";

    private static async Task<(QuartzJobScheduler Jobs, IScheduler Quartz)> SchedulerAsync()
    {
        var factory = new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-tests-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
        });

        return (new QuartzJobScheduler(factory), await factory.GetScheduler().ConfigureAwait(false));
    }

    private static async Task<int> JobCountAsync(IScheduler quartz)
        => (await quartz.GetJobKeys(GroupMatcher<JobKey>.AnyGroup())).Count;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Невалідний_cron_одного_розкладу_не_валить_старт_і_решта_ставляться()
    {
        var (jobs, quartz) = await SchedulerAsync();
        var logger = new RecordingLogger<RecurringScheduleService>();

        var applied = await RecurringScheduleService.ApplyCollectionSchedulesAsync(
            [new CollectionSchedule(7, "15 2 * * *"), new CollectionSchedule(8, Hourly)],
            jobs, new CollectionScheduleApplier(jobs), logger, CancellationToken.None);

        Assert.Equal(1, applied);
        Assert.Equal(1, await JobCountAsync(quartz));

        // Оператор має побачити, ЯКИЙ розклад пропущено і чому.
        var error = Assert.Single(logger.OfLevel(LogLevel.Error));
        Assert.Contains("7", error.Message, StringComparison.Ordinal);
        Assert.Contains("15 2 * * *", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(999, 0)]
    [InlineData(1000, 1)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Рівно_тисяча_розкладів_дає_попередження_що_перелік_обрізано(int count, int warnings)
    {
        // ⛔ Стеля — вимога, тому числом, а не константою з того самого модуля.
        Assert.Equal(1000, RecurringScheduleService.MaxCollectionSchedules);

        var (jobs, quartz) = await SchedulerAsync();
        var logger = new RecordingLogger<RecurringScheduleService>();
        var schedules = Enumerable.Range(1, count).Select(i => new CollectionSchedule(i, Hourly)).ToList();

        var applied = await RecurringScheduleService.ApplyCollectionSchedulesAsync(
            schedules, jobs, new CollectionScheduleApplier(jobs), logger, CancellationToken.None);

        Assert.Equal(count, applied);
        Assert.Equal(count, await JobCountAsync(quartz));
        Assert.Equal(warnings, logger.OfLevel(LogLevel.Warning).Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Вимкнений_розклад_знімається_а_новий_cron_замінює_старий()
    {
        var (jobs, quartz) = await SchedulerAsync();
        var applier = new CollectionScheduleApplier(jobs);
        var schedule = new CollectionSchedule(7, Hourly);

        await applier.ApplyAsync(schedule, CancellationToken.None);

        schedule.Reschedule(RecurringScheduleService.NightlyCron);
        await applier.ApplyAsync(schedule, CancellationToken.None);

        var triggerKey = Assert.Single(await quartz.GetTriggerKeys(GroupMatcher<TriggerKey>.AnyGroup()));
        var trigger = Assert.IsAssignableFrom<ICronTrigger>(await quartz.GetTrigger(triggerKey));
        Assert.Equal(RecurringScheduleService.NightlyCron, trigger.CronExpressionString);

        schedule.Disable();
        await applier.ApplyAsync(schedule, CancellationToken.None);
        Assert.Equal(0, await JobCountAsync(quartz));

        schedule.Enable();
        await applier.ApplyAsync(schedule, CancellationToken.None);
        Assert.Equal(1, await JobCountAsync(quartz));
    }
}
