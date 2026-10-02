// tests/Ecr.Infrastructure.Tests/Jobs/CollectionScheduleDependencyGateTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
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
/// Залежність розкладу збору від іншого розкладу того ж з'єднання (<c>ФВ-13.15</c>):
/// плановий запуск чекає успішного прогону залежного, ручний і з вікном — ні.
/// </summary>
/// <remarks>
/// ⚠ На справжньому SQL Server: гілка читає <c>ext.CollectionSchedule</c> запитом, а
/// зовнішній ключ-самопосилання без каскаду існує лише в базі.
/// </remarks>
[Collection("SqlServer")]
public sealed class CollectionScheduleDependencyGateTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-13.15")]
    public async Task Плановий_запуск_пропускається_доки_залежний_розклад_не_відбіг_після_нашого_прогону()
    {
        await using var db = Context();
        var (dependent, waiting) = await ArrangeAsync(db, dependencyRan: Now.AddHours(-5), ownRan: Now.AddHours(-3));
        var runner = Substitute.For<ICollectionRunner>();
        var progress = Substitute.For<IJobProgress>();

        await Job(db, runner).ExecuteAsync(new CollectionJobRequest(waiting, null, null), progress, CancellationToken.None);

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати гілку `IsDependencyMet` у `CollectionJob` — збір іде, і
        // `DidNotReceive` червоніє; а зробити `IsDependencyMet` завжди `false` — червоніють решта тестів.
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default, default, default, default!, default);
        await progress.Received().ReportAsync(
            100, Arg.Is<string?>(m => m!.Contains("jobs.collectionDependencyWaiting", StringComparison.Ordinal)), Arg.Any<CancellationToken>());

        // Пропуск видно в журналі покриття: подія SkippedDependency із конвертом; повторний
        // пропуск тієї ж години не дублює її. ⛔ МУТАЦІЯ: прибрати RecordCoverageEventAsync — червоніє.
        await Job(db, runner).ExecuteAsync(new CollectionJobRequest(waiting, null, null), progress, CancellationToken.None);
        var events = await db.CollectionCoverages.AsNoTracking()
            .Where(c => c.SourceEntityId == waiting && c.Status == CollectionCoverage.SkippedDependency).ToListAsync();
        var skipped = Assert.Single(events);
        Assert.Contains("coverageEvents.skippedDependency", skipped.Details, StringComparison.Ordinal);

        // Той, від кого залежимо, збирається без обмежень.
        await Job(db, runner).ExecuteAsync(new CollectionJobRequest(dependent, null, null), progress, CancellationToken.None);
        await runner.Received(1).RunAsync(dependent, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<IJobProgress>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-13.15")]
    public async Task Ручний_збір_і_збір_із_вікном_залежність_не_блокує()
    {
        await using var db = Context();
        var (_, waiting) = await ArrangeAsync(db, dependencyRan: Now.AddHours(-5), ownRan: Now.AddHours(-3));
        var runner = Substitute.For<ICollectionRunner>();

        await Job(db, runner).ExecuteAsync(
            new CollectionJobRequest(waiting, null, null, Manual: true), Substitute.For<IJobProgress>(), CancellationToken.None);
        await Job(db, runner).ExecuteAsync(
            new CollectionJobRequest(waiting, Now.AddDays(-2), Now.AddDays(-1)), Substitute.For<IJobProgress>(), CancellationToken.None);

        await runner.Received(2).RunAsync(waiting, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<IJobProgress>(), Arg.Any<CancellationToken>());
        Assert.False(await db.CollectionCoverages.AnyAsync(
            c => c.SourceEntityId == waiting && c.Status == CollectionCoverage.SkippedDependency));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-13.15")]
    public async Task Залежний_відбіг_після_нашого_прогону_або_застарів_понад_дві_доби_збір_іде()
    {
        await using var db = Context();
        var (_, fresh) = await ArrangeAsync(db, dependencyRan: Now.AddHours(-2), ownRan: Now.AddHours(-3));
        var (_, stale) = await ArrangeAsync(db, dependencyRan: Now.AddHours(-60), ownRan: Now.AddHours(-30));
        var runner = Substitute.For<ICollectionRunner>();

        await Job(db, runner).ExecuteAsync(new CollectionJobRequest(fresh, null, null), Substitute.For<IJobProgress>(), CancellationToken.None);
        await Job(db, runner).ExecuteAsync(new CollectionJobRequest(stale, null, null), Substitute.For<IJobProgress>(), CancellationToken.None);

        await runner.Received(1).RunAsync(fresh, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<IJobProgress>(), Arg.Any<CancellationToken>());
        await runner.Received(1).RunAsync(stale, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<IJobProgress>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-13.15")]
    public async Task Ключ_самопосилання_без_каскаду_видалення_без_зняття_залежності_відхиляється_базою()
    {
        await using var db = Context();
        var (dependencyEntity, dependentEntity) = await ArrangeAsync(db, dependencyRan: null, ownRan: null);

        var store = new CollectionScheduleStore(db);

        // ⚠ Контекст очищено: відстежуваний залежний EF сам знімає ключ при видаленні
        // принципала, і ключ у БД не перевірився б. Обробник видалення, навпаки, не покладається на це.
        db.ChangeTracker.Clear();
        var dependency = await db.CollectionSchedules.SingleAsync(s => s.SourceEntityId == dependencyEntity);

        // ⛔ Без `FindDependentsAsync` + зняття видалення впало б на `FK_CS_DependsOn`.
        db.CollectionSchedules.Remove(dependency);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(CancellationToken.None));
        db.ChangeTracker.Clear();

        var again = await db.CollectionSchedules.SingleAsync(s => s.SourceEntityId == dependencyEntity);
        var dependents = await store.FindDependentsAsync(again.Id, CancellationToken.None);

        Assert.Equal(dependentEntity, Assert.Single(dependents).SourceEntityId);

        foreach (var dependent in dependents)
        {
            dependent.SetDependency(null);
        }

        store.Remove(again);
        await db.SaveChangesAsync(CancellationToken.None);

        Assert.False(await db.CollectionSchedules.AnyAsync(s => s.SourceEntityId == dependencyEntity));
        Assert.Null((await db.CollectionSchedules.SingleAsync(s => s.SourceEntityId == dependentEntity)).DependsOnScheduleId);
    }

    private static CollectionJob Job(EcrDbContext db, ICollectionRunner runner)
        => new(
            runner,
            db,
            Substitute.For<IBackgroundJobScheduler>(),
            new TestClock(Now),
            Substitute.For<INotificationOutbox>(),
            new OutboxDispatcher(db, new TestClock(Now), Substitute.For<INotificationSender>()),
            Substitute.For<IRegistrySyncJob>());

    /// <summary>Два розклади одного з'єднання: перший (залежність) і другий (залежить від першого).</summary>
    /// <returns>Сутності: залежності та залежного.</returns>
    private async Task<(int DependencyEntity, int DependentEntity)> ArrangeAsync(
        EcrDbContext db, DateTime? dependencyRan, DateTime? ownRan)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "FV-13.15" }),
            ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        // ⚠ Сутності НЕАКТИВНІ навмисно (як у `CollectionScheduleWindowTests`): активна без
        // завершеного збору робить `SourcesHealthCheck` жовтим для наступних прогонів.
        var first = new SourceEntity(dataSource.Id, $"Dep{tag}", RegistrySourceKind.External);
        var second = new SourceEntity(dataSource.Id, $"Own{tag}", RegistrySourceKind.External);
        first.Deactivate();
        second.Deactivate();
        db.SourceEntities.AddRange(first, second);
        await db.SaveChangesAsync(CancellationToken.None);

        var dependency = new CollectionSchedule(first.Id, "0 5 * * * ?");
        db.CollectionSchedules.Add(dependency);
        await db.SaveChangesAsync(CancellationToken.None);

        var own = new CollectionSchedule(second.Id, "0 15 2 * * ?");
        own.SetDependency(dependency.Id);
        db.CollectionSchedules.Add(own);
        await db.SaveChangesAsync(CancellationToken.None);

        if (dependencyRan is { } theirs)
        {
            dependency.MarkRun(theirs, watermark: null);
        }

        if (ownRan is { } mine)
        {
            own.MarkRun(mine, watermark: null);
        }

        await db.SaveChangesAsync(CancellationToken.None);

        return (first.Id, second.Id);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
