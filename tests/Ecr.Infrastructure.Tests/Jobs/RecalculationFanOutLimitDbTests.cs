// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationFanOutLimitDbTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ PSS-02 (аудит 20.11, <c>D-561</c>): у режимі черги в базі дітей розкладу перерахунку проєкту
/// (<c>FanOutParentJobId</c>) одночасно виконується не більше <c>Jobs:Recalculation:FanOutMaxConcurrency</c> — на весь пул
/// процесів разом; ручний перерахунок, автоперерахунок і залежні межі не бачать і обганяють дітей, що чекають.
/// </summary>
/// <remarks>
/// Справжня конкуренція: десять «процесів» (окремі контексти й з'єднання) роблять claim одночасно, бар'єр — лише
/// <see cref="Task.WhenAll(System.Collections.Generic.IEnumerable{Task})"/>, без затримок.
/// <para>
/// МУТАЦІЇ (локально): (1) без <c>FanOutCapFilter</c> у <c>ClaimQueuedSqlFor</c> — червоні всі три (дітей Running 10);
/// (2) без applock у <c>FanOutCapPrologue</c> (лічильник без серіалізації) — <c>Десять_процесів…</c> червоний у
/// частині прогонів (5–6 дітей), тому доказ строгості — 20 прогонів поспіль.
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "PSS-02")]
public sealed class RecalculationFanOutLimitDbTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private const int Cap = 4;

    private const string Parent = "IRecalculationJob-project-parent-under-test";

    [Fact]
    public async Task Десять_процесів_одночасно_беруть_не_більше_межі_дітей_а_ручний_проходить_повз()
    {
        for (var i = 0; i < 10; i++)
        {
            await EnqueueRecalcAsync(documentId: 9_100_000 + i, parent: Parent);
        }

        var manual = await EnqueueRecalcAsync(documentId: 9_199_999, parent: null);

        var hosts = Enumerable.Range(0, 10).Select(_ => CappedHost()).ToList();
        try
        {
            var claimed = await Task.WhenAll(hosts.Select((h, i) => ClaimAsync(h, $"host/{i}")));

            var children = await RunningChildrenAsync();
            Assert.Equal(Cap, children);

            // Ручний (без батька) узятий, хоча стояв у черзі ПІСЛЯ всіх десяти дітей.
            Assert.Contains(manual, claimed.Where(c => c is not null).Select(c => c!.Claim.JobId));
            Assert.Equal(Cap + 1, claimed.Count(c => c is not null));
        }
        finally
        {
            foreach (var host in hosts)
            {
                await host.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Місце_дитини_звільнилось_наступна_дитина_береться_за_FIFO()
    {
        var children = new List<string>();
        for (var i = 0; i < Cap + 2; i++)
        {
            children.Add(await EnqueueRecalcAsync(documentId: 9_200_000 + i, parent: Parent));
        }

        await using var host = CappedHost();
        var running = new List<ClaimedJob>();
        for (var i = 0; i < Cap; i++)
        {
            running.Add((await ClaimAsync(host, "host/a"))!);
        }

        Assert.Equal(children.Take(Cap), running.Select(r => r.Claim.JobId));
        Assert.Null(await ClaimAsync(host, "host/a"));

        // Автоперерахунок (не дитина), поставлений пізніше за дітей, — береться попри межу.
        var auto = await EnqueueRecalcAsync(documentId: 9_299_999, parent: null);
        Assert.Equal(auto, (await ClaimAsync(host, "host/a"))?.Claim.JobId);

        Assert.True(await host.Queue.CompleteAsync(running[0].Claim, CancellationToken.None));
        Assert.Equal(children[Cap], (await ClaimAsync(host, "host/a"))?.Claim.JobId);
        Assert.Null(await ClaimAsync(host, "host/a"));
    }

    [Fact]
    public async Task Без_межі_у_збірці_черга_як_доти_а_межа_рахує_лише_свій_лейн()
    {
        for (var i = 0; i < Cap + 1; i++)
        {
            await EnqueueRecalcAsync(documentId: 9_300_000 + i, parent: Parent);
        }

        // Ручна збірка без межі (тести, утиліти) — береться все.
        await using (var unlimited = NewHost())
        {
            for (var i = 0; i < Cap + 1; i++)
            {
                Assert.NotNull(await ClaimAsync(unlimited, "host/u"));
            }
        }

        // Межа вже вичерпана (5 дітей Running) — нова дитина з межею не береться, а задача лейна default з батьком
        // (не перерахунок) межі не бачить.
        await EnqueueRecalcAsync(documentId: 9_399_999, parent: Parent);
        await using var capped = CappedHost();
        Assert.Null(await ClaimAsync(capped, "host/c"));

        await using (var enqueue = NewHost())
        {
            await enqueue.Queue.EnqueueAsync(
                new JobEnqueueRequest(Code, JobLanes.Default, $"{{\"fanOutParentJobId\":\"{Parent}\"}}"), CancellationToken.None);
        }

        Assert.NotNull(await capped.Queue.ClaimAsync([JobLanes.Default], "host/c", JobQueueLimits.DefaultLease, CancellationToken.None));
    }

    private Host CappedHost()
    {
        var db = Sql.CreateContext();
        return new Host(db, new DbJobQueue(db, new SystemClock(), new QuartzJobTypeLimiter(2, recalcFanOutMaxConcurrency: Cap)));
    }

    private static Task<ClaimedJob?> ClaimAsync(Host host, string owner)
        => host.Queue.ClaimAsync([JobLanes.Recalc], owner, JobQueueLimits.DefaultLease, CancellationToken.None);

    private async Task<string> EnqueueRecalcAsync(long documentId, string? parent)
    {
        await using var host = NewHost();
        var payload = parent is null
            ? $"{{\"documentId\":{documentId}}}"
            : $"{{\"documentId\":{documentId},\"fanOutParentJobId\":\"{parent}\"}}";
        return (await host.Queue.EnqueueAsync(
                new JobEnqueueRequest(Code, JobLanes.Recalc, payload, DocumentId: documentId), CancellationToken.None))
            .JobId;
    }

    private async Task<int> RunningChildrenAsync()
    {
        await using var db = Sql.CreateContext();
        return await db.JobProgresses.AsNoTracking()
            .CountAsync(p => p.Lane == JobLanes.Recalc && p.State == "Running" && p.FanOutParentJobId != null);
    }
}
