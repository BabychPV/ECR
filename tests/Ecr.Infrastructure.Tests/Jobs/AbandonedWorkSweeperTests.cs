// tests/Ecr.Infrastructure.Tests/Jobs/AbandonedWorkSweeperTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Quartz.Impl;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Прибирання покинутої роботи (<see cref="AbandonedWorkSweeper"/>): завислі
/// задачі (U4) і журнали прогонів збору й обслуговування (U11).
/// </summary>
/// <remarks>
/// ⚠ Справжні SQL Server і Quartz (<c>RAMJobStore</c>, власна фабрика).
/// Прибирання глобальне — закриває КОЖЕН покинутий рядок спільної бази, тому
/// моменти тут у 2031 році (чуже на той час гарантовано застаріле), а тест
/// перевіряє стан СВОЇХ рядків, а не лічильники.
/// <para>
/// Мутаційні докази — у коментарях тестів; прогін і результат — в описі коміту.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class AbandonedWorkSweeperTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2031, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Задача-маркер для черги; не виконується (планувальник не запущено).</summary>
    /// <remarks>
    /// ⚠ Ім'я коротке НАВМИСНО: <c>JobCode</c> — повне ім'я типу, а стовпець
    /// <c>itg.JobProgress.JobCode</c> — <c>nvarchar(64)</c>; «…+QueuedJob» мав 67
    /// символів і падав на постановці.
    /// </remarks>
    private sealed class Held : IBackgroundJob
    {
        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct) => Task.CompletedTask;
    }

    private static StdSchedulerFactory Factory()
        => new(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-sweeper-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
        });

    private static QuartzJobScheduler Jobs(StdSchedulerFactory factory, EcrDbContext db, DateTime at)
        => new(factory, new JobProgressStore(db), new TestClock(at));

    private async Task<JobStatus?> StatusAsync(string jobId)
    {
        await using var db = sql.CreateContext();
        return await new JobProgressStore(db).FindAsync(jobId, CancellationToken.None);
    }

    /// <remarks>
    /// ⛔ U4. Задача цього інстанса довго стоїть у черзі (биття лише від
    /// постановки) — жива; задача процесу, що зник, — покинута. Мутація:
    /// <c>KeepAliveLocalJobsAsync</c> нічого не підтверджує → жива теж
    /// <c>Failed</c>, червоний (прогнано).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прибирання_валить_покинуту_задачу_і_не_чіпає_довгу_чергу_живого_інстанса()
    {
        var factory = Factory();
        var quartz = await factory.GetScheduler();
        try
        {
            await using var db = sql.CreateContext();
            var queued = await Jobs(factory, db, Now).EnqueueAsync<Held>(null, CancellationToken.None);

            // Процес, що впав: рядок лишився, черги (in-memory) немає ніде.
            var orphan = $"orphan-{Guid.NewGuid():N}";
            await new JobProgressStore(db).QueueAsync(orphan, "Ecr.Test.OrphanJob", Now, CancellationToken.None);

            // Через 10 хв живий інстанс підтверджує свою чергу; ще за 2 хв — прибирання.
            await Jobs(factory, db, Now.AddMinutes(10)).KeepAliveLocalJobsAsync(CancellationToken.None);
            await new AbandonedWorkSweeper(db, new JobProgressStore(db))
                .SweepAsync(AbandonedWorkSweeper.AbandonedJobReason, Now.AddMinutes(12), purge: false, CancellationToken.None);

            Assert.Equal("Queued", (await StatusAsync(queued))?.State);

            var abandoned = await StatusAsync(orphan);
            Assert.NotNull(abandoned);
            Assert.Equal("Failed", abandoned.State);
            Assert.Equal(AbandonedWorkSweeper.AbandonedJobReason, abandoned.Error);
        }
        finally
        {
            await quartz.Shutdown(waitForJobsToComplete: false);
        }
    }

    /// <remarks>
    /// ⛔ U11. Прогін збору й прогін обслуговування, яких ніхто не закрив
    /// (скасування, зупинка, БД недоступна в мить закриття), — <c>Failed</c> із
    /// причиною; прогін обслуговування, чия задача ЖИВА, лишається. Мутація:
    /// <c>KindOf</c> завжди <c>null</c> → <c>partition-check</c> 10 хв лишається
    /// <c>Running</c> (межа невідомих — доба), червоний (прогнано). Бік «живу не
    /// чіпає» стереже третій блок перевірок.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прибирання_закриває_завислі_прогони_збору_й_обслуговування_і_не_чіпає_живі()
    {
        var at = Now.AddDays(1);

        await using var db = sql.CreateContext();
        var entityId = await ArrangeEntityAsync(db);

        var collection = new CollectionRun(entityId, at.AddDays(-1), at, false, null, at.AddHours(-1));
        var orphanMaintenance = new MaintenanceRun(PartitionCheckJob.Code, at.AddMinutes(-10));
        var liveMaintenance = new MaintenanceRun(ConsistencyCheckJob.Code, at.AddMinutes(-10));
        db.CollectionRuns.Add(collection);
        db.MaintenanceRuns.AddRange(orphanMaintenance, liveMaintenance);
        await db.SaveChangesAsync();

        // Перевірка узгодженості ЖИВА: її задача б'є серцем щойно.
        var liveJob = $"ConsistencyCheckJob-{Guid.NewGuid():N}";
        await new JobProgressStore(db).StartAsync(
            liveJob, typeof(ConsistencyCheckJob).FullName!, at.AddMinutes(-1), CancellationToken.None);

        try
        {
            var outcome = await new AbandonedWorkSweeper(db, new JobProgressStore(db))
                .SweepAsync(AbandonedWorkSweeper.AbandonedJobReason, at, purge: false, CancellationToken.None);

            Assert.True(outcome.CollectionRuns >= 1);
            Assert.True(outcome.MaintenanceRuns >= 1);

            await using var check = sql.CreateContext();
            var run = await check.CollectionRuns.AsNoTracking().SingleAsync(r => r.Id == collection.Id);
            Assert.Equal("Failed", run.Status);
            Assert.Equal(at, run.FinishedAt);
            // U12: причина прогону збору — конверт із ключем, не українське речення.
            // Мутація «повернути AbandonedRunReason у SetProperty» → червоний.
            Assert.True(
                JobProgressMessageCodec.TryDecode(run.ErrorMessage, out var reason),
                run.ErrorMessage);
            Assert.Equal(AbandonedWorkSweeper.AbandonedCollectionRunKey, reason.Key);

            var orphan = await check.MaintenanceRuns.AsNoTracking().SingleAsync(r => r.Id == orphanMaintenance.Id);
            Assert.Equal("Failed", orphan.Status);
            Assert.NotNull(orphan.FinishedAt);
            Assert.Contains(AbandonedWorkSweeper.AbandonedRunReason, orphan.DetailsJson, StringComparison.Ordinal);

            var live = await check.MaintenanceRuns.AsNoTracking().SingleAsync(r => r.Id == liveMaintenance.Id);
            Assert.Equal("Running", live.Status);
            Assert.Null(live.FinishedAt);
        }
        finally
        {
            await new JobProgressStore(db).FinishAsync(liveJob, "Succeeded", null, at, CancellationToken.None);
            await db.MaintenanceRuns
                .Where(r => r.Id == orphanMaintenance.Id || r.Id == liveMaintenance.Id)
                .ExecuteDeleteAsync();
            await db.CollectionRuns.Where(r => r.SourceEntityId == entityId).ExecuteDeleteAsync();
        }
    }

    /// <remarks>
    /// Прогін збору, поки інша задача збору жива, закривається лише після
    /// подвоєної стелі прогону — живий прогін свій watchdog закриває сам.
    /// Мутація: межа не залежить від живого збору (завжди <c>StaleAfter</c>) →
    /// 10-хвилинний прогін закрито, червоний (прогнано).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прогін_збору_при_живому_зборі_закривається_лише_після_подвоєної_стелі()
    {
        var at = Now.AddDays(2);

        await using var db = sql.CreateContext();
        var entityId = await ArrangeEntityAsync(db);

        var recent = new CollectionRun(entityId, at.AddDays(-1), at, false, null, at.AddMinutes(-10));
        var ancient = new CollectionRun(entityId, at.AddDays(-1), at, false, null, at.AddMinutes(-45));
        db.CollectionRuns.AddRange(recent, ancient);
        await db.SaveChangesAsync();

        var liveJob = $"ICollectionJob-{Guid.NewGuid():N}";
        await new JobProgressStore(db).StartAsync(
            liveJob, typeof(ICollectionJob).FullName!, at.AddMinutes(-1), CancellationToken.None);

        try
        {
            await new AbandonedWorkSweeper(db, new JobProgressStore(db))
                .SweepAsync(AbandonedWorkSweeper.AbandonedJobReason, at, purge: false, CancellationToken.None);

            await using var check = sql.CreateContext();
            Assert.Equal("Running", (await check.CollectionRuns.AsNoTracking().SingleAsync(r => r.Id == recent.Id)).Status);
            Assert.Equal("Failed", (await check.CollectionRuns.AsNoTracking().SingleAsync(r => r.Id == ancient.Id)).Status);
        }
        finally
        {
            await new JobProgressStore(db).FinishAsync(liveJob, "Succeeded", null, at, CancellationToken.None);
            await db.CollectionRuns.Where(r => r.SourceEntityId == entityId).ExecuteDeleteAsync();
        }
    }

    private static async Task<int> ArrangeEntityAsync(EcrDbContext db)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Sweeper" }),
            ExternalTransport.PiWebApi,
            "https://example.test",
            "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        var entity = new SourceEntity(dataSource.Id, $"Ent{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        return entity.Id;
    }
}
