// tests/Ecr.Infrastructure.Tests/Jobs/RecurringUnscheduleTests.cs
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Періодичну задачу можна зняти й перепланувати без перезапуску (ФВ-14.3).
/// </summary>
/// <remarks>
/// ⚠ Планувальник справжній, але НЕ запущений (як у
/// <see cref="ExclusiveEnqueueTests"/>): перевіряється сховище задач і
/// тригерів, бази не треба.
/// </remarks>
public sealed class RecurringUnscheduleTests
{
    private const string Hourly = "0 5 * * * ?";
    private const string Nightly = "0 15 2 * * ?";

    private static readonly CollectionTask Payload = new(41, null, null);

    private static async Task<(QuartzJobScheduler Jobs, IScheduler Quartz)> SchedulerAsync()
    {
        var factory = new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-tests-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
        });

        return (new QuartzJobScheduler(factory), await factory.GetScheduler().ConfigureAwait(false));
    }

    private static async Task<List<ICronTrigger>> CronTriggersAsync(IScheduler quartz)
    {
        var result = new List<ICronTrigger>();

        foreach (var key in await quartz.GetTriggerKeys(GroupMatcher<TriggerKey>.AnyGroup()))
        {
            if (await quartz.GetTrigger(key) is ICronTrigger cron)
            {
                result.Add(cron);
            }
        }

        return result;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Зняття_прибирає_тригер_а_повторне_каже_що_знімати_нічого()
    {
        var (jobs, quartz) = await SchedulerAsync();

        await jobs.ScheduleAsync<ICollectionJob>(Hourly, Payload, CancellationToken.None);
        Assert.Single(await CronTriggersAsync(quartz));

        // ⛔ Payload — НОВИЙ рівний екземпляр, як і в житті: ключ мусить
        // рахуватися зі змісту, і так само, як його рахує ScheduleAsync.
        var removed = await jobs.UnscheduleAsync<ICollectionJob>(
            new CollectionTask(41, null, null), CancellationToken.None);

        Assert.True(removed);
        Assert.Empty(await CronTriggersAsync(quartz));
        Assert.Empty(await quartz.GetJobKeys(GroupMatcher<JobKey>.AnyGroup()));

        Assert.False(await jobs.UnscheduleAsync<ICollectionJob>(Payload, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Зняття_одного_розкладу_не_чіпає_сусіднього()
    {
        var (jobs, quartz) = await SchedulerAsync();

        await jobs.ScheduleAsync<ICollectionJob>(Hourly, Payload, CancellationToken.None);
        await jobs.ScheduleAsync<ICollectionJob>(Hourly, new CollectionTask(42, null, null), CancellationToken.None);

        await jobs.UnscheduleAsync<ICollectionJob>(Payload, CancellationToken.None);

        Assert.Single(await quartz.GetJobKeys(GroupMatcher<JobKey>.AnyGroup()));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Новий_cron_на_тому_самому_payload_замінює_тригер_а_не_додає_другий()
    {
        var (jobs, quartz) = await SchedulerAsync();

        await jobs.ScheduleAsync<ICollectionJob>(Hourly, Payload, CancellationToken.None);
        await jobs.ScheduleAsync<ICollectionJob>(Nightly, Payload, CancellationToken.None);

        var trigger = Assert.Single(await CronTriggersAsync(quartz));
        Assert.Equal(Nightly, trigger.CronExpressionString);
    }

    [Theory]
    [InlineData("15 2 * * *")] // unix-cron із 5 полів
    [InlineData("0 15 2 * * *")] // обидва поля дня задані — Quartz вимагає «?»
    [InlineData("щоночі")]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Невалідний_cron_відхиляється_і_чинний_розклад_лишається(string cron)
    {
        var (jobs, quartz) = await SchedulerAsync();
        await jobs.ScheduleAsync<ICollectionJob>(Hourly, Payload, CancellationToken.None);

        Assert.False(QuartzJobScheduler.IsValidCron(cron, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => jobs.ScheduleAsync<ICollectionJob>(cron, Payload, CancellationToken.None));
        Assert.Contains(cron, ex.Message, StringComparison.Ordinal);

        // ⛔ Відмова ДО Quartz: невдала правка не має знімати те, що працює.
        var trigger = Assert.Single(await CronTriggersAsync(quartz));
        Assert.Equal(Hourly, trigger.CronExpressionString);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Шестипольний_cron_проєкту_валідний()
    {
        Assert.True(QuartzJobScheduler.IsValidCron(Nightly, out var error));
        Assert.Null(error);
    }
}
