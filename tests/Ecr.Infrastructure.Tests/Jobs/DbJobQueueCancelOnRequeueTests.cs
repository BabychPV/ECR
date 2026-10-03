// tests/Ecr.Infrastructure.Tests/Jobs/DbJobQueueCancelOnRequeueTests.cs
using Ecr.Application.Ports;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// L2-01 (аудит 2026-10-03): запит скасування людини, що прийшов, поки задача
/// була <c>Running</c>, не губиться, коли виконавець повертає її в чергу
/// (відкладення через лок документа чи ретрай після провалу).
/// </summary>
/// <remarks>Мутаційні докази — в описі коміту.</remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class DbJobQueueCancelOnRequeueTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    [Fact]
    public async Task Defer_задачі_з_запитом_скасування_закриває_Cancelled_а_не_Queued()
    {
        var jobId = await EnqueueAsync();
        await using var worker = NewHost();
        await using var api = NewHost();

        var claim = (await worker.ClaimAsync())!.Claim;
        Assert.Equal(CancelOutcome.CancelRequested, await api.Queue.RequestCancelAsync(jobId, CancellationToken.None));

        Assert.True(await worker.Queue.DeferAsync(claim, TimeSpan.Zero, CancellationToken.None));

        Assert.Equal("Cancelled", (await RowAsync(jobId))!.State);
        Assert.Null(await worker.ClaimAsync());
    }

    [Fact]
    public async Task Requeue_задачі_з_запитом_скасування_закриває_Cancelled_а_не_Queued()
    {
        var jobId = await EnqueueAsync();
        await using var worker = NewHost();
        await using var api = NewHost();

        var claim = (await worker.ClaimAsync())!.Claim;
        Assert.Equal(CancelOutcome.CancelRequested, await api.Queue.RequestCancelAsync(jobId, CancellationToken.None));

        Assert.True(await worker.Queue.RequeueAsync(claim, TimeSpan.Zero, CancellationToken.None));

        Assert.Equal("Cancelled", (await RowAsync(jobId))!.State);
        Assert.Null(await worker.ClaimAsync());
    }

    [Fact]
    public async Task Defer_без_запиту_скасування_як_і_раніше_повертає_в_Queued()
    {
        var jobId = await EnqueueAsync();
        await using var worker = NewHost();

        var claim = (await worker.ClaimAsync())!.Claim;
        Assert.True(await worker.Queue.DeferAsync(claim, TimeSpan.Zero, CancellationToken.None));

        Assert.Equal("Queued", (await RowAsync(jobId))!.State);
        Assert.Equal(jobId, (await worker.ClaimAsync())!.Claim.JobId);
    }
}
