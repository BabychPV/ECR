// tests/Ecr.Api.Tests/JobsHealthCacheTests.cs
using Ecr.Api.Health;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Quartz;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Анонімний <c>/health/ready</c> не має ходити в сховище на кожну пробу: результат
/// <see cref="JobsHealthCheck"/> живе <see cref="HealthResultCache.Ttl"/>, не довше.
/// </summary>
public sealed class JobsHealthCacheTests
{
    private static readonly HealthCheckContext Context = new()
    {
        Registration = new HealthCheckRegistration("jobs", Substitute.For<IHealthCheck>(), null, null),
    };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Два_виклики_в_межах_TTL_ходять_у_сховище_один_раз_а_після_TTL_удруге()
    {
        var time = new ManualTime();
        var (check, store) = Build(new HealthResultCache(time, TimeSpan.FromSeconds(5)));

        await check.CheckHealthAsync(Context, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(4));
        await check.CheckHealthAsync(Context, CancellationToken.None);

        await store.Received(1).SummarizeStaleAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());

        time.Advance(TimeSpan.FromSeconds(2));
        await check.CheckHealthAsync(Context, CancellationToken.None);

        await store.Received(2).SummarizeStaleAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Degraded_не_ховається_довше_за_TTL()
    {
        var time = new ManualTime();
        var (check, store) = Build(new HealthResultCache(time, TimeSpan.FromSeconds(5)));

        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(Context, CancellationToken.None)).Status);

        store.SummarizeStaleAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new StaleJobsSummary(1, DateTime.UtcNow));
        time.Advance(TimeSpan.FromSeconds(6));

        Assert.Equal(HealthStatus.Degraded, (await check.CheckHealthAsync(Context, CancellationToken.None)).Status);
    }

    private static (JobsHealthCheck Check, IJobProgressStore Store) Build(HealthResultCache cache)
    {
        var scheduler = Substitute.For<IScheduler>();
        scheduler.IsStarted.Returns(true);
        scheduler.IsShutdown.Returns(false);
        scheduler.GetJobKeys(Arg.Any<Quartz.Impl.Matchers.GroupMatcher<JobKey>>(), Arg.Any<CancellationToken>())
            .Returns(new List<JobKey> { new("job") });
        scheduler.GetTriggerKeys(Arg.Any<Quartz.Impl.Matchers.GroupMatcher<TriggerKey>>(), Arg.Any<CancellationToken>())
            .Returns(new List<TriggerKey> { new("trigger") });

        var factory = Substitute.For<ISchedulerFactory>();
        factory.GetScheduler(Arg.Any<CancellationToken>()).Returns(scheduler);

        var user = Substitute.For<ICurrentUser>();
        user.Language.Returns("en");

        var store = Substitute.For<IJobProgressStore>();
        store.SummarizeStaleAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new StaleJobsSummary(0, null));

        var check = new JobsHealthCheck(
            factory, new FakeUiStringCatalog(), user, store, new TestClock(DateTime.UtcNow), cache);
        return (check, store);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2036, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
