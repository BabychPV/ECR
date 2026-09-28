// tests/Ecr.Api.Tests/JobsHealthStaleTests.cs
using Ecr.Api.Health;
using Ecr.Api.Startup;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Quartz;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// U16: перевірка <c>jobs</c> бачить завислі задачі, а періодичне прибирання
/// (<see cref="RecurringScheduleService.SweepOnceAsync"/>) їх закриває.
/// </summary>
/// <remarks>
/// ⚠ Справжня база і справжній <see cref="JobProgressStore"/>; планувальник —
/// заглушка «запущено, розклад є» (як у <c>HealthCatalogLocalizationTests</c>):
/// справжній Quartz у спільному прогоні Api тягне статичний <c>LogProvider</c>
/// закритого хоста.
/// <para>
/// ⚠ Лічильник завислих — глобальний, тож момент тут у 2032 році, а перед
/// перевіркою йде прохід прибирання «на чисто»: усе чуже на той час
/// гарантовано застаріле й закривається ним.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class JobsHealthStaleTests(SqlServerFixture sql)
{
    private static readonly DateTime T = new(2032, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private static readonly HealthCheckContext Context = new()
    {
        Registration = new HealthCheckRegistration("jobs", Substitute.For<IHealthCheck>(), null, null),
    };

    /// <remarks>
    /// Мутації (прогнано): (1) прибрати блок завислих у <c>JobsHealthCheck</c> →
    /// «до» зелений, червоний; (2) не викликати <c>FailStaleAsync</c> у
    /// <c>AbandonedWorkSweeper</c> → прохід не закриває нічого, червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Завислі_задачі_жовтять_і_червонять_jobs_а_прибирання_повертає_зелений()
    {
        await SweepAtAsync(T);

        var jobId = $"hung-{Guid.NewGuid():N}";
        await using (var db = sql.CreateContext())
        {
            await new JobProgressStore(db).StartAsync(jobId, "Ecr.Test.HungJob", T.AddMinutes(-6), CancellationToken.None);
        }

        // Шість хвилин без биття — чекає найближчого проходу: жовтий.
        var waiting = await CheckAtAsync(T);
        Assert.Equal(HealthStatus.Degraded, waiting.Status);
        Assert.Equal(1, waiting.Data["staleJobs"]);

        // Шістнадцять — прибирання мало встигнути й не встигло: червоний.
        Assert.Equal(HealthStatus.Unhealthy, (await CheckAtAsync(T.AddMinutes(10))).Status);

        var outcome = await SweepAtAsync(T.AddMinutes(10));
        Assert.True(outcome.Jobs >= 1);

        Assert.Equal(HealthStatus.Healthy, (await CheckAtAsync(T.AddMinutes(10))).Status);

        await using var check = sql.CreateContext();
        Assert.Equal("Failed", (await new JobProgressStore(check).FindAsync(jobId, CancellationToken.None))?.State);
    }

    private async Task<Infrastructure.Jobs.SweepOutcome> SweepAtAsync(DateTime at)
    {
        var services = new ServiceCollection();
        services.AddDbContext<EcrDbContext>(o => o.UseSqlServer(sql.ConnectionString));
        services.AddScoped<IJobProgressStore, JobProgressStore>();
        services.AddSingleton<Domain.Abstractions.IClock>(new TestClock(at));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        return await RecurringScheduleService.SweepOnceAsync(scope.ServiceProvider, purge: false, CancellationToken.None);
    }

    private async Task<HealthCheckResult> CheckAtAsync(DateTime at)
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

        await using var db = sql.CreateContext();

        return await new JobsHealthCheck(
                factory, new FakeUiStringCatalog(), user, new JobProgressStore(db), new TestClock(at))
            .CheckHealthAsync(Context, CancellationToken.None);
    }
}
