// tests/Ecr.Infrastructure.Tests/Persistence/CollectionStoreSourceUnitTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Units;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Сира точка лягає в <c>ext.RawDataPoint</c> В ОДИНИЦІ ДЖЕРЕЛА
/// (<c>ФВ-11.7</c>, <c>ФВ-16.10</c>).
/// </summary>
/// <remarks>
/// ⛔ Мапінг оголошує ЦІЛЬОВУ одиницю, відмінну від одиниці джерела (т → кг).
/// Якби запис конвертував на вході, у базі лежали б 2500 кг замість 2.5 т — і
/// повторний перерахунок з архіву після зміни мапінгу дав би інше число.
/// </remarks>
[Collection("SqlServer")]
public sealed class CollectionStoreSourceUnitTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-11.7")]
    public async Task Сира_точка_зберігає_значення_й_одиницю_джерела_а_не_цільову()
    {
        await using var db = Context();

        var dimension = await db.Dimensions.OrderBy(d => d.Id).FirstAsync();
        var tonne = new Unit(
            EcrCode.Create($"t{_tag}"), Name("t"), Name("tonne"), dimension.Id,
            isBase: false, factorToBase: 1000m, offsetToBase: 0m);
        var kilogram = new Unit(
            EcrCode.Create($"kg{_tag}"), Name("kg"), Name("kilogram"), dimension.Id,
            isBase: false, factorToBase: 1m, offsetToBase: 0m);
        db.Units.AddRange(tonne, kilogram);

        var dataSource = new DataSource(
            EcrCode.Create($"Src{_tag}"), Name("FV-11.7"), ExternalTransport.PiWebApi,
            "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync();

        // ⚠ Неактивна: активна сутність без завершеного збору лишилась би в
        // спільній базі й пожовтила б `SourcesHealthCheck` наступним прогонам.
        var entity = new SourceEntity(dataSource.Id, $"Ent{_tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync();

        var map = EntityFieldMap.ToColumn(entity.Id, "Flow", columnDefId: 1);
        map.SetUnits(tonne.Id, kilogram.Id);

        var at = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var store = new CollectionStore(db, new TestClock(at));
        var runId = await store.StartRunAsync(entity.Id, at, at.AddHours(1), false, null, CancellationToken.None);

        await store.UpsertRawPointsAsync(
            runId,
            entity.Id,
            [new SourceDataPoint("Flow", at, 2.5m, null, tonne.Code, "Good")],
            CancellationToken.None);

        await using var read = Context();
        var stored = await read.RawDataPoints.AsNoTracking().SingleAsync(p => p.SourceEntityId == entity.Id);

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: у `CollectionStore.UpsertRawPointsAsync`
        // записати одиницю, відмінну від одиниці точки (напр. `null` замість
        // розпізнаної), → червоніє друге твердження; помножити значення на
        // коефіцієнт → перше.
        Assert.Equal(2.5m, stored.ValueNumeric);
        Assert.Equal(tonne.Id, stored.UnitId);
        Assert.NotEqual(map.TargetUnitId, stored.UnitId);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
