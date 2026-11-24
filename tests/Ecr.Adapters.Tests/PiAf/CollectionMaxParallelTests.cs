using Ecr.Adapters.PiAf;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.PiAf;

/// <summary>
/// Поле з'єднання «Макс. паралельність» (<c>ext.DataSource.MaxParallel</c>) — стеля одночасних читань прогону
/// ЦЬОГО джерела, для кожного транспорту.
/// </summary>
/// <remarks>
/// ⛔ Дефект: значення перевірялося (1..32) і зберігалося, але збирач будувався без нього й завжди тримав 4
/// читання в мережі. ⛔ МУТАЦІЯ (доведено вручну): <c>ParallelReadsOf</c> повертає
/// <c>DefaultMaxParallelReads</c>, ігноруючи джерело, — тест «2» бачить 4 одночасні читання, тест «6» — 4.
/// ⚠ Тест на конкурентність: прогнано 20/20 поспіль перед пушем.
/// </remarks>
public sealed class CollectionMaxParallelTests
{
    private const int SourceEntityId = 42;

    private static readonly DateTime Origin = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Пакетний_адаптер_PiWebApi_тримає_в_мережі_рівно_MaxParallel_джерела(int maxParallel)
    {
        var end = Origin.AddDays(20);
        var source = new SyntheticBatchPi(3, Origin, TimeSpan.FromHours(1), end)
        {
            Transport = ExternalTransport.PiWebApi,
            Latency = TimeSpan.FromMilliseconds(40),
        };

        var summary = await Run(source, end, maxParallel);

        Assert.False(summary.Degraded);
        Assert.Equal(20, source.Calls);

        // Не більше стелі — і саме стеля: читання наперед стартує її одразу (поточний шматок + MaxParallel − 1).
        Assert.Equal(maxParallel, source.MaxInFlight);
    }

    [Theory]
    [InlineData(ExternalTransport.PiSqlClient)]
    [InlineData(ExternalTransport.Sql)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Послідовні_адаптери_Sql_і_PiSqlClient_не_перевищують_MaxParallel(ExternalTransport transport)
    {
        var end = Origin.AddDays(5);
        var source = new SyntheticPi(3, Origin, TimeSpan.FromHours(1), end)
        {
            Transport = transport,
            Latency = TimeSpan.FromMilliseconds(5),
        };

        var summary = await Run(source, end, maxParallel: 2);

        Assert.False(summary.Degraded);
        Assert.Equal(5 * 3, source.Calls);
        Assert.Equal(1, source.MaxInFlight);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Джерело_без_стелі_читається_типовими_чотирма()
    {
        var source = DataSourceOf(ExternalTransport.PiWebApi, maxParallel: null);

        Assert.Equal(4, source.MaxParallel);
        Assert.Equal(CollectionRunner.DefaultMaxParallelReads, CollectionRunner.ParallelReadsOf(source));
    }

    private static DataSource DataSourceOf(ExternalTransport transport, int? maxParallel)
    {
        var source = new DataSource(
            EcrCode.Create("PIAF"),
            new LocalizedText(new Dictionary<string, string> { ["uk"] = "PI AF" }),
            transport,
            "pi.example",
            "PiAf.Primary");

        if (maxParallel is { } value)
        {
            source.Configure(null, null, value);
        }

        return source;
    }

    private static async Task<CollectionRunSummary> Run(SyntheticPi source, DateTime end, int maxParallel)
    {
        var store = Substitute.For<ICollectionStore>();
        var dataSource = DataSourceOf(source.Transport, maxParallel);
        var maps = source.Paths.Select((path, i) => EntityFieldMap.ToColumn(SourceEntityId, path, 1000 + i)).ToList();

        store.FindSourceEntityAsync(SourceEntityId, Arg.Any<CancellationToken>())
            .Returns(new SourceEntity(dataSourceId: 5, "STACK-1", RegistrySourceKind.External));
        store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(dataSource);
        store.GetFieldMapsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(maps);
        store.GetCoverageAsync(Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([new TimeInterval(Origin.AddDays(-400), Origin)]);
        store.StartRunAsync(
                Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(77L);
        store.UpsertRawPointsAsync(
                Arg.Any<long>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<SourceDataPoint>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyList<SourceDataPoint>>(2).Count);

        var catalog = Substitute.For<IUnitCatalog>();
        catalog.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.Ordinal)));

        var runner = new CollectionRunner(
            [source],
            new SourceUnitConverter(new UnitConverter(), catalog),
            new CatchUpPlanner(store, new TestClock(end)),
            store,
            options: new CollectionRunnerOptions(TimeSpan.FromDays(1), DeleteMissingPoints: false));

        return await runner.RunAsync(SourceEntityId, Origin, end, Substitute.For<IJobProgress>(), CancellationToken.None);
    }
}
