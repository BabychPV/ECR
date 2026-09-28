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

    /// <summary>Дробові частини секунди на межі округлення, у тіках (100 нс), плюс контроль.</summary>
    public static TheoryData<long> BoundaryFractions => new() { 9_995_000, 9_996_000, 9_999_000, 9_999_999, 1_234_567 };

    [Theory]
    [MemberData(nameof(BoundaryFractions))]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B4")]
    public async Task Точка_записана_старим_шляхом_EF_на_межі_мілісекунди_не_дублюється_новим_MERGE(long fractionTicks)
    {
        // ⛔ Межа «обрізання проти округлення». До виправлення сирі точки писав
        // ЛИШЕ EF (`db.RawDataPoints.Add` + `SaveChanges`, див. історію
        // `CollectionStore.UpsertRawPointsAsync`) — і цей тест пише саме так,
        // тим самим кодом. Сервер, отримавши `datetime2(7)`, ОКРУГЛЮЄ
        // (`.9996` → наступна секунда, перевірено `sqlcmd`), але EF шле
        // параметр зі `Scale = 3` за типом колонки, і SqlClient ділить тіки
        // націло — тобто ОБРІЗАЄ до того, як сервер щось побачить. Новий MERGE
        // мусить дати той самий ключ, інакше повторний збір лишить дубль поруч
        // зі старою точкою на мілісекунду раніше/пізніше.
        // МУТАЦІЙНИЙ ДОКАЗ (прогнано): у `UpsertRawPointsAsync` слати мітку
        // з повною точністю (`point.Timestamp.ToString(".fffffff")` замість
        // `ToStoredPrecision(...)` + `.fff`) — тоді `OPENJSON … Ts datetime2(3)`
        // ОКРУГЛЮЄ так, як сервер, — і для `.9995`, `.9996`, `.9999`,
        // `.9999999` у базі два рядки (4 червоні з 5; контроль `.1234567`
        // зелений, бо там округлення й обрізання збігаються). Тобто
        // «округлювати як сервер» тут і є дефект, а не виправлення.
        await using var db = sql.CreateContext();
        var entityId = await ArrangeEntityAsync(db);

        try
        {
            var store = new CollectionStore(db, new TestClock(Now));
            var runId = await store.StartRunAsync(entityId, Now.AddDays(-1), Now, false, null, CancellationToken.None);
            var ts = Now.AddHours(-3).AddTicks(fractionTicks);

            // Старий шлях — дослівно те, що робив `UpsertRawPointsAsync` до зміни.
            var legacy = new RawDataPoint(entityId, Path, ts, runId, Now);
            legacy.SetValue(1m, null, null, "Good");
            db.RawDataPoints.Add(legacy);
            await db.SaveChangesAsync(CancellationToken.None);

            var legacyStored = Assert.Single(await ReadAsync(entityId)).Timestamp;

            // Документує, що саме зробив старий шлях: обрізання до мс.
            Assert.Equal(CollectionStore.ToStoredPrecision(ts), legacyStored);

            await store.UpsertRawPointsAsync(runId, entityId, [Point(ts, 5m)], CancellationToken.None);

            var stored = Assert.Single(await ReadAsync(entityId));
            Assert.Equal(legacyStored, stored.Timestamp);
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
        // set-based INSERT порціями, кілька секунд. Батч покриває весь їхній діапазон і
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

            // ⚠ Порціями з CHECKPOINT — ті самі 100 001 рядок і ті самі мітки,
            // що одним INSERT. Один INSERT на всі рядки — це ОДНА транзакція на
            // ~53 МБ журналу (35 МБ записано + 18 МБ резерву під відкат), а
            // транзакцію посередині не звільнить жодна модель відновлення: на
            // прогоні журнал спільної бази стрибав тут 56 → 104 МБ за секунду
            // при стелі `TestDatabaseSizeTests` 128 МБ.
            await db.Database.ExecuteSqlRawAsync(
                """
                DECLARE @i int = 0, @n int;
                WHILE @i < @count
                BEGIN
                    -- TOP — усередині похідної таблиці, і RECOMPILE: з TOP
                    -- зовні над змінними план сортував увесь перехресний
                    -- добуток, порція йшла 6–19 с, і засів падав на таймауті
                    -- команди (30 с); так — ~0.3 с на порцію (заміряно).
                    SET @n = IIF(@count - @i < @chunk, @count - @i, @chunk);
                    INSERT INTO ext.RawDataPoint (SourceEntityId, SourcePath, [Timestamp], ValueNumeric, RetrievedAt, CollectionRunId)
                    SELECT @entity, @path, DATEADD(second, @i + n.i, @start), 1, @start, @run
                    FROM (SELECT TOP (@n) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS int) AS i
                          FROM sys.all_objects a CROSS JOIN sys.all_objects b) AS n
                    OPTION (RECOMPILE);
                    SET @i += @chunk;
                    CHECKPOINT;
                END;
                """,
                [
                    new SqlParameter("@count", Seeded),
                    new SqlParameter("@chunk", Chunk),
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

        // ⛔ Порціями з CHECKPOINT, а не одним `ExecuteDeleteAsync`. Одна
        // транзакція на 100 001 рядок потребує ~85 МБ журналу (32 МБ записано
        // + 53 МБ зарезервовано під відкат, заміряно `sys.dm_tran_database_transactions`),
        // а транзакцію посередині не звільнить жодна модель відновлення — саме
        // це видування підняло журнал спільної бази з 40 до 104 МБ і лишило
        // `TestDatabaseSizeTests` (стеля 128) запас у дві заливки.
        await db.Database.ExecuteSqlAsync($"""
            WHILE 1 = 1
            BEGIN
                DELETE TOP ({Chunk}) FROM ext.RawDataPoint WHERE SourceEntityId = {entityId};
                IF @@ROWCOUNT = 0 BREAK;
                CHECKPOINT;
            END;
            """);
    }

    /// <summary>Порція засіву й прибирання: тримає журнал тестової бази малим.</summary>
    private const int Chunk = 10_000;
}
