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

        Assert.True(await host.Queue.CancelQueuedByTargetAsync(target, "covering-job-1", CancellationToken.None));

        var cancelled = await RowAsync(waiting);
        Assert.Equal("Cancelled", cancelled!.State);

        // Конверт із JobId покривної — у переліку задач видно, ЧОМУ скасовано.
        Assert.True(JobProgressMessageCodec.TryDecode(cancelled.Message, out var envelope), cancelled.Message);
        Assert.Equal(IJobQueue.CoveredByMessageKey, envelope.Key);
        Assert.Equal("covering-job-1", envelope.Params?["jobId"]);

        var runningRow = await RowAsync(running);
        Assert.Equal("Running", runningRow!.State);
        Assert.Null(runningRow.CancelRequestedAt);
        Assert.Null(runningRow.Message);
        Assert.Equal("Queued", (await RowAsync(unrelated))!.State);
        Assert.Null(await host.Queue.FindQueuedByTargetAsync(target, CancellationToken.None));
        Assert.False(await host.Queue.CancelQueuedByTargetAsync(target, "covering-job-1", CancellationToken.None));
    }

    /// <summary>Рядок дзеркала Quartz (<c>Lane IS NULL</c>) — не задача черги: ні пошук, ні скасування його не бачать.</summary>
    [Fact]
    public async Task Рядок_без_лейна_не_знаходиться_й_не_скасовується()
    {
        var target = Target();
        var jobId = $"quartz-mirror-{Guid.NewGuid():N}";
        await ExecAsync(
            $"""
            INSERT INTO itg.JobProgress (JobId, JobCode, [State], [Percent], StartedAt, UpdatedAt, HeartbeatAt, CreatedAt, TargetKey)
            VALUES (@id, N'{Code}', 'Queued', 0, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(), N'{target}');
            """,
            jobId);

        try
        {
            await using var host = NewHost();
            Assert.Null(await host.Queue.FindQueuedByTargetAsync(target, CancellationToken.None));
            Assert.False(await host.Queue.CancelQueuedByTargetAsync(target, "covering", CancellationToken.None));
            Assert.Equal("Queued", (await RowAsync(jobId))!.State);
        }
        finally
        {
            await ExecAsync("DELETE FROM itg.JobProgress WHERE JobId = @id;", jobId);
        }
    }

    /// <summary>
    /// Закриття прострочених оренд повертає <c>JobId</c> закритих рядків (B80 N5: виконавець повертає роботу, яку
    /// покривала закрита «залежні з затвердженими»).
    /// </summary>
    [Fact]
    public async Task Закриття_прострочених_оренд_повертає_закриті_задачі()
    {
        var cancelledJob = await EnqueueAsync();
        await using var dead = NewHost();
        await dead.ClaimAsync("host/dead");
        Assert.Equal(CancelOutcome.CancelRequested, await dead.Queue.RequestCancelAsync(cancelledJob, CancellationToken.None));
        await ExpireLeaseAsync(cancelledJob);

        await using var host = NewHost();
        var ended = await host.Queue.ExpireEndedAsync(JobQueueLimits.MaxReclaims, CancellationToken.None);

        Assert.Equal([cancelledJob], ended);
        Assert.Equal("Cancelled", (await RowAsync(cancelledJob))!.State);
        Assert.Empty(await host.Queue.ExpireEndedAsync(JobQueueLimits.MaxReclaims, CancellationToken.None));
    }
}
