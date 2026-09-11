// tests/Ecr.Infrastructure.Tests/Persistence/CollectionStoreUnitResolutionTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Units;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>UpsertRawPointsAsync</c> шукає одиницю джерела (<c>uom.Unit</c>) одним
/// пакетним запитом на весь батч, а не по запиту на точку (<c>Q-248</c>).
/// </summary>
/// <remarks>
/// ⛔ До фіксу метод ходив у <c>uom.Unit</c> ВСЕРЕДИНІ <c>foreach</c> по
/// точках — тисячі точок означали тисячі round-trip, попри те, що
/// РІЗНИХ символів одиниць у батчі зазвичай лічені одиниці (усі точки
/// одного джерела — зазвичай в одній одиниці). Той самий клас дефекту,
/// що й перевірка наявних точок кількома рядками вище в тому самому
/// методі (яка вже й до цього фіксу була пакетною) — і той самий доказ:
/// рахунок РЕАЛЬНИХ SQL-команд, а не читання коду.
/// </remarks>
[Collection("SqlServer")]
public sealed class CollectionStoreUnitResolutionTests(SqlServerFixture sql)
{
    /// <summary>Суфікс кодів — свій на кожен тест: база спільна на всю збірку.</summary>
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Батч_із_лічених_одиниць_коштує_одного_запиту_до_uom_Unit_незалежно_від_кількості_точок()
    {
        const int PointCount = 50;
        const int DistinctUnits = 3;

        int sourceEntityId;
        long collectionRunId;
        var unitCodes = new string[DistinctUnits];

        await using (var setup = CreateContext())
        {
            var dataSource = new DataSource(
                EcrCode.Create($"Src{_tag}"), Name("Джерело"), ExternalTransport.PiWebApi,
                "https://example.test", "secret");
            setup.DataSources.Add(dataSource);
            await setup.SaveChangesAsync(CancellationToken.None);

            var entity = new SourceEntity(dataSource.Id, $"Ent{_tag}", RegistrySourceKind.External);
            setup.SourceEntities.Add(entity);

            var dimension = await setup.Dimensions.OrderBy(d => d.Id).FirstAsync();

            for (var i = 0; i < DistinctUnits; i++)
            {
                var code = $"U{i}{_tag}";
                unitCodes[i] = code;
                setup.Units.Add(new Unit(
                    EcrCode.Create(code), Name($"symbol{i}"), Name($"unit{i}"), dimension.Id,
                    isBase: false, factorToBase: 1m, offsetToBase: 0m));
            }

            await setup.SaveChangesAsync(CancellationToken.None);
            sourceEntityId = entity.Id;

            var store = new CollectionStore(setup, new TestClock(DateTime.UtcNow));
            collectionRunId = await store.StartRunAsync(
                sourceEntityId,
                DateTime.UtcNow.AddHours(-1),
                DateTime.UtcNow,
                isCatchUp: false,
                triggeredByUserId: null,
                CancellationToken.None);
        }

        // П'ятдесят точок, лише ТРИ РІЗНІ символи одиниць — типова картина
        // одного джерела з симптому картки: тисячі точок, лічені одиниці.
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var points = new List<SourceDataPoint>();
        for (var i = 0; i < PointCount; i++)
        {
            points.Add(new SourceDataPoint(
                SourcePath: $"P{i}",
                Timestamp: baseTime.AddMinutes(i),
                ValueNumeric: i,
                ValueString: null,
                SourceUnitSymbol: unitCodes[i % DistinctUnits],
                Quality: "Good"));
        }

        var executed = new List<string>();
        await using var counting = CreateCountingContext(executed);
        var upsertStore = new CollectionStore(counting, new TestClock(DateTime.UtcNow));

        var written = await upsertStore.UpsertRawPointsAsync(
            collectionRunId, sourceEntityId, points, CancellationToken.None);

        Assert.Equal(PointCount, written);

        // ⛔ Прямий доказ фіксу: РІВНО один SELECT до uom.Unit на весь
        // батч — до фіксу тут було б PointCount (50) запитів, по одному на
        // точку. Мутаційний доказ (RED до фіксу, GREEN після) — у PR/Q-248.
        var unitQueries = executed.Count(cmd =>
            cmd.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
            && cmd.Contains("[uom].[Unit]", StringComparison.Ordinal));
        Assert.Equal(1, unitQueries);

        await using var verify = CreateContext();
        var storedCount = await verify.RawDataPoints
            .Where(p => p.SourceEntityId == sourceEntityId)
            .CountAsync();
        Assert.Equal(PointCount, storedCount);
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    /// <summary>Контекст, який складає кожну виконану команду в список.</summary>
    private EcrDbContext CreateCountingContext(List<string> executed)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .LogTo(executed.Add, [RelationalEventId.CommandExecuted])
            .Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
