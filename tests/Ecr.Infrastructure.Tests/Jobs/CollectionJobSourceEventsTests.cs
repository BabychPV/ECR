// tests/Ecr.Infrastructure.Tests/Jobs/CollectionJobSourceEventsTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Розклад і кнопка збору для сутності-шаблону ПОДІЙ ведуть у синк подій, а не в збір точок
/// (HSE301 A5b, FEATURE-HSE301-VIEW §4.7.4).
/// </summary>
/// <remarks>
/// ⚠ Сутність із активним <c>SourceEventMap</c> і без мапінгів атрибутів точок не має точок: збирач
/// шукав би за її кодом-шаблоном сирий тег і завів би прогін збору-привида. Тому задача ставить синк
/// подій — <c>EnqueueCoalescedAsync</c> на ціль «сутність» (сплеск постановок зливається, виконувана
/// не переривається) — і фіксує прогін розкладу без watermark.
///
/// Мутаційні докази (A5b): прибрати гілку <c>hasEventMap</c> у <c>CollectionJob.ExecuteAsync</c> —
/// червоніє <see cref="Сутність_подій_ставить_синк_а_не_збирає_точки"/>; прибрати умову
/// <c>hasEventMap</c> — червоніє <see cref="Сутність_без_мапінгу_подій_збирає_точки_і_синку_не_ставить"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class CollectionJobSourceEventsTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A5b")]
    public async Task Сутність_подій_ставить_синк_а_не_збирає_точки()
    {
        await using var db = sql.CreateContext();
        var entityId = await ArrangeAsync(db, withEventMap: true);
        var runner = Substitute.For<ICollectionRunner>();
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        var job = NewJob(runner, db, jobs);
        var from = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        await job.ExecuteAsync(new CollectionJobRequest(entityId, from, null), Substitute.For<IJobProgress>(), CancellationToken.None);

        await runner.DidNotReceiveWithAnyArgs().RunAsync(default, default, default, default!, default);
        await jobs.Received(1).EnqueueCoalescedAsync<ISourceEventSyncJob>(
            SourceEventSyncTarget.Of(entityId),
            new SourceEventSyncRequest(entityId, from, null),
            Arg.Any<CancellationToken>(),
            Arg.Any<int?>());

        // Прогін розкладу зафіксовано, watermark не рухається.
        await using var read = sql.CreateContext();
        var schedule = await read.CollectionSchedules.AsNoTracking().SingleAsync(s => s.SourceEntityId == entityId);
        Assert.Equal(Now, schedule.LastRunAt);
        Assert.Null(schedule.Watermark);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A5b")]
    public async Task Сутність_без_мапінгу_подій_збирає_точки_і_синку_не_ставить()
    {
        await using var db = sql.CreateContext();

        // Чужа сутність з активним мапінгом подій: мапінг шукається за СВОЄЮ сутністю, а не «десь є».
        await ArrangeAsync(db, withEventMap: true);
        var entityId = await ArrangeAsync(db, withEventMap: false);
        var runner = Substitute.For<ICollectionRunner>();
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        var job = NewJob(runner, db, jobs);

        await job.ExecuteAsync(new CollectionJobRequest(entityId, null, null), Substitute.For<IJobProgress>(), CancellationToken.None);

        await runner.Received(1).RunAsync(entityId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<IJobProgress>(), Arg.Any<CancellationToken>());
        await jobs.DidNotReceive().EnqueueCoalescedAsync<ISourceEventSyncJob>(
            Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>());
    }

    private static CollectionJob NewJob(ICollectionRunner runner, EcrDbContext db, IBackgroundJobScheduler jobs)
        => new(
            runner,
            db,
            jobs,
            new TestClock(Now),
            Substitute.For<INotificationOutbox>(),
            new OutboxDispatcher(db, new TestClock(Now), Substitute.For<INotificationSender>()),
            Substitute.For<IRegistrySyncJob>());

    /// <summary>
    /// Сутність (НЕАКТИВНА навмисно — див. <c>CollectionScheduleWindowTests</c>) із розкладом і,
    /// за <paramref name="withEventMap"/>, активним мапінгом подій у динамічну таблицю.
    /// </summary>
    private async Task<int> ArrangeAsync(EcrDbContext db, bool withEventMap)
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var dataSource = new DataSource(
            EcrCode.Create($"CJSRC{tag}"), Name("HSE301-A5b"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        var entity = new SourceEntity(dataSource.Id, $"FlareEvent{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        db.CollectionSchedules.Add(new CollectionSchedule(entity.Id, "0 5 * * * ?"));
        await db.SaveChangesAsync(CancellationToken.None);

        if (withEventMap)
        {
            var doc = await new TestDocumentBuilder(sql.ConnectionString)
                .BuildAsync(periodKey: 202601, rowCount: 0, rowMode: TableRowMode.Dynamic);

            var start = new ColumnDef(doc.TableDefId, EcrCode.Create($"START_{tag}"), Name("Start"), 10, CellDataType.Date);
            var end = new ColumnDef(doc.TableDefId, EcrCode.Create($"END_{tag}"), Name("End"), 11, CellDataType.Date);
            db.ColumnDefs.AddRange(start, end);
            await db.SaveChangesAsync(CancellationToken.None);

            var table = await db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == doc.TableDefId);
            db.SourceEventMaps.Add(SourceEventMap.Create(
                entity.Id,
                doc.DocumentId,
                table,
                [new(start, SourceEventMap.StartAttribute), new(end, SourceEventMap.EndAttribute)],
                SourceEventVolumeMode.None));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return entity.Id;
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
