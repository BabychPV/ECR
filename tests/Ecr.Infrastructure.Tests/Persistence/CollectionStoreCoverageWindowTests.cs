// tests/Ecr.Infrastructure.Tests/Persistence/CollectionStoreCoverageWindowTests.cs
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Покриття для екрана джерел і наздоганяння — лише вікно прогалин, лише
/// інтервали прогонів, без стелі «найстаріше першим» (аудит P5).
/// </summary>
/// <remarks>
/// ⚠ Сутність кожного тесту — власна і НЕАКТИВНА (збирач її не бере), а рядки
/// покриття прибираються у <c>finally</c>: сто двадцять тисяч рядків на прогін
/// спільної бази інакше накопичувалися б.
/// </remarks>
[Collection("SqlServer")]
public sealed class CollectionStoreCoverageWindowTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Обрій екрана й наздоганяння — <c>CollectionStore.GapLookbackDays</c> = <c>CollectionRunner.CatchUpLookback</c>.</summary>
    private static readonly DateTime WindowFrom = Now.AddDays(-45);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P5")]
    public async Task Журнал_понад_колишню_стелю_зі_свіжими_даними_не_дає_прогалини_ні_екрану_ні_наздоганянню()
    {
        // ⛔ Один довгий інтервал до початку вікна і 101 000 суміжних по 38 с,
        // що закінчуються рівно «зараз» (≈ 44.4 доби) — усе вікно покрите, і
        // УСІ рядки лежать у вікні. Колишня стеля — 100 000 рядків за
        // зростанням CoveredFrom — відрізала останні ~1 000 (≈ 10.6 год), і
        // екран показував прогалину в найсвіжішому.
        // МУТАЦІЙНИЙ ДОКАЗ (прогнано): повернути в `ReadCoverageIslandsAsync`
        // стару форму `CollectionCoverages.Where(...).OrderBy(CoveredFrom)
        // .Take(100_000)` — OldestGap не null, тест червоний.
        // ⚠ Рядків рівно стільки, скільки треба, щоб перетнути стелю, і
        // CHECKPOINT після вставки й після прибирання: журнал тестової бази
        // інакше розростається на ~100 МБ і валить `TestDatabaseSizeTests`.
        const int count = 101_000;
        const int stepSeconds = 38;
        var start = Now.AddSeconds(-(double)count * stepSeconds);

        await using var db = sql.CreateContext();
        var entityId = await ArrangeEntityAsync(db);

        try
        {
            var store = new CollectionStore(db, new TestClock(Now));
            var runId = await store.StartRunAsync(entityId, start, Now, false, null, CancellationToken.None);

            // Set-based INSERT порціями, а не 101 000 Add; CHECKPOINT між
            // порціями звільняє журнал (див. ⚠ вище).
            await db.Database.ExecuteSqlAsync($"""
                DECLARE @i int = 0;
                WHILE @i < {count}
                BEGIN
                    INSERT INTO itg.CollectionCoverage (SourceEntityId, CoveredFrom, CoveredTo, CollectionRunId)
                    SELECT {entityId},
                           DATEADD(second, (@i + n.i) * {stepSeconds}, {start}),
                           DATEADD(second, (@i + n.i + 1) * {stepSeconds}, {start}),
                           {runId}
                    FROM (SELECT TOP ({Chunk}) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS int) AS i
                          FROM sys.all_columns a CROSS JOIN sys.all_columns b) n
                    WHERE @i + n.i < {count};
                    SET @i += {Chunk};
                    CHECKPOINT;
                END;
                INSERT INTO itg.CollectionCoverage (SourceEntityId, CoveredFrom, CoveredTo, CollectionRunId)
                VALUES ({entityId}, {Now.AddDays(-50)}, {start}, {runId});
                """);

            var status = await FindStatusAsync(store, entityId);
            Assert.Null(status.OldestGap);

            var coverage = await store.GetCoverageAsync(entityId, WindowFrom, CancellationToken.None);
            Assert.Empty(GapFinder.Find(coverage, WindowFrom, Now));

            // Острів один: сто двадцять тисяч рядків злито базою, у пам'ять
            // приїхав один інтервал.
            var island = Assert.Single(coverage);
            Assert.Equal(Now, island.ToUtc);
        }
        finally
        {
            await CleanUpAsync(entityId);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P5")]
    public async Task Справжня_прогалина_у_вікні_показана_а_покриття_злите_і_без_давньої_історії()
    {
        // Вікно — 45 діб. Давній інтервал за рік до «зараз» у вибірку не йде
        // зовсім; інтервал, що перетинає початок вікна, — іде; суміжні й
        // перекриті зливаються; прогалина [Now−40; Now−30) — справжня.
        await using var db = sql.CreateContext();
        var entityId = await ArrangeEntityAsync(db);

        try
        {
            var store = new CollectionStore(db, new TestClock(Now));
            var runId = await store.StartRunAsync(entityId, Now.AddDays(-400), Now, false, null, CancellationToken.None);

            await store.WriteCoverageAsync(
                runId,
                entityId,
                [
                    new TimeInterval(Now.AddDays(-400), Now.AddDays(-399)),
                    new TimeInterval(Now.AddDays(-60), Now.AddDays(-40)),
                    new TimeInterval(Now.AddDays(-30), Now.AddDays(-10)),
                    new TimeInterval(Now.AddDays(-10), Now.AddDays(-2)),
                    new TimeInterval(Now.AddDays(-5), Now),
                ],
                CancellationToken.None);

            var status = await FindStatusAsync(store, entityId);
            Assert.Equal(Now.AddDays(-40), status.OldestGap);

            var coverage = await store.GetCoverageAsync(entityId, WindowFrom, CancellationToken.None);
            Assert.Equal(
                new[]
                {
                    new TimeInterval(Now.AddDays(-60), Now.AddDays(-40)),
                    new TimeInterval(Now.AddDays(-30), Now),
                },
                coverage);

            Assert.Equal(
                new[] { (Now.AddDays(-40), Now.AddDays(-30)) },
                GapFinder.Find(coverage, WindowFrom, Now));
        }
        finally
        {
            await CleanUpAsync(entityId);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L3-01")]
    public async Task GetCoverageAsync_МежіМаютьKindUtc()
    {
        // ⛔ `SqlQuery` в ad-hoc тип оминає конвертер `UtcDateTimeColumns`, і
        // межі приходили з `Kind = Unspecified`; PI Web API (`Iso`) трактував
        // їх як місцевий час і читав зі зсувом на пояс сервера (аудит
        // 2026-10-03, L3-01). Перевірка — сам `Kind`, тож червоне незалежно від
        // поясу машини.
        // МУТАЦІЙНИЙ ДОКАЗ (прогнано): прибрати `SpecifyKind` у
        // `ReadCoverageIslandsAsync` — Kind == Unspecified, тест червоний.
        await using var db = sql.CreateContext();
        var entityId = await ArrangeEntityAsync(db);

        try
        {
            var store = new CollectionStore(db, new TestClock(Now));
            var runId = await store.StartRunAsync(entityId, Now.AddDays(-10), Now, false, null, CancellationToken.None);

            await store.WriteCoverageAsync(
                runId, entityId, [new TimeInterval(Now.AddDays(-10), Now)], CancellationToken.None);

            var coverage = await store.GetCoverageAsync(entityId, WindowFrom, CancellationToken.None);

            var island = Assert.Single(coverage);
            Assert.Equal(DateTimeKind.Utc, island.FromUtc.Kind);
            Assert.Equal(DateTimeKind.Utc, island.ToUtc.Kind);
            Assert.Equal(Now.AddDays(-10), island.FromUtc);
            Assert.Equal(Now, island.ToUtc);
        }
        finally
        {
            await CleanUpAsync(entityId);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P5")]
    public async Task Рядки_подій_журналу_не_є_покриттям()
    {
        // ⛔ Події `Skipped`/`SkippedRegistry` лежать у тій самій таблиці як
        // інтервали нульової довжини зі Status. Покриттям вони не є: у
        // вибірку для прогалин не йдуть і прогалину не «латають».
        // МУТАЦІЙНИЙ ДОКАЗ (прогнано): прибрати `AND cc.Status IS NULL` у
        // `ReadCoverageIslandsAsync` — GetCoverageAsync повертає зайві острови
        // [t; t] посеред прогалини, тест червоний.
        await using var db = sql.CreateContext();
        var entityId = await ArrangeEntityAsync(db);

        try
        {
            var store = new CollectionStore(db, new TestClock(Now));
            var runId = await store.StartRunAsync(entityId, Now.AddDays(-45), Now, false, null, CancellationToken.None);

            await store.WriteCoverageAsync(
                runId,
                entityId,
                [
                    new TimeInterval(Now.AddDays(-45), Now.AddDays(-40)),
                    new TimeInterval(Now.AddDays(-30), Now),
                ],
                CancellationToken.None);

            db.CollectionCoverages.AddRange(
                CollectionCoverage.Skipped(
                    entityId, 202_602, CollectionCoverage.SkippedPeriodClosed, "P5", Now.AddDays(-35)),
                CollectionCoverage.Skipped(
                    entityId, 202_602, CollectionCoverage.ConflictKeptManual, "P5", Now.AddDays(-39)),
                CollectionCoverage.Skipped(
                    entityId, 202_602, CollectionCoverage.SkippedWriteConflict, "P5", Now.AddDays(-1)));
            await db.SaveChangesAsync(CancellationToken.None);

            var coverage = await store.GetCoverageAsync(entityId, WindowFrom, CancellationToken.None);
            Assert.Equal(
                new[]
                {
                    new TimeInterval(Now.AddDays(-45), Now.AddDays(-40)),
                    new TimeInterval(Now.AddDays(-30), Now),
                },
                coverage);

            var status = await FindStatusAsync(store, entityId);
            Assert.Equal(Now.AddDays(-40), status.OldestGap);
        }
        finally
        {
            await CleanUpAsync(entityId);
        }
    }

    private static async Task<SourceEntityStatus> FindStatusAsync(CollectionStore store, int entityId)
        => Assert.Single(
            await store.ListSourceEntitiesAsync(CancellationToken.None),
            s => s.Id == entityId);

    private static async Task<int> ArrangeEntityAsync(EcrDbContext db)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "P5" }),
            ExternalTransport.PiWebApi,
            "https://example.test",
            "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        // ⚠ Код із «0» на початку: перелік екрана впорядкований за кодом зі
        // стелею, і сутність мусить потрапити в нього в спільній базі.
        var entity = new SourceEntity(dataSource.Id, $"0P5{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        return entity.Id;
    }

    private async Task CleanUpAsync(int entityId)
    {
        await using var db = sql.CreateContext();

        // Порціями з CHECKPOINT — з тієї ж причини, що й вставка.
        await db.Database.ExecuteSqlAsync($"""
            WHILE 1 = 1
            BEGIN
                DELETE TOP ({Chunk}) FROM itg.CollectionCoverage WHERE SourceEntityId = {entityId};
                IF @@ROWCOUNT = 0 BREAK;
                CHECKPOINT;
            END;
            """);
    }

    /// <summary>Порція вставки й прибирання: тримає журнал тестової бази малим.</summary>
    private const int Chunk = 10_000;
}
