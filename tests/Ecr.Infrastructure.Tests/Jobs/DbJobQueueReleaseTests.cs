// tests/Ecr.Infrastructure.Tests/Jobs/DbJobQueueReleaseTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// L2-08 (аудит 2026-10-03): повернення в чергу при зупинці хоста — подія
/// життєвого циклу (D-208), а не провал: спроба ретраю не з'їдається, відлік
/// стелі відкладень не обнуляється.
/// </summary>
/// <remarks>Мутаційні докази — в описі коміту.</remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class DbJobQueueReleaseTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    [Fact]
    public async Task Повернення_при_зупинці_не_зараховує_спробу()
    {
        var jobId = await EnqueueAsync();
        await using var host = NewHost();

        var first = await host.ClaimAsync();
        Assert.Equal(1, first!.Attempt);
        Assert.True(await host.Queue.ReleaseAsync(first.Claim, CancellationToken.None));
        Assert.Equal("Queued", (await RowAsync(jobId))!.State);

        var second = await host.ClaimAsync();
        Assert.Equal((jobId, 1), (second!.Claim.JobId, second.Attempt));
    }

    [Fact]
    public async Task Повернення_при_зупинці_не_знімає_відліку_відкладень()
    {
        var jobId = await EnqueueAsync();
        await using var host = NewHost();

        // Задача вже відкладалась: відлік стелі поставлено.
        var first = await host.ClaimAsync();
        Assert.True(await host.Queue.DeferAsync(first!.Claim, TimeSpan.Zero, CancellationToken.None));
        var since = JobDeferral.SinceOf((await RowAsync(jobId))!.Payload);
        Assert.NotNull(since);

        var second = await host.ClaimAsync();
        Assert.True(await host.Queue.ReleaseAsync(second!.Claim, CancellationToken.None));

        Assert.Equal(since, JobDeferral.SinceOf((await RowAsync(jobId))!.Payload));
    }

    [Fact]
    public async Task Повернення_без_відкладень_не_ставить_відліку()
    {
        var jobId = await EnqueueAsync();
        await using var host = NewHost();

        var first = await host.ClaimAsync();
        Assert.True(await host.Queue.ReleaseAsync(first!.Claim, CancellationToken.None));

        Assert.Null(JobDeferral.SinceOf((await RowAsync(jobId))!.Payload));
    }
}
