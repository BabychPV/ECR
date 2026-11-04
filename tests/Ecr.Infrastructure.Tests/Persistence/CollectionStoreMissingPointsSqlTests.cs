// tests/Ecr.Infrastructure.Tests/Persistence/CollectionStoreMissingPointsSqlTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// D-462 на справжній базі: <c>UpsertRawPointsAsync</c> не переписує незмінних точок (значення, одиниця, якість),
/// а <c>DeleteMissingRawPointsAsync</c> видаляє рівно ті точки атрибута в інтервалі, яких немає серед міток джерела.
/// </summary>
[Collection("SqlServer")]
public sealed class CollectionStoreMissingPointsSqlTests(SqlServerFixture sql)
{
    private const string Flow = "E1|Flow";
    private const string Temp = "E1|Temp";

    private static readonly DateTime Now = new(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <remarks>
    /// ⛔ МУТАЦІЯ: прибрати умову <c>AND EXISTS (… EXCEPT …)</c> у <c>WHEN MATCHED</c> — незмінна точка
    /// отримує прогін повторного читання (<c>CollectionRunId</c>), тест червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-462")]
    public async Task Повторне_читання_незмінного_не_переписує_точку_а_змінене_переписує()
    {
        await using var db = sql.CreateContext();
        var entityId = await ArrangeEntityAsync(db);

        try
        {
            var first = await RunAsync(db, entityId, Now.AddHours(-2));
            var accepted = await new CollectionStore(db, new TestClock(Now.AddHours(-2))).UpsertRawPointsAsync(
                first, entityId, [Point(Flow, Day.AddHours(1), 5m), Point(Flow, Day.AddHours(2), 7m)], CancellationToken.None);
            Assert.Equal(2, accepted);

            var second = await RunAsync(db, entityId, Now);
            accepted = await new CollectionStore(db, new TestClock(Now)).UpsertRawPointsAsync(
                second,
                entityId,
                [
                    Point(Flow, Day.AddHours(1), 5m),                      // те саме
                    Point(Flow, Day.AddHours(2), 7m, quality: "Bad"),      // інша якість
                    Point(Flow, Day.AddHours(3), 9m),                      // нова
                ],
                CancellationToken.None);

            // Прийнято всі три (число — «скільки точок віддало джерело», як і до D-462).
            Assert.Equal(3, accepted);

            var stored = await ReadAsync(entityId);
            Assert.Equal(
                [(Day.AddHours(1), 5m, "Good", first), (Day.AddHours(2), 7m, "Bad", second), (Day.AddHours(3), 9m, "Good", second)],
                stored.Select(p => (p.Timestamp, p.ValueNumeric!.Value, p.Quality!, p.CollectionRunId)));
        }
        finally
        {
            await CleanUpAsync(entityId);
        }
    }

    /// <remarks>
    /// ⛔ МУТАЦІЇ: прибрати <c>NOT EXISTS</c> — видаляється все в інтервалі; прибрати межу <c>&lt; @to</c> чи
    /// фільтр шляху — зачіпається точка наступної доби чи сусідній атрибут.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-462")]
    public async Task Видаляються_лише_точки_атрибута_в_інтервалі_яких_немає_серед_міток_джерела()
    {
        await using var db = sql.CreateContext();
        var entityId = await ArrangeEntityAsync(db);

        try
        {
            var store = new CollectionStore(db, new TestClock(Now));
            var run = await RunAsync(db, entityId, Now);
            await store.UpsertRawPointsAsync(
                run,
                entityId,
                [
                    Point(Flow, Day.AddHours(1), 1m),
                    Point(Flow, Day.AddHours(2).AddTicks(1234), 2m),   // мітка з тіками: порівняння — за мс сховища
                    Point(Flow, Day.AddHours(3), 3m),                   // зникла в джерелі
                    Point(Flow, Day.AddDays(1), 4m),                    // наступна доба — поза інтервалом
                    Point(Temp, Day.AddHours(3), 30m),                  // інший атрибут
                ],
                CancellationToken.None);

            var deleted = await store.DeleteMissingRawPointsAsync(
                entityId, Flow, Day, Day.AddDays(1),
                [Day.AddHours(1), Day.AddHours(2).AddTicks(1999)],
                CancellationToken.None);

            Assert.Equal(1, deleted);
            Assert.Equal(
                [(Flow, Day.AddHours(1)), (Flow, Day.AddHours(2)), (Temp, Day.AddHours(3)), (Flow, Day.AddDays(1))],
                (await ReadAsync(entityId)).Select(p => (p.SourcePath, p.Timestamp)));

            // ✎ D-462: порожній перелік — джерело успішно віддало нуль точок: видаляється все атрибута в інтервалі
            // (і лише його: Temp лишається).
            Assert.Equal(3, await store.DeleteMissingRawPointsAsync(entityId, Flow, Day, Day.AddDays(2), [], CancellationToken.None));
            Assert.Equal([(Temp, Day.AddHours(3))], (await ReadAsync(entityId)).Select(p => (p.SourcePath, p.Timestamp)));
        }
        finally
        {
            await CleanUpAsync(entityId);
        }
    }

    private static SourceDataPoint Point(string path, DateTime timestamp, decimal value, string quality = "Good")
        => new(path, timestamp, value, null, null, quality);

    private static Task<long> RunAsync(EcrDbContext db, int entityId, DateTime at)
        => new CollectionStore(db, new TestClock(at)).StartRunAsync(entityId, Day, Day.AddDays(2), false, null, CancellationToken.None);

    private async Task<List<RawDataPoint>> ReadAsync(int entityId)
    {
        await using var check = sql.CreateContext();

        return await check.RawDataPoints.AsNoTracking()
            .Where(p => p.SourceEntityId == entityId)
            .OrderBy(p => p.Timestamp).ThenBy(p => p.SourcePath)
            .ToListAsync();
    }

    private static async Task<int> ArrangeEntityAsync(EcrDbContext db)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "D-462" }),
            ExternalTransport.PiWebApi,
            "https://example.test",
            "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        // ⚠ Неактивна: сховище активності не питає, а активна сутність без завершеного збору фарбує здоров'я.
        var entity = new SourceEntity(dataSource.Id, $"Ent{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        return entity.Id;
    }

    private async Task CleanUpAsync(int entityId)
    {
        await using var db = sql.CreateContext();
        await db.RawDataPoints.Where(p => p.SourceEntityId == entityId).ExecuteDeleteAsync();
    }
}
