// tests/Ecr.Infrastructure.Tests/Jobs/NightlyRecalculationQueueTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ PSS-15 (<c>D-562</c>): нічний розклад іде через чергу L1 як СИСТЕМНА робота — діти з позначкою батька й без автора,
/// тож стеля дітей розкладу (<c>D-561</c>) їх тримає, а останнє місце виконавця лишається ручному й авто.
/// </summary>
/// <remarks>
/// МУТАЦІЯ (локально): у <c>RecalculationJob.NightlyFanOutAsync</c> позначка батька лише з каналу прогресу (без запасної
/// <c>IRecalculationJob-nightly-p{id}</c>) — діти без <c>FanOutParentJobId</c>: останнє місце бере дитину, а не ручний,
/// і дітей виконується шість — червоний.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "PSS-15")]
public sealed class NightlyRecalculationQueueTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private const int Cap = 4;

    private static readonly DateTime Now = new(2026, 3, 10, 0, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Нічні_діти_під_стелею_а_ручний_бере_останнє_місце()
    {
        var builder = new TestDocumentBuilder(Sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey: 202601);
        await AddDocumentsAsync(builder, document, count: 5);

        // Канал прогресу БЕЗ ідентифікатора задачі: позначка батька однаково мусить бути.
        await using (var db = builder.CreateContext())
        {
            var jobs = new DbBackgroundJobScheduler(
                new DbJobQueue(db, new SystemClock()),
                new QuartzJobScheduler(null, new JobProgressStore(db), new SystemClock()),
                new JobQueueSignal());

            await NightlyJobParts.Job(db, Substitute.For<ICalculationRunner>(), jobs, Now)
                .ExecuteAsync(
                    new RecalculationRequest(
                        document.ProjectId, 0, PeriodKey: null, TriggeredByUserId: null, Nightly: true, NightlySkipUnchanged: true),
                    Substitute.For<IJobProgress>(),
                    CancellationToken.None);
        }

        var lane = JobLaneMap.Of<IRecalculationJob>();
        await using (var verify = builder.CreateContext())
        {
            var children = await verify.JobProgresses.AsNoTracking()
                .Where(p => p.Lane == lane && p.State == "Queued")
                .Select(p => new { p.FanOutParentJobId, p.CreatedByUserId })
                .ToListAsync();

            Assert.Equal(6, children.Count);
            Assert.All(children, c =>
            {
                Assert.False(string.IsNullOrEmpty(c.FanOutParentJobId));
                Assert.Null(c.CreatedByUserId);
            });
        }

        // Людина просить перерахунок ПІСЛЯ розкладу.
        string manual;
        await using (var host = NewHost())
        {
            manual = (await host.Queue.EnqueueAsync(
                    new JobEnqueueRequest(
                        typeof(IRecalculationJob).FullName!, lane, "{\"documentId\":9799999,\"periodKey\":202601}",
                        $"{nameof(IRecalculationJob)}~doc9799999-p202601", 7, DocumentId: 9_799_999),
                    CancellationToken.None))
                .JobId;
        }

        await using var capped = CappedHost();

        // Останнє місце виконавця — ручному, хоча діти стоять у FIFO раніше; дитини воно не бере.
        Assert.Equal(manual, (await capped.Queue.ClaimExceptFanOutAsync(
            [lane], "host/last", JobQueueLimits.DefaultLease, CancellationToken.None))?.Claim.JobId);
        Assert.Null(await capped.Queue.ClaimExceptFanOutAsync(
            [lane], "host/last", JobQueueLimits.DefaultLease, CancellationToken.None));

        // Звичайні місця — діти, не більше стелі.
        var claimed = 0;
        while (await capped.Queue.ClaimAsync([lane], "host/a", JobQueueLimits.DefaultLease, CancellationToken.None) is not null)
        {
            claimed++;
        }

        Assert.Equal(Cap, claimed);
        await using var count = builder.CreateContext();
        Assert.Equal(Cap, await count.JobProgresses.AsNoTracking().CountAsync(p =>
            p.Lane == lane && p.State == "Running" && p.FanOutParentJobId != null));
    }

    private Host CappedHost()
    {
        var db = Sql.CreateContext();
        return new Host(db, new DbJobQueue(db, new SystemClock(), new QuartzJobTypeLimiter(2, recalcFanOutMaxConcurrency: Cap)));
    }

    private async Task AddDocumentsAsync(TestDocumentBuilder builder, TestDocument document, int count)
    {
        await using var db = builder.CreateContext();
        var added = new List<Document>();
        for (var i = 0; i < count; i++)
        {
            var extra = new Document(document.ProjectId, $"N15Q-{Guid.NewGuid():N}"[..20], 1, Now);
            added.Add(extra);
            db.Documents.Add(extra);
        }

        await db.SaveChangesAsync();

        var loader = new BulkCellLoader(Sql.ConnectionString, 1000);
        var ids = await loader.ReserveIdsAsync("doc.TableInstanceSeq", count, CancellationToken.None);
        foreach (var extra in added)
        {
            db.TableInstances.Add(new TableInstance(document.PeriodKey, ids++, extra.Id, document.TableDefId, Now));
        }

        await db.SaveChangesAsync();
    }
}
