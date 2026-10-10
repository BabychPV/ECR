// tests/Ecr.Infrastructure.Tests/Jobs/JobLaneFairnessTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// J1-04: claim перебирає лейни в порядку ВИКЛИКАЧА, а основний цикл воркера чергує цей порядок —
/// під сталим потоком <c>interactive</c> лейн <c>default</c> не голодує.
/// </summary>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "J1-04")]
public sealed class JobLaneFairnessTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    [Fact]
    public async Task Claim_поважає_порядок_лейнів_викликача()
    {
        await using var host = NewHost();
        for (var i = 0; i < 3; i++)
        {
            await host.Queue.EnqueueAsync(new JobEnqueueRequest(Code, JobLanes.Interactive, "{}", null), CancellationToken.None);
        }

        var plain = await EnqueueAsync();

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: повернути `JobLanes.All.Where(...)` у `DbJobQueue.ClaimAsync` — береться
        // задача з interactive, хоча викликач поставив default першим.
        var claimed = await host.Queue.ClaimAsync(
            [JobLanes.Default, JobLanes.Interactive], "test/host", JobQueueLimits.DefaultLease, CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(plain, claimed.Claim.JobId);

        // Наступний claim у тому самому порядку, коли default порожній, — бере interactive.
        var next = await host.Queue.ClaimAsync(
            [JobLanes.Default, JobLanes.Interactive], "test/host", JobQueueLimits.DefaultLease, CancellationToken.None);
        Assert.NotNull(next);
        Assert.NotEqual(plain, next.Claim.JobId);
    }

    [Fact]
    public void Основний_цикл_чергує_лейни_по_колу()
    {
        string[] lanes = [JobLanes.Interactive, JobLanes.Default, JobLanes.Excel];

        Assert.Equal(lanes, JobWorker.Rotate(lanes, 0));
        Assert.Equal([JobLanes.Default, JobLanes.Excel, JobLanes.Interactive], JobWorker.Rotate(lanes, 1));
        Assert.Equal([JobLanes.Excel, JobLanes.Interactive, JobLanes.Default], JobWorker.Rotate(lanes, 2));
        Assert.Equal(lanes, JobWorker.Rotate(lanes, 3));

        // За 10 claim під сталим interactive default стоїть першим не рідше ніж кожен другий раз
        // для пари лейнів Api — тобто голодувати не може.
        string[] api = [JobLanes.Interactive, JobLanes.Default];
        var defaultFirst = Enumerable.Range(0, 10).Count(t => JobWorker.Rotate(api, t)[0] == JobLanes.Default);
        Assert.Equal(5, defaultFirst);
    }
}
