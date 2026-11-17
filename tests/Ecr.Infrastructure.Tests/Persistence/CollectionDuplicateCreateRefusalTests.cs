using Ecr.Application.Errors;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Notifications;
using Ecr.Domain.Entities.Units;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// B158 (лінія c): програш гонки двох однакових запитів створення — та сама відмова, що й послідовна перевірка
/// обробника, а не сирий <c>DbUpdateException</c> (500). Переможець зберігає першим, переможений — той самий ключ з
/// іншого контексту, повз перевірку (саме так його бачить база, коли обидва пройшли «чи вже є?»).
/// </summary>
/// <remarks>
/// Мутації: прибрати арм у <c>DuplicateCreateRefusals</c> (c1, c2, c3, c5) або локальний catch у
/// <c>CollectionStore.AddSourceEntityAsync</c> (c4), <c>SourceEventMapStore.AddMapAsync</c> (c6),
/// <c>RowWindowMapStore.AddMapAsync</c> (c7) — відповідний тест червоний (сирий виняток бази).
/// </remarks>
[Collection("SqlServer")]
public sealed class CollectionDuplicateCreateRefusalTests(SqlServerFixture sql)
{
    private static readonly TestClock Clock = new(new DateTime(2099, 1, 5, 9, 0, 0, DateTimeKind.Utc));

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task c1_Дубль_коду_одиниці_дає_загальний_409_а_не_сирий_виняток_бази()
    {
        // Ключ переможця ({id}) база не каже: арм дає загальний 409, повну відмову будує CreateUnitHandler (HTTP-тест c1).
        var code = $"b158u{Guid.NewGuid():N}"[..14];
        var thrown = await LoseRaceAsync<ConcurrencyConflictException>(db => db.Units.Add(NewUnit(code)));

        Assert.Equal("ECR-CELL-0409", thrown.ErrorCode);
        Assert.Equal("err.ECR-CELL-0409.concurrentChange", thrown.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task c2_Дубль_назви_каналу_дає_notificationChannelNameTaken_а_не_сирий_виняток_бази()
    {
        var name = $"b158c-{Guid.NewGuid():N}";
        var thrown = await LoseRaceAsync<BusinessRuleException>(db => db.NotificationChannels.Add(
            new NotificationChannel(NotificationChannelKind.TeamsWebhook, name, "{}", Clock.UtcNow, null)));

        Assert.Equal("ECR-REQ-0422", thrown.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.notificationChannelNameTaken", thrown.Details!["messageKey"]);
        Assert.Equal(name, thrown.Details["name"]);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task c3_Дубль_коду_джерела_даних_дає_dataSourceCodeTaken_а_не_сирий_виняток_бази()
    {
        var code = $"B158DS{Guid.NewGuid():N}"[..14];
        var thrown = await LoseRaceAsync<BusinessRuleException>(db => db.DataSources.Add(NewDataSource(code)));

        Assert.Equal("ECR-REQ-0422", thrown.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.dataSourceCodeTaken", thrown.Details!["messageKey"]);
        Assert.Equal(code, thrown.Details["code"]);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task c5_Другий_розклад_сутності_дає_загальний_409_а_не_сирий_виняток_бази()
    {
        // Ключ переможця ({scheduleId}) база не каже: повну відмову будує CreateCollectionScheduleHandler (HTTP-тест c5).
        var (_, entityId) = await AddSourceEntityAsync();
        var thrown = await LoseRaceAsync<ConcurrencyConflictException>(
            db => db.CollectionSchedules.Add(new CollectionSchedule(entityId, "0 0 3 1 1 ? 2099")));

        Assert.Equal("ECR-CELL-0409", thrown.ErrorCode);
        Assert.Equal("err.ECR-CELL-0409.concurrentChange", thrown.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task c4_Дубль_коду_сутності_у_сховищі_дає_sourceEntityDuplicate_а_не_сирий_виняток_бази()
    {
        var (dataSourceId, dataSourceCode) = await AddDataSourceAsync();
        var code = $"Ent{Guid.NewGuid():N}"[..14];

        await using (var winner = Context())
        {
            await new CollectionStore(winner, Clock).AddSourceEntityAsync(Inactive(dataSourceId, code), CancellationToken.None);
        }

        await using var loser = Context();
        var thrown = await Assert.ThrowsAsync<BusinessRuleException>(
            () => new CollectionStore(loser, Clock).AddSourceEntityAsync(Inactive(dataSourceId, code), CancellationToken.None));

        Assert.Equal("ECR-INT-0409", thrown.ErrorCode);
        Assert.Equal("err.ECR-INT-0409.sourceEntityDuplicate", thrown.Details!["messageKey"]);
        Assert.Equal(code, thrown.Details["code"]);
        Assert.Equal(dataSourceCode, thrown.Details["dataSource"]);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task c6_Дубль_мапінгу_подій_у_сховищі_дає_eventMapExists_а_не_сирий_виняток_бази()
    {
        var stand = await ArrangeTableAsync();
        var (_, entityId) = await AddSourceEntityAsync();

        async Task<SourceEventMap> NewMapAsync(EcrDbContext db)
        {
            var table = await db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == stand.TableDefId);
            return SourceEventMap.Create(
                entityId,
                stand.DocumentId,
                table,
                [new(stand.Start, SourceEventMap.StartAttribute), new(stand.End, SourceEventMap.EndAttribute)],
                SourceEventVolumeMode.None);
        }

        await using (var winner = Context())
        {
            await new SourceEventMapStore(winner).AddMapAsync(await NewMapAsync(winner), CancellationToken.None);
        }

        await using var loser = Context();
        var map = await NewMapAsync(loser);
        var thrown = await Assert.ThrowsAsync<BusinessRuleException>(
            () => new SourceEventMapStore(loser).AddMapAsync(map, CancellationToken.None));

        Assert.Equal("ECR-INT-0409", thrown.ErrorCode);
        Assert.Equal("err.ECR-INT-0409.eventMapExists", thrown.Details!["messageKey"]);
        Assert.Equal(entityId.ToString(System.Globalization.CultureInfo.InvariantCulture), thrown.Details["sourceEntityId"]);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task c7_Дубль_цілі_вікна_рядка_у_сховищі_дає_rowWindowTargetTaken_а_не_сирий_виняток_бази()
    {
        var stand = await ArrangeTableAsync();

        RowWindowMap NewMap()
            => RowWindowMap.Create(stand.Target, stand.Start, stand.End, null, RowWindowSummaryKind.Total, false, stand.UnitId);

        await using (var winner = Context())
        {
            await new RowWindowMapStore(winner).AddMapAsync(NewMap(), CancellationToken.None);
        }

        await using var loser = Context();
        var thrown = await Assert.ThrowsAsync<BusinessRuleException>(
            () => new RowWindowMapStore(loser).AddMapAsync(NewMap(), CancellationToken.None));

        Assert.Equal("ECR-INT-0409", thrown.ErrorCode);
        Assert.Equal("err.ECR-INT-0409.rowWindowTargetTaken", thrown.Details!["messageKey"]);
        Assert.Equal(stand.Target.Code, thrown.Details["targetColumn"]);

        await using var cleanup = Context();
        await cleanup.Database.ExecuteSqlAsync($"DELETE FROM ext.RowWindowMap WHERE TableDefId = {stand.TableDefId}");
    }

    // ───────────────────────────── стенд ─────────────────────────────

    /// <summary>Переможець зберігає через одиницю роботи першим; переможений — те саме з іншого контексту.</summary>
    private async Task<TException> LoseRaceAsync<TException>(Action<EcrDbContext> add)
        where TException : Exception
    {
        await using (var winner = Context())
        {
            add(winner);
            await new UnitOfWork(winner).SaveChangesAsync(CancellationToken.None);
        }

        await using var loser = Context();
        add(loser);

        return await Assert.ThrowsAsync<TException>(() => new UnitOfWork(loser).SaveChangesAsync(CancellationToken.None));
    }

    private sealed record TableStand(int TableDefId, long DocumentId, ColumnDef Start, ColumnDef End, ColumnDef Target, int UnitId);

    private async Task<TableStand> ArrangeTableAsync()
    {
        var chain = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(
            periodKey: 202601, columnCount: 1, rowCount: 0, rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);

        await using var db = Context();
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var start = new ColumnDef(chain.TableDefId, EcrCode.Create($"DQS_{tag}"), Name("Start"), 20, CellDataType.Date);
        var end = new ColumnDef(chain.TableDefId, EcrCode.Create($"DQE_{tag}"), Name("End"), 21, CellDataType.Date);
        var target = new ColumnDef(chain.TableDefId, EcrCode.Create($"DQT_{tag}"), Name("Volume"), 22, CellDataType.Decimal);
        db.ColumnDefs.AddRange(start, end, target);

        var dimensionId = await db.Dimensions.OrderBy(d => d.Id).Select(d => d.Id).FirstAsync();
        var unit = new Unit(EcrCode.Create($"DQU{tag}"), Name("u"), Name("u"), dimensionId, isBase: false, factorToBase: 1m, offsetToBase: 0m);
        db.Units.Add(unit);
        await db.SaveChangesAsync();

        return new TableStand(chain.TableDefId, chain.DocumentId, start, end, target, unit.Id);
    }

    private async Task<(int Id, string Code)> AddDataSourceAsync()
    {
        await using var db = Context();
        var source = NewDataSource($"B158DS{Guid.NewGuid():N}"[..14]);
        source.Deactivate();
        db.DataSources.Add(source);
        await db.SaveChangesAsync();

        return (source.Id, source.Code);
    }

    private async Task<(int DataSourceId, int EntityId)> AddSourceEntityAsync()
    {
        var (dataSourceId, _) = await AddDataSourceAsync();

        await using var db = Context();
        var entity = Inactive(dataSourceId, $"Ent{Guid.NewGuid():N}"[..14]);
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync();

        return (dataSourceId, entity.Id);
    }

    /// <summary>Неактивна: база спільна, а активна сутність без збору фарбувала б <c>SourcesHealthCheck</c>.</summary>
    private static SourceEntity Inactive(int dataSourceId, string code)
    {
        var entity = new SourceEntity(dataSourceId, code, RegistrySourceKind.External);
        entity.Deactivate();
        return entity;
    }

    private static DataSource NewDataSource(string code)
        => new(EcrCode.Create(code), Name("B158c"), ExternalTransport.PiWebApi, "https://example.test", "secret");

    private static Unit NewUnit(string code)
        => new(EcrCode.Create(code), Name(code), Name(code), 1, isBase: false, factorToBase: 2.5m, offsetToBase: 0m);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);
}
