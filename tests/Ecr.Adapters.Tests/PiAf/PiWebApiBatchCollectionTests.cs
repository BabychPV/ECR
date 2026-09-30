using System.Globalization;
using System.Net;
using System.Text;
using Ecr.Adapters.PiAf;
using Ecr.Application.Errors;
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
/// Аудит продуктивності P7: збір через справжній <see cref="PiWebApiDataSource"/>
/// і <see cref="CollectionRunner"/> проти фейкового PI Web API, який рахує запити.
/// </summary>
/// <remarks>
/// Було: на кожну пару інтервал × атрибут — <c>attributes?path=</c> і
/// <c>streams/{webId}/recorded</c>, тобто 2·N·M запитів, послідовно. Стало:
/// N пошуків WebId на прогін і один <c>streamsets/recorded</c> на інтервал
/// (≤ 50 атрибутів), до <see cref="CollectionRunner.DefaultMaxParallelReads"/>
/// інтервалів одночасно.
/// </remarks>
public sealed class PiWebApiBatchCollectionTests
{
    private const int SourceEntityId = 42;

    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime From = Now.AddHours(-1);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "P7")]
    public async Task N_атрибутів_на_M_інтервалів_дають_N_пошуків_і_M_пакетів_а_не_2NM()
    {
        const int attributes = 20;
        const int gaps = 24;
        var world = new World(Paths(attributes), gaps: gaps);

        await world.Runner.RunAsync(SourceEntityId, From, Now, world.Progress, CancellationToken.None);

        const int intervals = gaps + 1;

        // ⛔ МУТАЦІЇ (доведено вручну):
        //  • без кешу WebId (AttributeAsync щоразу шле LookupAsync) — пошуків
        //    20 × 25 = 500, а не 20;
        //  • запит на атрибут (MaxStreamsPerRequest = 1) — пакетів 20 × 25 = 500.
        // Було 2 · 20 · 25 = 1000 запитів; стало 20 + 25 = 45.
        Assert.Equal(attributes, world.Server.Count("attributes"));
        Assert.Equal(intervals, world.Server.Count("streamsets/recorded"));
        Assert.Equal(attributes + intervals, world.Server.Requests.Count);

        // Кожен пакет несе всі 20 WebId.
        Assert.All(world.Server.WebIdsPerStreamsetRequest, n => Assert.Equal(attributes, n));

        // Прочитано все: дві точки на атрибут на інтервал (мітки :00 і :30).
        Assert.Equal(attributes * intervals * 2, world.Written.Count);
        Assert.Equal(
            world.Written.Count,
            world.Written.Select(p => (p.SourcePath, p.Timestamp)).Distinct().Count());
        await world.Store.Received().FinishRunAsync(
            Arg.Any<long>(), "Succeeded", attributes * intervals * 2, null, Arg.Any<CancellationToken>());
        Assert.Contains(new TimeInterval(From, Now), world.Coverage);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "P7")]
    public async Task Хвіст_понад_стелю_дочитується_сторінками_в_пакетному_режимі()
    {
        // A і C — по 12 000 точок (три сторінки), B — 3 000 (одна). На межі
        // першої сторінки A шість точок з ОДНІЄЮ міткою: три влазять у батч,
        // три — ні (як у CollectionRunnerPagingTests, B3).
        const int total = 12_000;
        var from = Now.AddMinutes(-total);
        var data = new Dictionary<string, List<DateTime>>
        {
            ["A"] = [.. Enumerable.Range(0, total).Select(i => from.AddMinutes(i is >= 4_997 and <= 5_002 ? 4_997 : i))],
            ["B"] = [.. Enumerable.Range(0, 3_000).Select(i => from.AddMinutes(i))],
            ["C"] = [.. Enumerable.Range(0, total).Select(i => from.AddMinutes(i))],
        };
        var world = new World(["A", "B", "C"], data: data);

        await world.Runner.RunAsync(SourceEntityId, from, Now, world.Progress, CancellationToken.None);

        // ⛔ МУТАЦІЯ (доведено вручну): ReadBatchedAsync не лишає атрибутів на
        // наступний раунд — записано по 5000 на A і C.
        Assert.Equal(total, world.Written.Count(p => p.SourcePath == "A"));
        Assert.Equal(3_000, world.Written.Count(p => p.SourcePath == "B"));
        Assert.Equal(total, world.Written.Count(p => p.SourcePath == "C"));

        // Без дублів і втрат: значення точки — її порядковий номер у потоці.
        foreach (var path in new[] { "A", "C" })
        {
            Assert.Equal(
                Enumerable.Range(0, total),
                world.Written.Where(p => p.SourcePath == path).Select(p => (int)p.ValueNumeric!.Value).Order());
        }

        // Перший раунд — один пакет на всі три атрибути. Сума його точок дійшла
        // до стелі, тож він перечитується по потоку (запобіжник maxCount), а
        // хвости — лише A і C: B у мережі рівно двічі.
        Assert.Equal(3, world.Server.WebIdsPerStreamsetRequest[0]);
        Assert.Equal(2, world.Server.Count("webId=W-B"));
        Assert.Contains(new TimeInterval(from, Now), world.Coverage);
        await world.Store.Received().FinishRunAsync(
            Arg.Any<long>(), "Succeeded", 2 * total + 3_000, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "P7")]
    public async Task Стеля_maxCount_на_всю_відповідь_не_губить_точок()
    {
        // Альтернативна семантика: maxCount 5000 — на ВЕСЬ streamsets. Три
        // потоки по 3000 точок: A отримує 3000 (не обрізаний за ознакою
        // «рівно стеля»), B — 2000 (обрізаний НИЖЧЕ стелі), C — 0.
        const int each = 3_000;
        var from = Now.AddMinutes(-each);
        var stamps = Enumerable.Range(0, each).Select(i => from.AddMinutes(i)).ToList();
        var data = new Dictionary<string, List<DateTime>> { ["A"] = stamps, ["B"] = stamps, ["C"] = stamps };
        var world = new World(["A", "B", "C"], data: data, maxCountIsTotal: true);

        await world.Runner.RunAsync(SourceEntityId, from, Now, world.Progress, CancellationToken.None);

        // ⛔ МУТАЦІЯ (доведено вручну): прибрати запобіжник у ReadChunkAsync
        // (перечитування пакета по потоку, коли сума точок ≥ стелі) — B
        // записує 2000, C — 0, а інтервал покривається як повністю прочитаний.
        foreach (var path in new[] { "A", "B", "C" })
        {
            Assert.Equal(
                Enumerable.Range(0, each),
                world.Written.Where(p => p.SourcePath == path).Select(p => (int)p.ValueNumeric!.Value).Order());
        }

        Assert.Equal(3 * each, world.Written.Count);
        Assert.Contains(new TimeInterval(from, Now), world.Coverage);
        await world.Store.Received().FinishRunAsync(
            Arg.Any<long>(), "Succeeded", 3 * each, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "P7")]
    public async Task Одночасно_в_мережі_не_більше_K_запитів_але_більше_одного()
    {
        const int limit = 3;
        var world = new World(["A", "B"], gaps: 11, delay: TimeSpan.FromMilliseconds(60), maxParallelReads: limit);

        await world.Runner.RunAsync(SourceEntityId, From, Now, world.Progress, CancellationToken.None);

        // ⛔ МУТАЦІЯ: прибрати межу в ReadAhead.Take (`next < count` замість
        // `next < index + limit`) — усі 12 інтервалів ідуть у мережу разом.
        Assert.InRange(world.Server.MaxInFlight, 2, limit);
        Assert.Equal(12, world.Server.Count("streamsets/recorded"));
        await world.Store.Received().FinishRunAsync(
            Arg.Any<long>(), "Succeeded", Arg.Any<int>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "P7")]
    public async Task Помилка_одного_атрибута_в_пакеті_не_валить_сусідів()
    {
        var world = new World(["A", "B", "C"], gaps: 2);
        world.Server.FailingStreams.Add("W-B");

        await world.Runner.RunAsync(SourceEntityId, From, Now, world.Progress, CancellationToken.None);

        // Сусіди прочитані повністю, B — ні; окремих запитів на атрибут немає.
        Assert.Equal(6, world.Written.Count(p => p.SourcePath == "A"));
        Assert.Equal(6, world.Written.Count(p => p.SourcePath == "C"));
        Assert.DoesNotContain(world.Written, p => p.SourcePath == "B");
        Assert.Equal(3, world.Server.Count("streamsets/recorded"));

        // Як і раніше для відмови атрибута: інтервал без покриття, прогін
        // «Degraded», непрочитане — у наздоганяння.
        Assert.DoesNotContain(new TimeInterval(From, Now), world.Coverage);
        await world.Store.Received().FinishRunAsync(
            Arg.Any<long>(), "Degraded", 12, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "P7")]
    public async Task Атрибут_404_шукається_раз_на_прогін_а_відмова_4xx_не_кешується()
    {
        // X — 404 (такого шляху немає: кешується на прогін як «не знайдено»);
        // Y — перший пошук 400 (не відповідь про шлях: у кеш не йде).
        // Без читання наперед: інакше другий інтервал міг би дочекатися ТОГО
        // САМОГО невдалого пошуку Y, і число спроб залежало б від планувальника.
        var world = new World(["A", "X", "Y"], gaps: 2, maxParallelReads: 1);
        world.Server.MissingPaths.Add("X");
        world.Server.RefuseFirstLookup.Add("Y");

        await world.Runner.RunAsync(SourceEntityId, From, Now, world.Progress, CancellationToken.None);

        Assert.Equal(1, world.Server.Count("attributes?path=X"));
        Assert.Equal(2, world.Server.Count("attributes?path=Y"));

        // A — усі три інтервали; Y — два, крім першого, де пошук відмовив.
        Assert.Equal(6, world.Written.Count(p => p.SourcePath == "A"));
        Assert.Equal(4, world.Written.Count(p => p.SourcePath == "Y"));
        Assert.DoesNotContain(world.Written, p => p.SourcePath == "X");
        await world.Store.Received().FinishRunAsync(
            Arg.Any<long>(), "Degraded", 10, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "P7")]
    public async Task Відмова_в_автентифікації_на_пакеті_обриває_прогін_як_і_послідовно()
    {
        var world = new World(["A", "B"], gaps: 5);
        world.Server.RefuseStreamsets = true;

        await Assert.ThrowsAsync<SourceAuthenticationException>(
            () => world.Runner.RunAsync(SourceEntityId, From, Now, world.Progress, CancellationToken.None));

        Assert.Empty(world.Written);
        await world.Store.Received().FinishRunAsync(
            Arg.Any<long>(), CollectionFailure.FailedStatus, 0, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    private static List<string> Paths(int count)
        => [.. Enumerable.Range(1, count).Select(i => $"P{i:D2}")];

    /// <summary>Збирач зі справжнім адаптером PI Web API поверх фейкового сервера.</summary>
    private sealed class World
    {
        public World(
            IReadOnlyList<string> paths,
            int gaps = 0,
            Dictionary<string, List<DateTime>>? data = null,
            TimeSpan? delay = null,
            int? maxParallelReads = null,
            bool maxCountIsTotal = false)
        {
            Server = new FakePiWebApiServer
            {
                Delay = delay ?? TimeSpan.Zero, Data = data, MaxCountIsTotal = maxCountIsTotal,
            };

            var entity = new SourceEntity(dataSourceId: 5, "STACK-1", RegistrySourceKind.External);
            IReadOnlyList<EntityFieldMap> maps = paths.Select((p, i) => EntityFieldMap.ToColumn(SourceEntityId, p, columnDefId: 100 + i)).ToList();

            Store.FindSourceEntityAsync(SourceEntityId, Arg.Any<CancellationToken>()).Returns(entity);
            Store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(FakePiWebApiServer.Source());
            Store.GetFieldMapsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(maps);
            Store.GetCoverageAsync(Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
                .Returns(call => Holes(call.ArgAt<DateTime>(1), gaps));
            Store.StartRunAsync(
                    Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                    Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
                .Returns(77L);
            Store.UpsertRawPointsAsync(
                    Arg.Any<long>(), Arg.Any<int>(),
                    Arg.Any<IReadOnlyList<SourceDataPoint>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var batch = call.ArgAt<IReadOnlyList<SourceDataPoint>>(2);
                    Written.AddRange(batch);
                    return batch.Count;
                });
            Store.WriteCoverageAsync(
                    Arg.Any<long>(), Arg.Any<int>(),
                    Arg.Any<IReadOnlyList<TimeInterval>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Coverage.AddRange(call.ArgAt<IReadOnlyList<TimeInterval>>(2));
                    return Task.CompletedTask;
                });

            var catalog = Substitute.For<IUnitCatalog>();
            catalog.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
                new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, int>(StringComparer.Ordinal)));

            var adapter = new PiWebApiDataSource(new HttpClient(Server), Store, FakePiWebApiServer.Secrets());

            Runner = new CollectionRunner(
                [adapter],
                new SourceUnitConverter(new UnitConverter(), catalog),
                new CatchUpPlanner(Store, new TestClock(Now)),
                Store,
                maxParallelReads: maxParallelReads);
        }

        public FakePiWebApiServer Server { get; }

        public ICollectionStore Store { get; } = Substitute.For<ICollectionStore>();

        public IJobProgress Progress { get; } = Substitute.For<IJobProgress>();

        public List<SourceDataPoint> Written { get; } = [];

        public List<TimeInterval> Coverage { get; } = [];

        public CollectionRunner Runner { get; }

        /// <summary>
        /// Покриття всього огляду, крім <paramref name="gaps"/> годинних дірок
        /// (кожна друга година перед <see cref="From"/>), — стільки ж інтервалів
        /// наздоганяння.
        /// </summary>
        private static List<TimeInterval> Holes(DateTime notBefore, int gaps)
        {
            var holes = Enumerable.Range(1, gaps).Select(k => From.AddHours(-2 * k)).Order().ToList();
            var covered = new List<TimeInterval>();
            var cursor = notBefore;

            foreach (var hole in holes)
            {
                covered.Add(new TimeInterval(cursor, hole));
                cursor = hole.AddHours(1);
            }

            covered.Add(new TimeInterval(cursor, Now));
            return covered;
        }
    }
}
