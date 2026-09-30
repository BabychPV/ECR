using Ecr.Adapters.PiAf;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Errors;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.PiAf;

/// <summary>
/// P7, рівень адаптера: кеш WebId і пакетне <c>streamsets/recorded</c> у
/// <see cref="PiWebApiDataSource"/>. Збір цілком — <c>PiWebApiBatchCollectionTests</c>.
/// </summary>
public sealed class PiWebApiBatchReadTests
{
    private static readonly DateTime From = new(2026, 3, 1, 11, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = From.AddHours(1);

    [Fact]
    [Trait("Finding", "P7")]
    public async Task Послідовне_ReadAsync_шукає_WebId_раз_на_екземпляр()
    {
        var server = new FakePiWebApiServer();
        var store = Substitute.For<ICollectionStore>();
        store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(FakePiWebApiServer.Source());
        var sut = new PiWebApiDataSource(new HttpClient(server), store, FakePiWebApiServer.Secrets());

        for (var hour = 0; hour < 3; hour++)
        {
            var result = await sut.ReadAsync(
                new CollectionRequest(1, 42, "A", From.AddHours(-hour), To.AddHours(-hour), 5_000),
                CancellationToken.None);
            Assert.Equal(2, result.Points.Count);
        }

        // ⛔ МУТАЦІЯ (доведено вручну): без кешу (AttributeAsync щоразу шле
        // LookupAsync) — три пошуки замість одного.
        Assert.Equal(1, server.Count("attributes"));
        Assert.Equal(3, server.Count("streams/"));
    }

    [Fact]
    [Trait("Finding", "P7")]
    public async Task Пакет_ріжеться_по_50_WebId()
    {
        var (server, sut) = Create(new FakePiWebApiServer());

        var items = await sut.ReadBatchAsync(FakePiWebApiServer.Source(), Requests(60), CancellationToken.None);

        // ⛔ МУТАЦІЯ: MaxStreamsPerRequest = 1 — шістдесят запитів по одному.
        Assert.Equal(60, items.Count);
        Assert.All(items, i => Assert.Equal(2, i.Collected!.Points.Count));
        Assert.Equal([50, 10], server.WebIdsPerStreamsetRequest);
        Assert.Equal(60, server.Count("attributes"));
    }

    [Fact]
    [Trait("Finding", "P7")]
    public async Task Пакет_ріжеться_за_довжиною_WebId_у_запиті()
    {
        // WebId по 300 символів: у 2000 символів запиту влазить шість.
        var (server, sut) = Create(new FakePiWebApiServer { WebIdPadding = 300 });

        var items = await sut.ReadBatchAsync(FakePiWebApiServer.Source(), Requests(10), CancellationToken.None);

        Assert.All(items, i => Assert.Equal(2, i.Collected!.Points.Count));
        Assert.Equal([6, 4], server.WebIdsPerStreamsetRequest);
        Assert.All(server.Requests, r => Assert.True(r.Length < 2_300, $"запит {r.Length} символів"));
    }

    [Fact]
    [Trait("Finding", "P7")]
    public async Task Потік_із_помилкою_і_шлях_404_не_валять_сусідів_у_пакеті()
    {
        var server = new FakePiWebApiServer();
        server.FailingStreams.Add("W-P02");
        server.MissingPaths.Add("P03");
        var (_, sut) = Create(server);

        var items = await sut.ReadBatchAsync(FakePiWebApiServer.Source(), Requests(4), CancellationToken.None);

        // Сусіди — як без помилок.
        Assert.Equal(2, items[0].Collected!.Points.Count);
        Assert.Equal(2, items[3].Collected!.Points.Count);
        Assert.Empty(items[0].Collected!.FailedIntervals);

        // Потік із Errors — як атрибут без WebId: увесь інтервал у FailedIntervals.
        Assert.Empty(items[1].Collected!.Points);
        Assert.Equal([new TimeInterval(From, To)], items[1].Collected!.FailedIntervals);
        Assert.Equal(ErrorCodes.SourceUnavailable, items[1].Collected!.ErrorCode);

        // 404 на пошук — та сама відмова, що кидав би ReadAsync.
        var missing = Assert.IsType<BusinessRuleException>(items[2].Error);
        Assert.Equal(ErrorCodes.SourceUnavailable, missing.ErrorCode);
        Assert.Null(items[2].Collected);

        // Один пакет на три знайдені атрибути.
        Assert.Equal([3], server.WebIdsPerStreamsetRequest);
    }

    [Fact]
    [Trait("Finding", "P7")]
    public async Task Хвіст_потоку_на_стелі_повертається_як_у_ReadAsync()
    {
        var (_, sut) = Create(new FakePiWebApiServer());
        List<CollectionRequest> requests =
        [
            new(1, 42, "A", From, To, MaxPoints: 1),
            new(1, 42, "B", From, To, MaxPoints: 1),
        ];

        var items = await sut.ReadBatchAsync(FakePiWebApiServer.Source(), requests, CancellationToken.None);

        // maxCount — на КОЖЕН потік: обидва віддали стелю й мають свій хвіст.
        Assert.All(items, i =>
        {
            var point = Assert.Single(i.Collected!.Points);
            Assert.Equal([new TimeInterval(point.Timestamp, To)], i.Collected.FailedIntervals);
        });
    }

    private static (FakePiWebApiServer Server, PiWebApiDataSource Sut) Create(FakePiWebApiServer server)
        => (server, new PiWebApiDataSource(
            new HttpClient(server), Substitute.For<ICollectionStore>(), FakePiWebApiServer.Secrets()));

    private static List<CollectionRequest> Requests(int count)
        => [.. Enumerable.Range(1, count).Select(i => new CollectionRequest(1, 42, $"P{i:D2}", From, To, 5_000))];
}
