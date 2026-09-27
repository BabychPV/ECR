// tests/Ecr.Infrastructure.Tests/Jobs/CollectionScheduleWindowTests.cs
using Ecr.Application.Ports;
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
/// Вікно збору береться з розкладу СВОЄЇ сутності, а не одне на систему
/// (<c>ФВ-13.15</c>).
/// </summary>
/// <remarks>
/// ⛔ Дві сутності з різними <c>LookbackDays</c> збираються одним і тим самим
/// <see cref="CollectionJob"/>. Якби вікно було спільним (константа задачі або
/// перший-ліпший розклад), обидві отримали б однаковий початок — і тест
/// червонів би саме на розбіжності початків.
///
/// ⚠ На справжньому SQL Server: розклад читається запитом до
/// <c>ext.CollectionSchedule</c>, і перевірити це в пам'яті означало б
/// перевірити копію запиту.
/// </remarks>
[Collection("SqlServer")]
public sealed class CollectionScheduleWindowTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-13.15")]
    public async Task Кожна_сутність_збирається_у_вікні_власного_розкладу()
    {
        await using var db = Context();

        var shortWindow = await ArrangeAsync(db, lookbackDays: 3);
        var longWindow = await ArrangeAsync(db, lookbackDays: 30);

        var runner = Substitute.For<ICollectionRunner>();
        var job = new CollectionJob(
            runner,
            db,
            Substitute.For<IBackgroundJobScheduler>(),
            new TestClock(Now),
            Substitute.For<INotificationOutbox>(),
            new OutboxDispatcher(db, new TestClock(Now), Substitute.For<INotificationSender>()));

        // Payload планового збору — без меж: початок має взятися з розкладу.
        await job.ExecuteAsync(
            new CollectionJobRequest(shortWindow, null, null), Substitute.For<IJobProgress>(), CancellationToken.None);
        await job.ExecuteAsync(
            new CollectionJobRequest(longWindow, null, null), Substitute.For<IJobProgress>(), CancellationToken.None);

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: зробити `CollectionSchedule.SetLookback`
        // порожнім (вікно лишається дефолтним 7 днів для обох) — червоніють
        // обидва твердження: початки однакові, хоча розклади різні.
        await runner.Received(1).RunAsync(
            shortWindow, Now.AddDays(-3), Now, Arg.Any<IJobProgress>(), Arg.Any<CancellationToken>());
        await runner.Received(1).RunAsync(
            longWindow, Now.AddDays(-30), Now, Arg.Any<IJobProgress>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Сутність джерела з увімкненим розкладом і заданим вікном.</summary>
    /// <remarks>
    /// ⚠ Сутність НЕАКТИВНА навмисно (див. <c>PausedMappingCollectionPathTests</c>):
    /// активна сутність без завершеного збору лишається в спільній базі й робить
    /// <c>SourcesHealthCheck</c> жовтим для кожного наступного прогону. Задачі
    /// збору активність не потрібна — збирач тут замінник.
    /// </remarks>
    private static async Task<int> ArrangeAsync(EcrDbContext db, int lookbackDays)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"), Name("FV-13.15"), ExternalTransport.PiWebApi,
            "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        var entity = new SourceEntity(dataSource.Id, $"Ent{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        var schedule = new CollectionSchedule(entity.Id, "0 5 * * * ?");
        schedule.SetLookback(lookbackDays);
        db.CollectionSchedules.Add(schedule);
        await db.SaveChangesAsync(CancellationToken.None);

        return entity.Id;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
