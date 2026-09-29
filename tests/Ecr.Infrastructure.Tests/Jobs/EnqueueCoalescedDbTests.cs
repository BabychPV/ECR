// tests/Ecr.Infrastructure.Tests/Jobs/EnqueueCoalescedDbTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// <see cref="DbBackgroundJobScheduler.EnqueueCoalescedAsync{TJob}"/>: <c>Queued</c> позаду
/// <c>Running</c> без запиту скасування, повторні постановки зливаються (HSE301 A4).
/// </summary>
/// <remarks>
/// Мутація: <c>supersede: true</c> в <c>EnqueueCoalescedAsync</c> — червоний
/// (<c>CancelRequestedAt</c> у Running заповнено).
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class EnqueueCoalescedDbTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    [Fact]
    public async Task Running_на_ціль_не_скасовується_позаду_одна_Queued_яка_поглинає_повтори()
    {
        await using var host = NewHost();
        var jobs = Scheduler(host);

        var running = await jobs.EnqueueCoalescedAsync<IRecalculationJob>("doc5~p202609", new { n = 1 }, CancellationToken.None);
        var targetKey = $"{nameof(IRecalculationJob)}~doc5_p202609";
        Assert.Equal(targetKey, (await RowAsync(running))?.TargetKey);

        var claimed = await host.Queue.ClaimAsync(
            [JobLanes.Recalc], "test/host", JobQueueLimits.DefaultLease, CancellationToken.None);
        Assert.Equal(running, claimed?.Claim.JobId);

        var behind = await jobs.EnqueueCoalescedAsync<IRecalculationJob>("doc5~p202609", new { n = 2 }, CancellationToken.None);

        Assert.NotEqual(running, behind);
        Assert.Equal("Queued", (await RowAsync(behind))?.State);

        var runningRow = await RowAsync(running);
        Assert.Equal("Running", runningRow?.State);

        // ⛔ Регресія: постановка «після» просила б скасувати перерахунок, що йде.
        Assert.Null(runningRow?.CancelRequestedAt);

        Assert.Equal(behind, await jobs.EnqueueCoalescedAsync<IRecalculationJob>("doc5~p202609", new { n = 3 }, CancellationToken.None));
        Assert.Equal(behind, await jobs.EnqueueCoalescedAsync<IRecalculationJob>("doc5~p202609", new { n = 4 }, CancellationToken.None));

        await using var db = Sql.CreateContext();
        Assert.Equal(1, await db.JobProgresses.CountAsync(p => p.TargetKey == targetKey && p.State == "Queued"));
        Assert.Null((await RowAsync(running))?.CancelRequestedAt);
    }

    private static DbBackgroundJobScheduler Scheduler(Host host)
        => new(
            host.Queue,
            new QuartzJobScheduler(null, new JobProgressStore(host.Db), new SystemClock()),
            new JobQueueSignal());
}
