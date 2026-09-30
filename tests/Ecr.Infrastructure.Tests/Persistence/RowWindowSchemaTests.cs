// tests/Ecr.Infrastructure.Tests/Persistence/RowWindowSchemaTests.cs
using System.Data.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
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
/// Міграція <c>HSE301M2RowWindow</c> на РЕАЛЬНОМУ SQL Server (FEATURE-HSE301-VIEW
/// §4.4, крок F5, <c>D-171</c>): <c>ext.RowWindowMap</c>, <c>ext.RowWindowSource</c>,
/// <c>ext.RowWindowValue</c>.
/// </summary>
/// <remarks>
/// ⛔ Інваріанти перевіряються вставкою ПОВЗ домен там, де домен їх уже тримає
/// (<c>RowWindowMapTests</c>): база мусить тримати їх і для скриптів, імпорту
/// та DBA — туди, куди C# не дістає.
///
/// Мутаційні докази (F5):
/// <list type="bullet">
/// <item>прибрати <c>unique</c> з <c>UQ_RowWindowMap_Target</c> у міграції —
/// червоніє <see cref="Друга_прив_язка_на_ту_саму_ціль_відхиляється"/>;</item>
/// <item>прибрати рядок <c>RowWindowValue</c> із <c>07-partition-tables.sql</c> —
/// червоніє <see cref="RowWindowValue_лежить_на_схемі_партиціонування_ключ_починається_з_PeriodKey"/>.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class RowWindowSchemaTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 28, 9, 30, 15, 123, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-F5")]
    public async Task Прив_язка_з_джерелами_зберігається_як_задано()
    {
        var arranged = await ArrangeAsync();
        int mapId;

        await using (var db = sql.CreateContext())
        {
            var map = await NewMapAsync(db, arranged);
            map.SetFetchPolicy(0m, 3, TimeSpan.FromMinutes(10));
            map.AddSource(null, arranged.EntityId, "Flare HP|Flow", arranged.UnitId);
            map.AddSource("FLARE_LP", arranged.EntityId, "Flare LP|Flow", arranged.UnitId);
            db.RowWindowMaps.Add(map);
            await db.SaveChangesAsync();
            mapId = map.Id;
        }

        await using var read = sql.CreateContext();
        var stored = await read.RowWindowMaps.AsNoTracking().Include(m => m.Sources).SingleAsync(m => m.Id == mapId);

        // ⚠ MinPercentGood = 0 — CLR-замовчування. Без ValueGeneratedNever EF
        // його не надіслав би, і в рядок ліг би DEFAULT 95 (EnumDefaultSentinelTests).
        Assert.Equal(
            (arranged.TableDefId, arranged.VolumeId, arranged.StartId, arranged.EndId, (int?)arranged.SelectorId,
             RowWindowSummaryKind.Total, true, 0m, 3, (int?)600, true),
            (stored.TableDefId, stored.TargetColumnDefId, stored.StartColumnDefId, stored.EndColumnDefId,
             stored.SelectorColumnDefId, stored.Summary, stored.IsStep, stored.MinPercentGood,
             stored.RefetchWithinDays, stored.MaxGapSeconds, stored.IsActive));
        Assert.Equal(8, stored.RowVersion.Length);
        Assert.Equal(
            ["(all) -> Flare HP|Flow", "FLARE_LP -> Flare LP|Flow"],
            stored.Sources.Select(s => $"{s.SelectorValue ?? "(all)"} -> {s.SourceField}").Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-F5")]
    public async Task Друга_прив_язка_на_ту_саму_ціль_відхиляється()
    {
        var arranged = await ArrangeAsync();

        await using (var db = sql.CreateContext())
        {
            db.RowWindowMaps.Add(await NewMapAsync(db, arranged));
            await db.SaveChangesAsync();
        }

        // Той самий селектор-пара «таблиця + цільова колонка» з іншого контексту:
        // домен цього не бачить (він не знає про чужі прив'язки), тримає база.
        await using var again = sql.CreateContext();
        again.RowWindowMaps.Add(await NewMapAsync(again, arranged, summary: RowWindowSummaryKind.Average));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => again.SaveChangesAsync());
        Assert.Contains("UQ_RowWindowMap_Target", error.InnerException?.Message, StringComparison.Ordinal);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-F5")]
    [InlineData("N'FLARE_HP'", "N'flare_hp'")]
    [InlineData("NULL", "NULL")]
    public async Task Дубль_значення_селектора_відхиляється_базою(string first, string second)
    {
        var arranged = await ArrangeAsync();
        int mapId;

        await using (var db = sql.CreateContext())
        {
            var map = await NewMapAsync(db, arranged);
            db.RowWindowMaps.Add(map);
            await db.SaveChangesAsync();
            mapId = map.Id;
        }

        await InsertSourceAsync(mapId, first, arranged);

        // ⚠ Другий NULL теж дубль: джерело «для всіх рядків» у прив'язки одне,
        // і саме тому з UQ_RowWindowSource знято типовий фільтр IS NOT NULL.
        var error = await Assert.ThrowsAsync<SqlException>(() => InsertSourceAsync(mapId, second, arranged));
        Assert.Contains("UQ_RowWindowSource", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-F5")]
    public async Task Колонка_вікна_з_чужої_таблиці_відхиляється_складеним_ключем()
    {
        var arranged = await ArrangeAsync();
        var foreign = await ArrangeAsync();

        // Id колонки справжній, але належить іншій таблиці: ключ лише на Id
        // це пропустив би.
        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync($"""
            INSERT INTO ext.RowWindowMap (TableDefId, TargetColumnDefId, StartColumnDefId, EndColumnDefId,
                                          Summary, TargetUnitId)
            VALUES ({arranged.TableDefId}, {arranged.VolumeId}, {foreign.StartId}, {arranged.EndId},
                    0, {arranged.UnitId})
            """));

        Assert.Contains("FK_RWM_Start", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-F5")]
    [InlineData("Summary", "5", "CK_RWM_Summary")]
    [InlineData("MinPercentGood", "100.5", "CK_RWM_Policy")]
    [InlineData("MaxGapSeconds", "0", "CK_RWM_Policy")]
    public async Task CHECK_прив_язки_відхиляє_значення_поза_переліком(string column, string value, string constraint)
    {
        var arranged = await ArrangeAsync();

        // Рядок, валідний в усьому, крім одного стовпця.
        var row = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TableDefId"] = Sql(arranged.TableDefId),
            ["TargetColumnDefId"] = Sql(arranged.VolumeId),
            ["StartColumnDefId"] = Sql(arranged.StartId),
            ["EndColumnDefId"] = Sql(arranged.EndId),
            ["Summary"] = "0",
            ["TargetUnitId"] = Sql(arranged.UnitId),
        };
        row[column] = value;

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            $"INSERT INTO ext.RowWindowMap ({string.Join(", ", row.Keys)}) VALUES ({string.Join(", ", row.Values)})"));

        Assert.Contains(constraint, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-F5")]
    public async Task RowWindowValue_лежить_на_схемі_партиціонування_ключ_починається_з_PeriodKey()
    {
        // ⚠ Регресія на Q-035 для нової таблиці: міграція EF кладе таблицю на
        // PRIMARY, і без рядка в 07-partition-tables.sql архівація й SPLIT/MERGE
        // мовчки не бачили б її партицій.
        var placement = await QueryAsync("""
            SELECT i.name + N' -> ' + ds.type_desc COLLATE DATABASE_DEFAULT
            FROM sys.indexes i
            JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
            WHERE i.object_id = OBJECT_ID(N'ext.RowWindowValue') AND i.type IN (1, 2)
            ORDER BY i.name
            """);

        Assert.Equal(
            ["PK_RowWindowValue -> PARTITION_SCHEME", "UX_RowWindowValue_Current -> PARTITION_SCHEME"],
            placement);

        var key = await QueryAsync("""
            SELECT c.name
            FROM sys.index_columns ic
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE ic.object_id = OBJECT_ID(N'ext.RowWindowValue') AND ic.index_id = 1 AND ic.is_included_column = 0
            ORDER BY ic.key_ordinal
            """);

        Assert.Equal(["PeriodKey", "Id"], key);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-F5")]
    public async Task Чинне_значення_комірки_одне_історія_лишається()
    {
        var arranged = await ArrangeAsync();
        var periodKey = arranged.PeriodKey;
        long firstId;

        await using (var db = sql.CreateContext())
        {
            var map = await NewMapAsync(db, arranged);
            db.RowWindowMaps.Add(map);
            await db.SaveChangesAsync();

            var first = Value(arranged, map.Id);
            first.Record(RowWindowValueStatus.Fetched, RowWindowComputedBy.Local, 1042.3m, "Sm3/h", 269.258m,
                0.000277777777777778m, 12, 99.5m, null);
            db.RowWindowValues.Add(first);
            await db.SaveChangesAsync();
            firstId = first.Id;

            // Друге чинне значення тієї самої комірки без зняття першого — відмова бази.
            db.RowWindowValues.Add(Value(arranged, map.Id));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("UX_RowWindowValue_Current", error.InnerException?.Message, StringComparison.Ordinal);
        }

        await using (var db = sql.CreateContext())
        {
            // Спершу зняти чинність попереднього, потім додати нове — два кроки,
            // щоб не залежати від порядку команд усередині одного SaveChanges.
            var previous = await db.RowWindowValues.SingleAsync(v => v.PeriodKey == periodKey && v.Id == firstId);
            previous.Supersede();
            await db.SaveChangesAsync();

            var mapId = previous.RowWindowMapId;
            var next = Value(arranged, mapId);
            next.Record(RowWindowValueStatus.NoData, RowWindowComputedBy.Local, null, null, null, null, 0, null, null);
            db.RowWindowValues.Add(next);
            await db.SaveChangesAsync();
        }

        await using var read = sql.CreateContext();
        var rows = await read.RowWindowValues.AsNoTracking()
            .Where(v => v.PeriodKey == periodKey && v.TableInstanceId == arranged.TableInstanceId)
            .OrderBy(v => v.Id)
            .ToListAsync();

        var expected = new (bool, RowWindowValueStatus, decimal?, decimal?)[]
        {
            (false, RowWindowValueStatus.Fetched, 269.258m, 99.5m),
            (true, RowWindowValueStatus.NoData, null, null),
        };
        Assert.Equal(expected, rows.Select(r => (r.IsCurrent, r.Status, r.ValueTarget, r.PercentGood)));

        // Моменти читаються з Kind=Utc (V-13) і без втрати мілісекунд.
        Assert.Equal((DateTimeKind.Utc, Now), (rows[0].FromUtc.Kind, rows[0].FromUtc));
        Assert.Equal(Now.AddSeconds(930), rows[0].ToUtc);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-F5")]
    public async Task Статус_провенансу_поза_переліком_відхиляється()
    {
        var arranged = await ArrangeAsync();
        int mapId;

        await using (var db = sql.CreateContext())
        {
            var map = await NewMapAsync(db, arranged);
            db.RowWindowMaps.Add(map);
            await db.SaveChangesAsync();
            mapId = map.Id;
        }

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync($"""
            INSERT INTO ext.RowWindowValue (PeriodKey, TableInstanceId, RowKey, ColumnDefId, RowWindowMapId,
                SourceEntityId, SourceField, FromUtc, ToUtc, Summary, ComputedBy, TargetUnitId, PointCount,
                Status, RetrievedAt)
            VALUES ({arranged.PeriodKey}, {arranged.TableInstanceId}, N'R1', {arranged.VolumeId}, {mapId},
                {arranged.EntityId}, N'Flow', SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 0, {arranged.UnitId}, 0,
                N'Ok', SYSUTCDATETIME())
            """));

        Assert.Contains("CK_RWV_Status", error.Message, StringComparison.Ordinal);
    }

    private static RowWindowValue Value(Arranged arranged, int mapId)
        => new(
            arranged.PeriodKey, arranged.TableInstanceId, arranged.RowKey, arranged.VolumeId, mapId,
            arranged.EntityId, "Flare HP|Flow", Now, Now.AddSeconds(930), RowWindowSummaryKind.Total,
            arranged.UnitId, Now.AddHours(1));

    private static async Task<RowWindowMap> NewMapAsync(
        EcrDbContext db, Arranged arranged, RowWindowSummaryKind summary = RowWindowSummaryKind.Total)
    {
        var columns = await db.ColumnDefs.AsNoTracking()
            .Where(c => c.TableDefId == arranged.TableDefId)
            .ToDictionaryAsync(c => c.Id);

        return RowWindowMap.Create(
            columns[arranged.VolumeId], columns[arranged.StartId], columns[arranged.EndId],
            columns[arranged.SelectorId], summary, isStep: true, arranged.UnitId);
    }

    /// <summary>
    /// Таблиця з колонками вікна (Date), цільовою (Decimal) і селектором (String),
    /// екземпляр, одиниця й неактивна сутність джерела.
    /// </summary>
    /// <remarks>
    /// ⚠ Сутність НЕАКТИВНА навмисно (як у <c>CollectionScheduleWindowTests</c>):
    /// активна без завершеного збору лишилась би в спільній базі й фарбувала б
    /// <c>SourcesHealthCheck</c> для кожного наступного прогону.
    /// </remarks>
    private async Task<Arranged> ArrangeAsync()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 202601);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        await using var db = sql.CreateContext();

        var start = new ColumnDef(doc.TableDefId, EcrCode.Create($"START_{tag}"), Name("Start"), 10, CellDataType.Date);
        var end = new ColumnDef(doc.TableDefId, EcrCode.Create($"END_{tag}"), Name("End"), 11, CellDataType.Date);
        db.ColumnDefs.AddRange(start, end);

        var dataSource = new DataSource(
            EcrCode.Create($"RWSRC{tag}"), Name("HSE301-F5"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync();

        var entity = new SourceEntity(dataSource.Id, $"RwEnt{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync();

        var unitId = await db.Units.OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        var firstRowId = doc.RowIds[0];
        var periodKey = doc.PeriodKey.Value;
        var rowKey = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == periodKey && r.Id == firstRowId)
            .Select(r => r.RowKeyValue)
            .SingleAsync();

        return new Arranged(
            doc.TableDefId, VolumeId: doc.ColumnDefIds[1], start.Id, end.Id, SelectorId: doc.ColumnDefIds[0],
            entity.Id, unitId, doc.PeriodKey.Value, doc.TableInstanceId, rowKey);
    }

    private async Task InsertSourceAsync(int mapId, string selectorLiteral, Arranged arranged)
        => await ExecuteAsync($"""
            INSERT INTO ext.RowWindowSource (RowWindowMapId, SelectorValue, SourceEntityId, SourceField, SourceUnitId)
            VALUES ({mapId}, {selectorLiteral}, {arranged.EntityId}, N'Flow', {arranged.UnitId})
            """);

    private async Task ExecuteAsync(string sqlText)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> QueryAsync(string query)
    {
        var rows = new List<string>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private static LocalizedText Name(string en) => new(new Dictionary<string, string> { ["en"] = en });

    private static string Sql(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private sealed record Arranged(
        int TableDefId,
        int VolumeId,
        int StartId,
        int EndId,
        int SelectorId,
        int EntityId,
        int UnitId,
        int PeriodKey,
        long TableInstanceId,
        string RowKey);
}

/// <summary>
/// Переліки домену прив'язки збігаються з переліками порту джерела.
/// </summary>
/// <remarks>
/// ⛔ Домен на <c>Ecr.Application</c> не посилається, тож <see cref="RowWindowSummaryKind"/>
/// і <see cref="RowWindowComputedBy"/> — копії <see cref="SourceSummaryKind"/> і
/// <see cref="WindowComputedBy"/>. Задача підтягування (A1) перекладає одне в інше
/// приведенням числа; розбіжність дала б запит іншої згортки, ніж налаштовано, —
/// правдоподібне число без жодної помилки. Тут — у проєкті, що бачить обидва.
/// </remarks>
public sealed class RowWindowEnumParityTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F5")]
    public void Перелік_згортки_збігається_з_портом_джерела()
    {
        Assert.Equal(Pairs<SourceSummaryKind>(), Pairs<RowWindowSummaryKind>());
        Assert.Equal(Pairs<WindowComputedBy>(), Pairs<RowWindowComputedBy>());
    }

    private static List<(string Name, byte Value)> Pairs<T>() where T : struct, Enum
        => [.. Enum.GetValues<T>().Select(v => (v.ToString(), Convert.ToByte(v, System.Globalization.CultureInfo.InvariantCulture)))];
}
