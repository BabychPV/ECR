// tests/Ecr.Infrastructure.Tests/Persistence/CollectionStoreLastRunTests.cs
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
/// AN-56: останній прогін кожної сутності в переліку джерел (екран і <c>/health/ready</c>) - точний, без стелі
/// «N найсвіжіших прогонів усіх сутностей».
/// </summary>
/// <remarks>⚠ Сутності тестів НЕАКТИВНІ (збирач їх не бере); прогони прибираються у <c>finally</c>.</remarks>
[Collection("SqlServer")]
public sealed class CollectionStoreLastRunTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "AN-56")]
    public async Task Давній_прогін_сутності_не_губиться_за_двадцятьма_тисячами_свіжіших_чужих()
    {
        // ⛔ Сутність A мала один прогін рік тому; сусідня B - 20 001 свіжіший. Колишня форма брала 20 000
        // найсвіжіших прогонів УСІХ сутностей і згортала їх у пам'яті: A лишалась без прогону («не збиралось»).
        // МУТАЦІЙНИЙ ДОКАЗ (прогнано): повернути `CollectionRuns.Where(ids.Contains).OrderByDescending(StartedAt)
        // .Take(20_000)` + GroupBy - LastRun сутності A null, тест червоний.
        const int count = 20_001;

        await using var db = sql.CreateContext();
        var a = await ArrangeEntityAsync(db);
        var b = await ArrangeEntityAsync(db);

        try
        {
            var store = new CollectionStore(db, new TestClock(Now.AddDays(-365)));
            var oldRun = await store.StartRunAsync(a, Now.AddDays(-366), Now.AddDays(-365), false, null, CancellationToken.None);
            await store.FinishRunAsync(oldRun, "Succeeded", 7, null, CancellationToken.None);

            await db.Database.ExecuteSqlAsync($"""
                DECLARE @i int = 0;
                WHILE @i < {count}
                BEGIN
                    INSERT INTO itg.CollectionRun (SourceEntityId, RangeFrom, RangeTo, StartedAt, FinishedAt, Status, PointsRetrieved, IsCatchUp)
                    SELECT {b},
                           DATEADD(minute, -(@i + n.i), {Now}),
                           DATEADD(minute, -(@i + n.i), {Now}),
                           DATEADD(minute, -(@i + n.i), {Now}),
                           DATEADD(minute, -(@i + n.i), {Now}),
                           N'Succeeded', 1, 0
                    FROM (SELECT TOP ({Chunk}) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS int) AS i
                          FROM sys.all_columns x CROSS JOIN sys.all_columns y) n
                    WHERE @i + n.i < {count};
                    SET @i += {Chunk};
                    CHECKPOINT;
                END;
                """);

            var statuses = await store.ListSourceEntitiesAsync(CancellationToken.None);

            var statusA = Assert.Single(statuses, s => s.Id == a);
            Assert.NotNull(statusA.LastRun);
            Assert.Equal("Succeeded", statusA.LastRun!.Status);
            Assert.Equal(7, statusA.LastRun.PointsRetrieved);

            Assert.NotNull(Assert.Single(statuses, s => s.Id == b).LastRun);
        }
        finally
        {
            await CleanUpAsync(a, b);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "AN-56")]
    public async Task Останній_прогін_найновіший_а_сутність_без_прогонів_без_нього()
    {
        await using var db = sql.CreateContext();
        var withRuns = await ArrangeEntityAsync(db);
        var none = await ArrangeEntityAsync(db);

        try
        {
            var store = new CollectionStore(db, new TestClock(Now.AddHours(-2)));
            var first = await store.StartRunAsync(withRuns, Now.AddHours(-3), Now.AddHours(-2), false, null, CancellationToken.None);
            await store.FinishRunAsync(first, "Succeeded", 10, null, CancellationToken.None);

            store = new CollectionStore(db, new TestClock(Now));
            var second = await store.StartRunAsync(withRuns, Now.AddHours(-1), Now, false, null, CancellationToken.None);
            await store.FinishRunAsync(second, "Failed", 0, "boom", CancellationToken.None);

            var statuses = await store.ListSourceEntitiesAsync(CancellationToken.None);

            var last = Assert.Single(statuses, s => s.Id == withRuns).LastRun;
            Assert.NotNull(last);
            Assert.Equal("Failed", last!.Status);
            Assert.Equal(Now, last.FinishedAt);

            Assert.Null(Assert.Single(statuses, s => s.Id == none).LastRun);
        }
        finally
        {
            await CleanUpAsync(withRuns, none);
        }
    }

    private static async Task<int> ArrangeEntityAsync(EcrDbContext db)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "AN-56" }),
            ExternalTransport.PiWebApi,
            "https://example.test",
            "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        // ⚠ Код із «0» на початку: перелік упорядкований за кодом зі стелею, і сутність мусить потрапити в нього в
        // спільній базі.
        var entity = new SourceEntity(dataSource.Id, $"0AN56{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        return entity.Id;
    }

    private async Task CleanUpAsync(int first, int second)
    {
        await using var db = sql.CreateContext();
        await db.Database.ExecuteSqlAsync($"""
            WHILE 1 = 1
            BEGIN
                DELETE TOP ({Chunk}) FROM itg.CollectionRun WHERE SourceEntityId IN ({first}, {second});
                IF @@ROWCOUNT = 0 BREAK;
                CHECKPOINT;
            END;
            """);
    }

    /// <summary>Порція вставки й прибирання: тримає журнал тестової бази малим.</summary>
    private const int Chunk = 10_000;
}
