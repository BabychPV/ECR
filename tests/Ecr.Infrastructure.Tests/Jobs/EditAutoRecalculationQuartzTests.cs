// tests/Ecr.Infrastructure.Tests/Jobs/EditAutoRecalculationQuartzTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ D-577 (Q2) у режимі Quartz (типовий на live): автоперерахунок після правки не дає шторму — сплеск постановок на ціль
/// <c>-auto</c> (масова вставка чи імпорт) зливається в ОДНУ задачу, що стартує не раніше <see cref="CalculationTrigger.EditDebounce"/>
/// після першої постановки.
/// </summary>
/// <remarks>
/// Мутація (локально): у <c>QuartzJobScheduler.EnqueueCoalescedDelayedAsync</c> повернути делегування в
/// <c>EnqueueCoalescedAsync</c> (без затримки) — тест червоний (тригер стартує одразу).
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait("Requirement", "D-577")]
public sealed class EditAutoRecalculationQuartzTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

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
    public async Task Q2_Quartz_сплеск_постановок_одна_задача_з_відкладеним_стартом()
    {
        var factory = new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-tests-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
        });
        quartz = await factory.GetScheduler();
        var jobs = new QuartzJobScheduler(factory, clock: new FixedClock(Now));
        var target = CalculationTrigger.AutoTargetOf(9_577_101, new Domain.ValueObjects.PeriodKey(202604));

        var first = await jobs.EnqueueCoalescedDelayedAsync<IRecalculationJob>(
            target, new { DocumentId = 9_577_101L, PeriodKey = 202604 }, CalculationTrigger.EditDebounce, CancellationToken.None);
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(first, await jobs.EnqueueCoalescedDelayedAsync<IRecalculationJob>(
                target, new { DocumentId = 9_577_101L, PeriodKey = 202604 }, CalculationTrigger.EditDebounce, CancellationToken.None));
        }

        var keys = await quartz.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());
        Assert.Equal(first, Assert.Single(keys).Name);
        var trigger = Assert.Single(await quartz.GetTriggersOfJob(new JobKey(first)));
        Assert.Equal(new DateTimeOffset(Now + CalculationTrigger.EditDebounce, TimeSpan.Zero), trigger.StartTimeUtc);
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
