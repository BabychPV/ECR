// tests/Ecr.Infrastructure.Tests/Jobs/DbJobQueueQueuedByTargetTests.cs
using Ecr.Application.Ports;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Перф-аудит 20.11: пошук і скасування задачі, що ЧЕКАЄ на ціль (<c>Queued</c>), без дотику до виконуваної —
/// основа «затверджені залежні покривають звичайні» (<c>CalculationTrigger.RequestDependentsAsync</c>).
/// </summary>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class DbJobQueueQueuedByTargetTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{

    [Fact]
    public async Task Знаходить_і_скасовує_лише_Queued_на_ціль_виконувана_лишається()
    {
        var target = Target();
        var other = Target();
        var running = await EnqueueAsync(target);
        await using var host = NewHost();
        await host.ClaimAsync();
        var waiting = await EnqueueAsync(target, delay: TimeSpan.FromHours(1));
        var unrelated = await EnqueueAsync(other, delay: TimeSpan.FromHours(1));

        Assert.Equal(waiting, await host.Queue.FindQueuedByTargetAsync(target, CancellationToken.None));
        Assert.Null(await host.Queue.FindQueuedByTargetAsync($"{Code}~nope-{Guid.NewGuid():N}", CancellationToken.None));

        Assert.True(await host.Queue.CancelQueuedByTargetAsync(target, CancellationToken.None));

        Assert.Equal("Cancelled", (await RowAsync(waiting))!.State);
        var runningRow = await RowAsync(running);
        Assert.Equal("Running", runningRow!.State);
        Assert.Null(runningRow.CancelRequestedAt);
        Assert.Equal("Queued", (await RowAsync(unrelated))!.State);
        Assert.Null(await host.Queue.FindQueuedByTargetAsync(target, CancellationToken.None));
        Assert.False(await host.Queue.CancelQueuedByTargetAsync(target, CancellationToken.None));
    }
}
