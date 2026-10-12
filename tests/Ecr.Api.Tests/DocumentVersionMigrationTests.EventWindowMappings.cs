// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.EventWindowMappings.cs
using System.Net;
using System.Text.Json;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D-1b (приймальна RC20): той самий клас дефекту, що D-1, для мапінгів подій джерела
/// (<c>ext.SourceEventMap</c>/<c>SourceEventFieldMap</c>) і прив'язок вікна (<c>ext.RowWindowMap</c>): перенос
/// проєкту на іншу версію шаблону переносить їх на таблицю/колонки цільової версії в тій самій транзакції,
/// не зачіпає мапінги чужих документів, а активний мапінг без відповідника не лишає мовчки.
/// </summary>
/// <remarks>
/// ⚠ Мапінги створюються прямим SQL: доменні фабрики вимагають динамічної таблиці й колонок типу Date/Decimal,
/// а сценарій переносу має фіксовану таблицю з рядковими/десятковими колонками. БД типів не перевіряє.
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    private async Task<int> AddSourceEntityAsync()
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..10];

        var dataSource = new DataSource(
            EcrCode.Create($"EwSrc{tag}"),
            new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "Source" }),
            ExternalTransport.PiWebApi,
            "https://example.test",
            "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // Неактивна сутність навмисно: спільна база тестів, див. AddMappingAsync.
        var entity = new SourceEntity(dataSource.Id, $"EwEnt{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return entity.Id;
    }

    /// <returns>(мапінг, поле).</returns>
    private async Task<(int MapId, int FieldId)> AddEventMapAsync(
        long documentId, int tableDefId, int columnDefId, bool active = true)
    {
        var entityId = await AddSourceEntityAsync().ConfigureAwait(false);
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        var isActive = active ? 1 : 0;
        var mapId = await InsertReturningIdAsync(db, $"""
            INSERT ext.SourceEventMap (SourceEntityId, DocumentId, TableDefId, VolumeMode, IsActive)
            OUTPUT INSERTED.Id AS Value
            VALUES ({entityId}, {documentId}, {tableDefId}, 0, {isActive})
            """).ConfigureAwait(false);
        var fieldId = await InsertReturningIdAsync(db, $"""
            INSERT ext.SourceEventFieldMap (SourceEventMapId, TargetColumnDefId, SourceAttribute, AttributeScope, ValueKind)
            OUTPUT INSERTED.Id AS Value
            VALUES ({mapId}, {columnDefId}, N'Attr', 0, 0)
            """).ConfigureAwait(false);
        return (mapId, fieldId);
    }

    /// <summary>INSERT ... OUTPUT не композується: читається списком, не <c>SingleAsync</c>.</summary>
    private static async Task<int> InsertReturningIdAsync(Ecr.Infrastructure.Persistence.EcrDbContext db, FormattableString sqlText)
    {
        var ids = await db.Database.SqlQuery<int>(sqlText).ToListAsync().ConfigureAwait(false);
        return Assert.Single(ids);
    }

    private async Task<int> AddWindowMapAsync(
        int tableDefId, int target, int start, int end, bool active = true)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var unitId = await db.Database
            .SqlQuery<int>($"SELECT TOP (1) Id AS Value FROM uom.Unit ORDER BY Id")
            .SingleAsync().ConfigureAwait(false);
        var isActive = active ? 1 : 0;

        return await InsertReturningIdAsync(db, $"""
            INSERT ext.RowWindowMap (TableDefId, TargetColumnDefId, StartColumnDefId, EndColumnDefId,
                                     Summary, IsStep, TargetUnitId, MinPercentGood, RefetchWithinDays, IsActive)
            OUTPUT INSERTED.Id AS Value
            VALUES ({tableDefId}, {target}, {start}, {end}, 0, 0, {unitId}, 90, 7, {isActive})
            """).ConfigureAwait(false);
    }

    private async Task<(int TableDefId, int FieldColumn)> EventMapStateAsync(int mapId, int fieldId)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var table = await db.SourceEventMaps.AsNoTracking()
            .Where(m => m.Id == mapId).Select(m => m.TableDefId).SingleAsync().ConfigureAwait(false);
        var column = await db.SourceEventFieldMaps.AsNoTracking()
            .Where(f => f.Id == fieldId).Select(f => f.TargetColumnDefId).SingleAsync().ConfigureAwait(false);
        return (table, column);
    }

    private async Task<(int Table, int Target, int Start, int End)> WindowMapStateAsync(int mapId)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var m = await db.RowWindowMaps.AsNoTracking().SingleAsync(x => x.Id == mapId).ConfigureAwait(false);
        return (m.TableDefId, m.TargetColumnDefId, m.StartColumnDefId, m.EndColumnDefId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-1b")]
    public async Task Мапінг_подій_і_вікно_переїжджають_на_таблицю_й_колонки_цільової_версії_чужий_документ_не_зачеплено()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        var c = s.Doc.ColumnDefIds;
        var events = await AddEventMapAsync(s.Doc.DocumentId, s.Doc.TableDefId, c[1]).ConfigureAwait(true);
        var window = await AddWindowMapAsync(s.Doc.TableDefId, c[1], c[0], c[2]).ConfigureAwait(true);

        // Мапінг ЧУЖОГО документа (інший проєкт), що адресує ту саму таблицю: перенос його не бачить.
        var other = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(202601, columnCount: 1, rowCount: 1).ConfigureAwait(true);
        var foreign = await AddEventMapAsync(other.DocumentId, s.Doc.TableDefId, c[1]).ConfigureAwait(true);

        // Провенанс підтягування, що посилається на колонку-ціль вікна.
        var unitId = await UnitIdAsync().ConfigureAwait(true);
        int valueId;
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var entityId = await AddSourceEntityAsync().ConfigureAwait(true);
            var value = new RowWindowValue(
                s.Doc.PeriodKey.Value, s.Doc.TableInstanceId, "R1", c[1], window, entityId, "Attr",
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                RowWindowSummaryKind.Total, unitId, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
            db.RowWindowValues.Add(value);
            await db.SaveChangesAsync().ConfigureAwait(true);
            valueId = (int)value.Id;
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = await ReadJsonAsync(await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.True(dry.GetProperty("canApply").GetBoolean(), dry.ToString());
        Assert.Equal(2, dry.GetProperty("mappingsRemapped").GetInt32());
        Assert.Equal(0, dry.GetProperty("mappingsUnmapped").GetInt32());

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        Assert.Equal(2, JsonDocument.Parse(body).RootElement.GetProperty("mappingsRemapped").GetInt32());

        // ⛔ Предмет тесту: таблиця й колонки — цільової версії (за кодами), а не лишились на старій.
        Assert.Equal((s.TargetTableDefId, s.TargetColumns["C2"]), await EventMapStateAsync(events.MapId, events.FieldId).ConfigureAwait(true));
        Assert.Equal(
            (s.TargetTableDefId, s.TargetColumns["C2"], s.TargetColumns["C1"], s.TargetColumns["C3"]),
            await WindowMapStateAsync(window).ConfigureAwait(true));

        // Чужий мапінг лишився на таблиці вихідної версії.
        Assert.Equal((s.Doc.TableDefId, c[1]), await EventMapStateAsync(foreign.MapId, foreign.FieldId).ConfigureAwait(true));

        // Провенанс переїхав на колонку цільової версії разом із коміркою.
        await using var check = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var movedColumn = await check.RowWindowValues.AsNoTracking()
            .Where(v => v.PeriodKey == s.Doc.PeriodKey.Value && v.Id == valueId)
            .Select(v => v.ColumnDefId).SingleAsync().ConfigureAwait(true);
        Assert.Equal(s.TargetColumns["C2"], movedColumn);
    }

    private async Task<int> UnitIdAsync()
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return await db.Database.SqlQuery<int>($"SELECT TOP (1) Id AS Value FROM uom.Unit ORDER BY Id")
            .SingleAsync().ConfigureAwait(false);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-1b")]
    public async Task Активні_мапінги_подій_і_вікна_без_відповідника_блокують_перенос_422_з_ключами_і_нічого_не_змінюють()
    {
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var c = s.Doc.ColumnDefIds;
        var events = await AddEventMapAsync(s.Doc.DocumentId, s.Doc.TableDefId, c[2]).ConfigureAwait(true);   // C3 зникає
        var window = await AddWindowMapAsync(s.Doc.TableDefId, c[1], c[0], c[2]).ConfigureAwait(true);        // кінець вікна C3
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = await ReadJsonAsync(await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.False(dry.GetProperty("canApply").GetBoolean());
        Assert.Contains("mappingsNotMapped", dry.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(2, dry.GetProperty("mappingsUnmapped").GetInt32());
        var keys = dry.GetProperty("mappingsUnmappedKeys").EnumerateArray().Select(k => k.GetString()!).ToList();
        Assert.Contains(keys, k => k.StartsWith("events ", StringComparison.Ordinal));
        Assert.Contains(keys, k => k.StartsWith("window ", StringComparison.Ordinal));

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        await AssertProblemAsync(
            response, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateMappingsNotMapped")
            .ConfigureAwait(true);

        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
        Assert.Equal((s.Doc.TableDefId, c[2]), await EventMapStateAsync(events.MapId, events.FieldId).ConfigureAwait(true));
        Assert.Equal(
            (s.Doc.TableDefId, c[1], c[0], c[2]), await WindowMapStateAsync(window).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-1b")]
    public async Task Призупинені_мапінги_подій_і_вікна_без_відповідника_перенос_не_блокують_і_лишаються()
    {
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var c = s.Doc.ColumnDefIds;
        var events = await AddEventMapAsync(s.Doc.DocumentId, s.Doc.TableDefId, c[2], active: false).ConfigureAwait(true);
        var window = await AddWindowMapAsync(s.Doc.TableDefId, c[1], c[0], c[2], active: false).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync().ConfigureAwait(true));

        Assert.Equal((s.Doc.TableDefId, c[2]), await EventMapStateAsync(events.MapId, events.FieldId).ConfigureAwait(true));
        Assert.Equal((s.Doc.TableDefId, c[1], c[0], c[2]), await WindowMapStateAsync(window).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-1b")]
    public async Task Вікно_на_колонку_цілі_яку_вже_зайняла_інша_прив_язка_цільової_версії_блокує_перенос_а_не_падає_на_UQ()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        var c = s.Doc.ColumnDefIds;
        var mine = await AddWindowMapAsync(s.Doc.TableDefId, c[1], c[0], c[2]).ConfigureAwait(true);
        var taken = await AddWindowMapAsync(
            s.TargetTableDefId, s.TargetColumns["C2"], s.TargetColumns["C1"], s.TargetColumns["C3"]).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = await ReadJsonAsync(await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.False(dry.GetProperty("canApply").GetBoolean());
        Assert.Contains("mappingsNotMapped", dry.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        await AssertProblemAsync(
            response, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateMappingsNotMapped")
            .ConfigureAwait(true);

        Assert.Equal((s.Doc.TableDefId, c[1], c[0], c[2]), await WindowMapStateAsync(mine).ConfigureAwait(true));
        Assert.Equal(
            (s.TargetTableDefId, s.TargetColumns["C2"], s.TargetColumns["C1"], s.TargetColumns["C3"]),
            await WindowMapStateAsync(taken).ConfigureAwait(true));
    }
}
