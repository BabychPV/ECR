// tests/Ecr.Infrastructure.Tests/Jobs/DbJobQueueCancelOnRequeueTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
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
    [Trait("Finding", "L2-01")]
    public async Task Requeue_задачі_з_запитом_скасування_знімає_повідомлення_про_ретрай_з_рядка_Cancelled()
    {
        var jobId = await EnqueueAsync();
        await using var worker = NewHost();
        await using var api = NewHost();

        var claim = (await worker.ClaimAsync())!.Claim;
        await ReportRetryMessageAsync(jobId);
        Assert.Equal(CancelOutcome.CancelRequested, await api.Queue.RequestCancelAsync(jobId, CancellationToken.None));

        Assert.True(await worker.Queue.RequeueAsync(claim, TimeSpan.Zero, CancellationToken.None));

        // ⛔ Без CASE у UPDATE на Cancelled-рядку лишається «повтор заплановано».
        var row = (await RowAsync(jobId))!;
        Assert.Equal("Cancelled", row.State);
        Assert.Null(row.Message);
    }

    [Fact]
    [Trait("Finding", "L2-01")]
    public async Task Requeue_без_запиту_скасування_лишає_повідомлення_про_ретрай()
    {
        var jobId = await EnqueueAsync();
        await using var worker = NewHost();

        var claim = (await worker.ClaimAsync())!.Claim;
        await ReportRetryMessageAsync(jobId);

        Assert.True(await worker.Queue.RequeueAsync(claim, TimeSpan.Zero, CancellationToken.None));

        var row = (await RowAsync(jobId))!;
        Assert.Equal("Queued", row.State);
        Assert.NotNull(row.Message);
        Assert.Contains("jobs.retryScheduled", row.Message, StringComparison.Ordinal);
    }

    /// <summary>Те, що воркер пише перед <c>RequeueAsync</c> (<c>JobWorker</c>, гілка ретраю).</summary>
    private async Task ReportRetryMessageAsync(string jobId)
    {
        await using var db = Sql.CreateContext();
        var message = JobRetryPolicy.RetryScheduledMessage(1, TimeSpan.FromSeconds(30), new InvalidOperationException("збій"), "corr-1");
        await new JobProgressStore(db).ReportAsync(jobId, 0, message, DateTime.UtcNow, CancellationToken.None);
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
