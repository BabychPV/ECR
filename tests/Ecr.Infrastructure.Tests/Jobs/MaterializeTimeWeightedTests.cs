// tests/Ecr.Infrastructure.Tests/Jobs/MaterializeTimeWeightedTests.cs
using Ecr.Application;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Sources;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Матеріалізація зі згортками за часом і конверсією одиниць на межі
/// (HSE301 F3, §4.1–4.2, <c>D-173</c>, ФВ-16.10).
/// </summary>
/// <remarks>
/// ⚠ Усе справжнє й зібране контейнером, як у проді: <c>IntegrationCellPatcher</c>
/// поверх <c>PatchCellsHandler</c>, автор <c>svc-integration</c>, довідник
/// одиниць із сіду (F1: <c>Sm3</c>, <c>Sm3_per_h</c>). Патчер лише обгорнуто
/// записувачем: точність перевіряється на значенні, яке задача ВІДДАЛА на
/// запис, — колонка з меншим масштабом округлила б хвіст <c>…000744</c>, і
/// тест по базі його не побачив би.
/// </remarks>
[Collection("SqlServer")]
public sealed class MaterializeTimeWeightedTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MidJanuary = new(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-16.10")]
    public async Task Місячний_Total_Sm3_per_h_лягає_в_колонку_Sm3_рівно()
    {
        // Постійні 3.6 Sm3/h; точки лише ПОЗА січнем і одна всередині — значення
        // на межах періоду інтерполюються з них (§4.1). Січень у поясі проєкту
        // (Asia/Atyrau, без переходу на літній час) — 744 год: 3.6 × 744 = 2 678.4 Sm3.
        var stand = await ArrangeAsync(
            new Field("INT", AggregationKind.TimeIntegral, "Sm3_per_h", "Sm3",
            [
                new(new DateTime(2025, 12, 25, 0, 0, 0, DateTimeKind.Utc), 3.6m),
                new(MidJanuary, 3.6m),
                new(new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc), 3.6m),
            ]));

        var written = await RunAsync(stand);

        // ⛔ МУТАЦІЙНІ ДОКАЗИ:
        // • прибрати виклик `BoundaryUnitConversion` з задачі → у комірці
        //   9 642 240 («Sm3/h × с»), червоний;
        // • перерахунок через `FactorToBase` швидкості замість знаменника →
        //   2678.4000000000002143, червоний;
        // • без межових точок до/після періоду → покрито лише середину січня,
        //   значення менше, червоний.
        var cell = Assert.Single(written);
        Assert.Equal(2678.4m, cell.Value);
        Assert.Equal(2678.4m, Assert.IsType<decimal>(await CellAsync(stand, stand.Columns[0])));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-16.10")]
    public async Task Середнє_за_часом_у_матеріалізації_7_5_а_не_просте()
    {
        // Ряд F2: [0 @0 с, 10 @10 с, 10 @20 с] — зважене 7.5, просте 6.67.
        var stand = await ArrangeAsync(
            new Field("TWA", AggregationKind.TimeWeightedAvg, null, null,
            [
                new(MidJanuary, 0m),
                new(MidJanuary.AddSeconds(10), 10m),
                new(MidJanuary.AddSeconds(20), 10m),
            ]));

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: повернути в задачу стару `Fold(kind, decimals)` без
        // часу → для `TimeWeightedAvg` вона відмовляє, задача падає, червоний.
        var written = await RunAsync(stand);

        Assert.Equal(7.5m, Assert.Single(written).Value);
        Assert.Equal(7.5m, Assert.IsType<decimal>(await CellAsync(stand, stand.Columns[0])));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-16.12")]
    public async Task Avg_старого_мапінгу_побітно_той_самий()
    {
        // Точка ДО періоду є, але згортка точок її не бачить — як і до F3.
        var stand = await ArrangeAsync(
            new Field("AVG", AggregationKind.Avg, null, null,
            [
                new(new DateTime(2025, 12, 25, 0, 0, 0, DateTimeKind.Utc), 1000m),
                new(MidJanuary, 3m),
                new(MidJanuary.AddHours(1), 4m),
                new(MidJanuary.AddHours(2), 8m),
            ]));

        var written = await RunAsync(stand);

        // Еталон — рівно те, що рахувала задача до F3: числа точок періоду так,
        // як їх віддає база (`decimal(34,16)`, тобто з масштабом 16), і стара
        // `Fold(kind, decimals)`.
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var series = await db.RawDataPoints
            .Where(p => p.SourceEntityId == stand.EntityId && p.Timestamp >= new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                        && p.ValueNumeric != null)
            .OrderBy(p => p.Timestamp)
            .Take(10)
            .Select(p => p.ValueNumeric!.Value)
            .ToListAsync();
        var before = PeriodFold.Fold(AggregationKind.Avg, series);

        Assert.Equal(3, series.Count);
        Assert.Equal(decimal.GetBits(before), decimal.GetBits(Assert.Single(written).Value));
        Assert.Equal(5m, Assert.IsType<decimal>(await CellAsync(stand, stand.Columns[0])));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-16.10")]
    public async Task Несумісні_одиниці_Sm3_в_m3_не_пишуть_тихого_числа_а_решта_полів_записана()
    {
        var stand = await ArrangeAsync(
            new Field("OK", AggregationKind.Avg, null, null, [new(MidJanuary, 5m)]),
            new Field("BAD", AggregationKind.Avg, "Sm3", "m3", [new(MidJanuary, 7m)]));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => RunAsync(stand));

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: ковтати помилку конверсії й писати значення як є →
        // у колонці m3 лежить 7 «Sm3», винятку немає — червоний.
        Assert.Equal("ECR-UOM-0422", error.ErrorCode);
        Assert.Equal("err.ECR-UOM-0422.boundaryConversionFailed", error.Details!["messageKey"]);
        Assert.Contains("BAD", error.Message, StringComparison.Ordinal);

        Assert.Equal(5m, Assert.IsType<decimal>(await CellAsync(stand, stand.Columns[0])));
        Assert.Null(await CellAsync(stand, stand.Columns[1]));
    }

    /// <summary>Поле джерела з мапінгом і точками.</summary>
    private sealed record Field(
        string Name, AggregationKind Kind, string? SourceUnit, string? TargetUnit, IReadOnlyList<Point> Points);

    /// <summary>Точка поля.</summary>
    private sealed record Point(DateTime At, decimal Value);

    /// <summary>Усе, що заведено для одного прогону.</summary>
    private sealed record Stand(TestDocument Chain, int EntityId, string RowKey, IReadOnlyList<int> Columns);

    /// <summary>
    /// Ланцюг із відкритим січнем і сутність, чиї поля лягають у рядок 1 —
    /// кожне у свою числову колонку.
    /// </summary>
    private async Task<Stand> ArrangeAsync(params Field[] fields)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(columnCount: fields.Length + 1, ct: CancellationToken.None);

        await using var db = builder.CreateContext();

        var period = await db.Periods.SingleAsync(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == chain.PeriodKey.Value);
        period.TransitionTo(PeriodState.Open, Now);
        await db.SaveChangesAsync(CancellationToken.None);

        var rowKey = (await db.TableRows
            .Where(r => r.PeriodKeyValue == chain.PeriodKey.Value && r.Id == chain.RowIds[0])
            .Select(r => r.RowKey)
            .SingleAsync()).Value;

        var tag = Guid.NewGuid().ToString("N")[..8];
        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"), Name("HSE301 F3"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        // ⛔ Неактивна навмисно (як у `MaterializeMappingScopeTests`): активна
        // сутність без завершеного збору робить `SourcesHealthCheck` жовтим.
        var entity = new SourceEntity(dataSource.Id, $"Ent{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        var columns = new List<int>();
        var points = new List<SourceDataPoint>();
        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            var path = $"{field.Name}_{tag}";
            var columnId = chain.ColumnDefIds[i + 1];
            columns.Add(columnId);

            var map = EntityFieldMap.ToColumn(entity.Id, path, columnId);
            map.SetMaterialization(rowKey, field.Kind);
            map.SetUnits(await UnitIdAsync(db, field.SourceUnit), await UnitIdAsync(db, field.TargetUnit));
            db.EntityFieldMaps.Add(map);

            points.AddRange(field.Points.Select(p => new SourceDataPoint(path, p.At, p.Value, null, null, "Good")));
        }

        await db.SaveChangesAsync(CancellationToken.None);

        var store = new CollectionStore(db, new TestClock(Now));
        var runId = await store.StartRunAsync(
            entity.Id, MidJanuary, MidJanuary.AddHours(2), isCatchUp: false, triggeredByUserId: null, CancellationToken.None);
        await store.UpsertRawPointsAsync(runId, entity.Id, points, CancellationToken.None);

        return new Stand(chain, entity.Id, rowKey, columns);
    }

    private static async Task<int?> UnitIdAsync(EcrDbContext db, string? code)
        => code is null ? null : await db.Units.Where(u => u.Code == code).Select(u => (int?)u.Id).SingleAsync();

    /// <summary>
    /// Прогін задачі в scope контейнера: справжній патчер, обгорнутий записувачем.
    /// </summary>
    /// <returns>Значення, які задача віддала на запис.</returns>
    private async Task<IReadOnlyList<IntegrationCellValue>> RunAsync(Stand stand)
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var recorder = new RecordingPatcher(sp.GetRequiredService<ICellPatcher>());
        var job = new MaterializeCollectedDataJob(
            sp.GetRequiredService<EcrDbContext>(),
            recorder,
            sp.GetRequiredService<ICoverageJournal>(),
            sp.GetRequiredService<IntegrationActor>());

        await job.ExecuteAsync(
            new MaterializeTask(
                stand.EntityId, stand.Chain.ProjectId, stand.Chain.DocumentId, stand.Chain.TableInstanceId,
                stand.Chain.PeriodKey.Value,
                new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 16, 0, 0, 0, DateTimeKind.Utc)),
            Substitute.For<IJobProgress>(),
            CancellationToken.None);

        return recorder.Cells;
    }

    /// <summary>Контейнер як у проді: застосунок + інфраструктура + обгортка автора задачі.</summary>
    private ServiceProvider BuildProvider()
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration[Arg.Any<string>()].Returns((string?)null);
        var connectionStrings = Substitute.For<IConfigurationSection>();
        connectionStrings["Ecr"].Returns(sql.ConnectionString);
        configuration.GetSection("ConnectionStrings").Returns(connectionStrings);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEcrApplication();
        services.AddEcrInfrastructure(configuration);

        services.AddScoped<JobActorScope>();
        services.AddScoped<ICurrentUser>(sp => new JobAwareCurrentUser(
            new NoHttpRequestUser(), sp.GetRequiredService<JobActorScope>()));

        // ⚠ Черга перерахунку — підробка (як у `MaterializeIntegrationActorTests`):
        // Quartz тримає планувальник у глобальному репозиторії процесу.
        services.AddScoped(_ => Substitute.For<IBackgroundJobScheduler>());

        return services.BuildServiceProvider();
    }

    private async Task<object?> CellAsync(Stand stand, int columnDefId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT v.ValueNumeric FROM doc.CellValue v JOIN doc.TableRow r ON r.PeriodKey = v.PeriodKey AND r.Id = v.TableRowId "
            + "WHERE r.TableInstanceId = @instance AND r.RowKey = @rowKey AND v.ColumnDefId = @column";
        command.Parameters.AddWithValue("@instance", stand.Chain.TableInstanceId);
        command.Parameters.AddWithValue("@rowKey", stand.RowKey);
        command.Parameters.AddWithValue("@column", columnDefId);
        var result = await command.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Справжній патчер, що запам'ятовує віддані на запис значення.</summary>
    private sealed class RecordingPatcher(ICellPatcher inner) : ICellPatcher
    {
        public List<IntegrationCellValue> Cells { get; } = [];

        public Task<IntegrationWriteResult> ApplyIntegrationAsync(
            long documentId, long tableInstanceId, PeriodKey periodKey,
            IReadOnlyList<IntegrationCellValue> cells, CancellationToken ct)
        {
            Cells.AddRange(cells);
            return inner.ApplyIntegrationAsync(documentId, tableInstanceId, periodKey, cells, ct);
        }
    }

    /// <summary><c>Ecr.Api.Auth.CurrentUser</c> поза HTTP-запитом: анонімний.</summary>
    private sealed class NoHttpRequestUser : ICurrentUser
    {
        public int? UserId => null;

        public string? UserName => null;

        public string CorrelationId
            => throw new InvalidOperationException("ICurrentUser використано поза запитом: HttpContext немає.");

        public string Language => "en";

        public IReadOnlyList<string> GroupSids => [];
    }
}
