// tests/Ecr.Infrastructure.Tests/Persistence/CollectionStoreRawPointUpsertTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>UpsertRawPointsAsync</c> — ідемпотентний за природним ключем без стелі
/// читання і без падіння батча на <c>UQ_RawDataPoint</c> (аудит B4, P6).
/// </summary>
/// <remarks>
/// ⚠ Сутність кожного тесту — власна і НЕАКТИВНА (сховище активності не питає),
/// а сирі точки прибираються у <c>finally</c>: сто тисяч рядків на прогін
/// спільної бази інакше накопичувалися б.
/// </remarks>
[Collection("SqlServer")]
public sealed class CollectionStoreRawPointUpsertTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B4")]
    public async Task Мітка_з_точністю_понад_мілісекунду_знаходить_збережену_точку_а_не_дублює_її()
    {
        // ⛔ Шлях (а) між батчами: у базі лежить `.123`, джерело повертає ту
        // саму подію як `.1234567`. Раніше вибірка наявних `>= from` точку
        // `.123` навіть не бачила (`.123 < .1234567`), вставляла нову — і та
        // після обрізання до `datetime2(3)` порушувала унікальність.
        // МУТАЦІЙНИЙ ДОКАЗ (прогнано): `ToStoredPrecision` → `timestamp`,
        // `StoredTimestampFormat` → `.fffffff` і `Ts datetime2(7)` в
        // `OPENJSON … WITH` → MERGE порівнює `.1234567` з `.123`, не знаходить,
        // вставляє — тест червоний.
        await using var db = sql.CreateContext();
        var entityId = await ArrangeEntityAsync(db);

        try
        {
            var store = new CollectionStore(db, new TestClock(Now));
            var runId = await store.StartRunAsync(entityId, Now.AddDays(-1), Now, false, null, CancellationToken.None);
            var ts = Now.AddHours(-3);

            await store.UpsertRawPointsAsync(
                runId, entityId, [Point(ts.AddMilliseconds(123), 1m)], CancellationToken.None);

            var written = await store.UpsertRawPointsAsync(
                runId, entityId, [Point(ts.AddTicks(1_234_567), 5m)], CancellationToken.None);

            Assert.Equal(1, written);

            var stored = Assert.Single(await ReadAsync(entityId));
            Assert.Equal(ts.AddMilliseconds(123), stored.Timestamp);
            Assert.Equal(5m, stored.ValueNumeric);
        }
        finally
        {
            await CleanUpAsync(entityId);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B4")]
    public async Task Дублікат_мітки_в_батчі_згортається_і_виграє_остання_точка()
    {
        // ⛔ Шлях (б) на рівні сховища. Лічильник — РІЗНІ ключі, а не довжина
        // батча: прогін рапортує саме записані точки.
        // МУТАЦІЙНИЙ ДОКАЗ (прогнано): прибрати `WHERE rn = 1` → MERGE із двома
        // рядками на один ключ падає, тест червоний.
        await using var db = sql.CreateContext();
        var entityId = await ArrangeEntityAsync(db);

        try
        {
            var store = new CollectionStore(db, new TestClock(Now));
            var runId = await store.StartRunAsync(entityId, Now.AddDays(-1), Now, false, null, CancellationToken.None);
            var ts = Now.AddHours(-2);

            var written = await store.UpsertRawPointsAsync(
                runId,
                entityId,
                [Point(ts, 1m), Point(ts.AddMinutes(1), 7m), Point(ts, 2m)],
                CancellationToken.None);

            Assert.Equal(2, written);

            var stored = await ReadAsync(entityId);
            Assert.Equal([2m, 7m], stored.Select(p => p.ValueNumeric ?? 0m));
        }
        finally
        {
            await CleanUpAsync(entityId);
        }
    }

    [Fact(Timeout = 300_000)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P6")]
    public async Task Понад_сто_тисяч_наявних_точок_у_діапазоні_батча_дублікати_розпізнаються_а_нові_пишуться()
    {
        // ⛔ P6: стеля читання наявних точок (`Take(100 000)` за `Id`) була
        // ПРИВАТНОЮ константою, без параметра, тому тут справді 100 001 рядок —
        // один set-based INSERT, ~1 с. Батч покриває весь їхній діапазон і
        // містить (1) точку, що збігається з ОСТАННЬОЮ заведеною (вона була за
        // стелею й не потрапляла в індекс), і (2) нову. Раніше (1) вставлялася
        // вдруге, і `UQ_RawDataPoint` відкидав увесь батч разом із (2).
        // ДОКАЗ — червоний прогін на коді до виправлення (2601 на точці (1)).
        // Окремого мутанта в новому коді немає: стелі там немає як такої, ключ
        // порівнює база — повернути дефект означає повернути читання в пам'ять.
        const int Seeded = 100_001;

        await using var db = sql.CreateContext();
        var entityId = await ArrangeEntityAsync(db);

        try
        {
            var store = new CollectionStore(db, new TestClock(Now));
            var runId = await store.StartRunAsync(entityId, Now.AddDays(-5), Now, false, null, CancellationToken.None);
            var start = Now.AddDays(-4);

            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO ext.RawDataPoint (SourceEntityId, SourcePath, [Timestamp], ValueNumeric, RetrievedAt, CollectionRunId)
                SELECT TOP (@count) @entity, @path, DATEADD(second, n.i, @start), 1, @start, @run
                FROM (SELECT CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS int) AS i
                      FROM sys.all_objects a CROSS JOIN sys.all_objects b) AS n
                ORDER BY n.i;
                """,
                [
                    new SqlParameter("@count", Seeded),
                    new SqlParameter("@entity", entityId),
                    new SqlParameter("@path", Path),
                    new SqlParameter("@start", System.Data.SqlDbType.DateTime2) { Scale = 3, Value = start },
                    new SqlParameter("@run", runId),
                ],
                CancellationToken.None);

            var lastSeeded = start.AddSeconds(Seeded - 1);
            var fresh = lastSeeded.AddSeconds(1);

            var written = await store.UpsertRawPointsAsync(
                runId,
                entityId,
                [Point(start, 1m), Point(lastSeeded, 99m), Point(fresh, 42m)],
                CancellationToken.None);

            Assert.Equal(3, written);

            await using var check = sql.CreateContext();
            var points = check.RawDataPoints.AsNoTracking().Where(p => p.SourceEntityId == entityId);

            Assert.Equal(Seeded + 1, await points.CountAsync());
            Assert.Equal(99m, (await points.SingleAsync(p => p.Timestamp == lastSeeded)).ValueNumeric);
            Assert.Equal(42m, (await points.SingleAsync(p => p.Timestamp == fresh)).ValueNumeric);
        }
        finally
        {
            await CleanUpAsync(entityId);
        }
    }

    private const string Path = "Stack|CO";

    private static SourceDataPoint Point(DateTime timestamp, decimal value)
        => new(Path, timestamp, value, null, null, "Good");

    private async Task<List<RawDataPoint>> ReadAsync(int entityId)
    {
        await using var check = sql.CreateContext();

        return await check.RawDataPoints.AsNoTracking()
            .Where(p => p.SourceEntityId == entityId)
            .OrderBy(p => p.Timestamp)
            .ToListAsync();
    }

    private static async Task<int> ArrangeEntityAsync(EcrDbContext db)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "P6" }),
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

    private async Task CleanUpAsync(int entityId)
    {
        await using var db = sql.CreateContext();

        await db.RawDataPoints
            .Where(p => p.SourceEntityId == entityId)
            .ExecuteDeleteAsync(CancellationToken.None);
    }
}
