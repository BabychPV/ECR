// tests/Ecr.Infrastructure.Tests/Persistence/SourceEventSchemaTests.cs
using System.Data.Common;
using System.Globalization;
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
/// Міграція <c>HSE301M5SourceEvents</c> на РЕАЛЬНОМУ SQL Server (FEATURE-HSE301-VIEW
/// §4.7.3, крок F9, <c>D-186</c>): <c>ext.SourceEventMap</c>, <c>ext.SourceEventFieldMap</c>,
/// <c>ext.SourceEventValueMap</c>, <c>ext.SourceEventLink</c>.
/// </summary>
/// <remarks>
/// ⛔ Інваріанти, які домен уже тримає (<c>SourceEventMapTests</c>,
/// <c>SourceEventLinkTests</c>), перевіряються вставкою ПОВЗ домен: база мусить
/// тримати їх і для скриптів, імпорту та DBA.
///
/// Мутаційні докази (F9):
/// <list type="bullet">
/// <item>прибрати <c>UQ_SEL_Event</c> з <c>SourceEventLinkConfiguration</c> — червоніє
/// <see cref="Модель_тримає_унікальність_події_в_мапінгу"/>;</item>
/// <item>прибрати <c>unique</c> з <c>UQ_SEL_Event</c> у міграції — червоніє
/// <see cref="Друга_прив_язка_тієї_самої_події_відхиляється_базою"/>.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class SourceEventSchemaTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 29, 9, 30, 15, 123, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-F9")]
    public async Task Чотири_таблиці_мапінгу_подій_існують()
    {
        var tables = await QueryAsync("""
            SELECT t.name
            FROM sys.tables t
            WHERE t.schema_id = SCHEMA_ID(N'ext') AND t.name LIKE N'SourceEvent%'
            ORDER BY t.name
            """);

        Assert.Equal(["SourceEventFieldMap", "SourceEventLink", "SourceEventMap", "SourceEventValueMap"], tables);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-F9")]
    public async Task Модель_тримає_унікальність_події_в_мапінгу()
    {
        await using var db = sql.CreateContext();
        var entity = db.Model.FindEntityType(typeof(SourceEventLink))!;

        var unique = entity.GetIndexes()
            .Where(i => i.IsUnique)
            .Select(i => $"{i.GetDatabaseName()}({string.Join(", ", i.Properties.Select(p => p.Name))})");

        Assert.Contains("UQ_SEL_Event(SourceEventMapId, SourceEventId)", unique);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-F9")]
    public async Task Мапінг_з_полями_зберігається_як_задано()
    {
        var arranged = await ArrangeAsync();
        int mapId;

        await using (var db = sql.CreateContext())
        {
            var map = await NewMapAsync(db, arranged);
            map.SetFilter("Flare", SourceEventAttributeScope.PrimaryElement, "FL-370");
            map.Deactivate();
            db.SourceEventMaps.Add(map);
            await db.SaveChangesAsync();
            mapId = map.Id;
        }

        await using var read = sql.CreateContext();
        var stored = await read.SourceEventMaps.AsNoTracking().Include(m => m.Fields)
            .SingleAsync(m => m.Id == mapId);

        // ⚠ IsActive = false — CLR-замовчування. Без ValueGeneratedNever EF його
        // не надіслав би, і в рядок ліг би DEFAULT 1 (EnumDefaultSentinelTests).
        Assert.Equal(
            (arranged.EntityId, arranged.DocumentId, arranged.TableDefId, SourceEventVolumeMode.RowWindow, false,
             "Flare", (SourceEventAttributeScope?)SourceEventAttributeScope.PrimaryElement, "FL-370"),
            (stored.SourceEntityId, stored.DocumentId, stored.TableDefId, stored.VolumeMode, stored.IsActive,
             stored.FilterAttribute, stored.FilterScope, stored.FilterValue));
        Assert.Equal(8, stored.RowVersion.Length);
        Assert.Equal(
            ["$end", "$name", "$start"],
            stored.Fields.Select(f => f.SourceAttribute).Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-F9")]
    public async Task Другий_мапінг_того_самого_шаблону_в_ту_саму_таблицю_відхиляється()
    {
        var arranged = await ArrangeAsync();

        await using (var db = sql.CreateContext())
        {
            db.SourceEventMaps.Add(await NewMapAsync(db, arranged));
            await db.SaveChangesAsync();
        }

        // Домен не бачить чужих мапінгів — тримає база.
        await using var again = sql.CreateContext();
        again.SourceEventMaps.Add(await NewMapAsync(again, arranged));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => again.SaveChangesAsync());
        Assert.Contains("UQ_SourceEventMap", error.InnerException?.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-F9")]
    public async Task Друга_прив_язка_тієї_самої_події_відхиляється_базою()
    {
        var arranged = await ArrangeAsync();
        var mapId = await SavedMapIdAsync(arranged);
        var observation = new SourceEventObservation("9b1c-ef", "Flaring 370", Now, Now.AddMinutes(15), Now);

        await using (var db = sql.CreateContext())
        {
            db.SourceEventLinks.Add(SourceEventLink.FirstSeenWritten(
                mapId, observation, new SourceEventRowRef(arranged.PeriodKey, arranged.TableInstanceId, "EF-9b1c-ef"),
                keptManualJson: null, unmappedJson: null, Now));
            await db.SaveChangesAsync();
        }

        // Той самий ID події з іншого контексту (два прогони синхронізації
        // паралельно): повтор не має дати ні другого зв'язку, ні другого рядка.
        await using var again = sql.CreateContext();
        again.SourceEventLinks.Add(SourceEventLink.FirstSeenUnwritten(
            mapId, observation, SourceEventLinkStatus.PeriodNotOpen, Now));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => again.SaveChangesAsync());
        Assert.Contains("UQ_SEL_Event", error.InnerException?.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-F9")]
    public async Task Зв_язок_зберігає_стан_текстом_і_час_в_UTC()
    {
        var arranged = await ArrangeAsync();
        var mapId = await SavedMapIdAsync(arranged);
        long linkId;

        await using (var db = sql.CreateContext())
        {
            var link = SourceEventLink.FirstSeenWritten(
                mapId, new SourceEventObservation("ev-1", null, Now, Now.AddMinutes(15), null),
                new SourceEventRowRef(arranged.PeriodKey, arranged.TableInstanceId, "EF-ev-1"),
                keptManualJson: "[\"VOLUME_SM3\"]", unmappedJson: null, Now);
            link.MarkMissing(Now.AddDays(1));
            db.SourceEventLinks.Add(link);
            await db.SaveChangesAsync();
            linkId = link.Id;
        }

        var status = await QueryAsync($"SELECT Status FROM ext.SourceEventLink WHERE Id = {linkId}");
        Assert.Equal(["Missing"], status);

        await using var read = sql.CreateContext();
        var stored = await read.SourceEventLinks.AsNoTracking().SingleAsync(l => l.Id == linkId);
        Assert.Equal(
            (SourceEventLinkStatus.Missing, DateTimeKind.Utc, Now, (DateTime?)Now.AddMinutes(15), "EF-ev-1", "[\"VOLUME_SM3\"]"),
            (stored.Status, stored.StartUtc.Kind, stored.StartUtc, stored.EndUtc, stored.RowKey, stored.KeptManualJson));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-F9")]
    [InlineData("N'Ok'", "NULL", "CK_SEL_Status")]
    [InlineData("N'Synced'", "NULL", "CK_SEL_StatusRow")]
    [InlineData("N'Open'", "N'EF-1'", "CK_SEL_StatusRow")]
    public async Task CHECK_зв_язку_відхиляє_стан_що_не_пасує_до_рядка(string status, string rowKey, string constraint)
    {
        var arranged = await ArrangeAsync();
        var mapId = await SavedMapIdAsync(arranged);
        var hasRow = rowKey != "NULL";
        var periodKey = hasRow ? arranged.PeriodKey.ToString(CultureInfo.InvariantCulture) : "NULL";
        var instanceId = hasRow ? arranged.TableInstanceId.ToString(CultureInfo.InvariantCulture) : "NULL";

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync($"""
            INSERT INTO ext.SourceEventLink (SourceEventMapId, SourceEventId, PeriodKey, TableInstanceId, RowKey,
                StartUtc, Status, FirstSeenAt, LastSeenAt, LastSyncAt)
            VALUES ({mapId}, N'ck-{Guid.NewGuid():N}', {periodKey},
                {instanceId}, {rowKey},
                SYSUTCDATETIME(), {status}, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME())
            """));

        Assert.Contains(constraint, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-F9")]
    public async Task Друге_поле_на_ту_саму_колонку_відхиляється_базою()
    {
        var arranged = await ArrangeAsync();
        var mapId = await SavedMapIdAsync(arranged);

        // $start уже лежить у StartId (NewMapAsync) — друге поле на ту саму колонку.
        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync($"""
            INSERT INTO ext.SourceEventFieldMap (SourceEventMapId, TargetColumnDefId, SourceAttribute, AttributeScope, ValueKind)
            VALUES ({mapId}, {arranged.StartId}, N'StartTime', 0, 0)
            """));

        Assert.Contains("UQ_SEFM_Target", error.Message, StringComparison.Ordinal);
    }

    private async Task<int> SavedMapIdAsync(Arranged arranged)
    {
        await using var db = sql.CreateContext();
        var map = await NewMapAsync(db, arranged);
        db.SourceEventMaps.Add(map);
        await db.SaveChangesAsync();
        return map.Id;
    }

    private static async Task<SourceEventMap> NewMapAsync(EcrDbContext db, Arranged arranged)
    {
        var table = await db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == arranged.TableDefId);
        var columns = await db.ColumnDefs.AsNoTracking()
            .Where(c => c.TableDefId == arranged.TableDefId)
            .ToDictionaryAsync(c => c.Id);

        return SourceEventMap.Create(
            arranged.EntityId, arranged.DocumentId, table,
            [
                new(columns[arranged.StartId], SourceEventMap.StartAttribute),
                new(columns[arranged.EndId], SourceEventMap.EndAttribute),
                new(columns[arranged.NameId], SourceEventMap.NameAttribute),
            ],
            SourceEventVolumeMode.RowWindow);
    }

    /// <summary>
    /// Динамічна таблиця з колонками Start/End (Date) і назвою (String), документ,
    /// екземпляр і неактивна сутність-шаблон подій.
    /// </summary>
    /// <remarks>
    /// ⚠ Сутність НЕАКТИВНА навмисно (як у <c>RowWindowSchemaTests</c>): активна без
    /// завершеного збору фарбувала б <c>SourcesHealthCheck</c> у спільній базі.
    /// </remarks>
    private async Task<Arranged> ArrangeAsync()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(periodKey: 202601, rowMode: TableRowMode.Dynamic);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        await using var db = sql.CreateContext();

        var start = new ColumnDef(doc.TableDefId, EcrCode.Create($"START_{tag}"), Name("Start"), 10, CellDataType.Date);
        var end = new ColumnDef(doc.TableDefId, EcrCode.Create($"END_{tag}"), Name("End"), 11, CellDataType.Date);
        db.ColumnDefs.AddRange(start, end);

        var dataSource = new DataSource(
            EcrCode.Create($"SESRC{tag}"), Name("HSE301-F9"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync();

        var entity = new SourceEntity(dataSource.Id, $"FlareEvents{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync();

        return new Arranged(
            doc.TableDefId, start.Id, end.Id, NameId: doc.ColumnDefIds[0], entity.Id, doc.DocumentId,
            doc.PeriodKey.Value, doc.TableInstanceId);
    }

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

    private sealed record Arranged(
        int TableDefId,
        int StartId,
        int EndId,
        int NameId,
        int EntityId,
        long DocumentId,
        int PeriodKey,
        long TableInstanceId);
}
