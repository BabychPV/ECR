// tests/Ecr.Infrastructure.Tests/Jobs/DbBackgroundJobSchedulerTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// <see cref="DbBackgroundJobScheduler"/>: лейн за типом задачі, ціль і витіснення
/// для <c>EnqueueExclusiveAsync</c> (MI-02, F1c).
/// </summary>
/// <remarks>
/// Мутації: <see cref="JobLaneMap.Of(Type)"/> завжди Default — перший тест
/// червоний (перерахунок у лейні, якого не опитує пул <c>D-206</c>);
/// <c>supersede: false</c> в <c>EnqueueExclusiveAsync</c> — другий червоний.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class DbBackgroundJobSchedulerTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    [Fact]
    public async Task Перерахунок_іде_в_лейн_recalc_решта_в_default_з_автором_і_документом()
    {
        await using var host = NewHost();
        var jobs = Scheduler(host);

        var recalc = await jobs.EnqueueAsync<IRecalculationJob>(new { documentId = 5L }, CancellationToken.None, 17);
        var formulas = await jobs.EnqueueAsync<IFormulaRecalculationJob>(new { n = 1 }, CancellationToken.None);

        var recalcRow = await RowAsync(recalc);
        Assert.Equal(JobLanes.Recalc, recalcRow?.Lane);
        Assert.Equal(typeof(IRecalculationJob).FullName, recalcRow?.JobCode);
        Assert.Equal(17, recalcRow?.CreatedByUserId);
        Assert.Equal(5L, recalcRow?.DocumentId);
        Assert.StartsWith(nameof(IRecalculationJob) + "-", recalc, StringComparison.Ordinal);

        Assert.Equal(JobLanes.Default, (await RowAsync(formulas))?.Lane);
        Assert.Equal(JobLanes.Recalc, JobLaneMap.Of<RecalculationJob>());
    }

    [Fact]
    public async Task Exclusive_зливається_з_Queued_на_ціль_і_просить_скасувати_Running()
    {
        await using var host = NewHost();
        var jobs = Scheduler(host);

        var first = await jobs.EnqueueExclusiveAsync<IRecalculationJob>("doc5~p202609", new { n = 1 }, CancellationToken.None);
        Assert.Equal($"{nameof(IRecalculationJob)}~doc5_p202609", (await RowAsync(first))?.TargetKey);

        // Та сама ціль, поки перша ще Queued, — та сама задача (коалесценція).
        Assert.Equal(first, await jobs.EnqueueExclusiveAsync<IRecalculationJob>(
            "doc5~p202609", new { n = 2 }, CancellationToken.None));

        var claimed = await host.Queue.ClaimAsync(
            [JobLanes.Recalc], "test/host", JobQueueLimits.DefaultLease, CancellationToken.None);
        Assert.Equal(first, claimed?.Claim.JobId);

        // Ціль уже Running: нова задача стає позаду, а Running отримує запит скасування (H-23c).
        var second = await jobs.EnqueueExclusiveAsync<IRecalculationJob>("doc5~p202609", new { n = 3 }, CancellationToken.None);

        Assert.NotEqual(first, second);
        Assert.Equal("Queued", (await RowAsync(second))?.State);
        Assert.NotNull((await RowAsync(first))?.CancelRequestedAt);
    }

    private static DbBackgroundJobScheduler Scheduler(Host host)
        => new(
            host.Queue,
            new QuartzJobScheduler(null, new JobProgressStore(host.Db), new SystemClock()),
            new JobQueueSignal());
}
