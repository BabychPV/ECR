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
/// D-462 (P2 аудиту PI): сирі точки, яких PI більше не віддає, видаляються ЛИШЕ за шматок вікна, який атрибут
/// дочитав повністю й без відмов, — ніколи за частковим чи невдалим читанням і ніколи поза вікном.
/// </summary>
/// <remarks>
/// Сховище — підставне: тут перевіряється, ЩО збирач просить видалити (атрибут, межі, мітки). Що сховище видаляє
/// рівно це — <c>CollectionStoreMissingPointsSqlTests</c> на справжній базі.
/// </remarks>
public sealed class CollectionRunnerMissingPointsTests
{
    private const int SourceEntityId = 42;
    private const string Flow = "E1|Flow";
    private const string Temp = "E1|Temp";

    private static readonly DateTime From = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = From.AddDays(2);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-462")]
    public async Task Повністю_прочитаний_шматок_вікна_видаляє_зниклі_за_мітками_джерела()
    {
        var world = new World(HourlyPoints(Flow, Temp));

        await world.Runner.RunAsync(SourceEntityId, From, To, world.Progress, CancellationToken.None);

        // Дві доби × два атрибути — чотири видалення, кожне — рівно за свою добу і рівно з мітками джерела.
        Assert.Equal(4, world.Deletions.Count);

        foreach (var day in new[] { From, From.AddDays(1) })
        {
            foreach (var path in new[] { Flow, Temp })
            {
                var deletion = Assert.Single(world.Deletions, d => d.Path == path && d.From == day);
                Assert.Equal(day.AddDays(1), deletion.To);
                Assert.Equal(Enumerable.Range(0, 24).Select(h => day.AddHours(h)), deletion.Present.Order());
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-462")]
    public async Task Сторінки_одного_шматка_зливаються_в_одне_видалення_після_останньої()
    {
        // 6 000 точок за добу одного атрибута: дві сторінки (5 000 + хвіст).
        var points = Enumerable.Range(0, 6_000)
            .Select(i => new SourceDataPoint(Flow, From.AddSeconds(i * 14), i, null, null, "Good"))
            .ToList();

        var world = new World(points, paths: [Flow], to: From.AddDays(1));

        await world.Runner.RunAsync(SourceEntityId, From, From.AddDays(1), world.Progress, CancellationToken.None);

        var deletion = Assert.Single(world.Deletions);
        Assert.Equal(6_000, deletion.Present.Count);
        Assert.Equal(2, world.Requests.Count);
    }

    /// <remarks>
    /// ⛔ МУТАЦІЯ: викликати видалення після кожної сторінки (а не з гілки «хвоста немає») — тут з'являється
    /// видалення для <c>Temp</c> за першу добу з частковим набором міток.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-462")]
    public async Task Часткова_відмова_атрибута_в_шматку_не_видаляє_нічого_за_цей_атрибут_і_шматок()
    {
        var world = new World(HourlyPoints(Flow, Temp))
        {
            // Temp за першу добу: джерело віддало половину і відмовило на хвості.
            Refuse = r => r.SourcePath == Temp && r.FromUtc < From.AddDays(1),
        };

        await world.Runner.RunAsync(SourceEntityId, From, To, world.Progress, CancellationToken.None);

        Assert.DoesNotContain(world.Deletions, d => d.Path == Temp && d.From == From);
        Assert.Contains(world.Deletions, d => d.Path == Temp && d.From == From.AddDays(1));
        Assert.Contains(world.Deletions, d => d.Path == Flow && d.From == From);
    }

    /// <remarks>
    /// ✎ Рішення власника продукту (D-462): УСПІШНА порожня відповідь за шматок — теж відповідь; наші точки шматка
    /// видаляються (порожній перелік міток), кількість — у журнал.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-462")]
    public async Task Успішна_порожня_відповідь_за_шматок_видаляє_всі_наші_точки_шматка()
    {
        // Друга доба — джерело успішно віддало нуль точок.
        var world = new World(HourlyPoints(Flow, Temp).Where(p => p.Timestamp < From.AddDays(1)).ToList());

        await world.Runner.RunAsync(SourceEntityId, From, To, world.Progress, CancellationToken.None);

        Assert.Equal(4, world.Deletions.Count);
        Assert.All(
            world.Deletions.Where(d => d.From == From.AddDays(1)),
            d => Assert.Empty(d.Present));
    }

    /// <remarks>
    /// ⛔ Помилка — ніколи не «порожньо»: виняток транспорту за шматок не видаляє нічого за цей атрибут і шматок.
    /// МУТАЦІЯ: викликати видалення з гілки відмови (<c>Collected is null</c>) — тут з'являється видалення Temp/доба 1.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-462")]
    public async Task Виняток_джерела_не_вважається_порожньою_відповіддю()
    {
        var world = new World(HourlyPoints(Flow, Temp))
        {
            Throw = r => r.SourcePath == Temp && r.FromUtc < From.AddDays(1),
        };

        await world.Runner.RunAsync(SourceEntityId, From, To, world.Progress, CancellationToken.None);

        Assert.DoesNotContain(world.Deletions, d => d.Path == Temp && d.From == From);
        Assert.Contains(world.Deletions, d => d.Path == Temp && d.From == From.AddDays(1));
    }

    /// <summary>
    /// ✎ Рішення власника продукту (D-462): нарізка не змінює кількості запитів звичайного прогону — типовий
    /// плановий прогін (7 діб) за типових налаштувань — один запит на тег.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-462")]
    public async Task Плановий_прогін_7_діб_за_типових_налаштувань_один_запит_на_тег()
    {
        var to = From.AddDays(7);
        var world = new World(HourlyPoints(Flow, Temp), to: to, options: CollectionRunnerOptions.Default);

        await world.Runner.RunAsync(SourceEntityId, From, to, world.Progress, CancellationToken.None);

        Assert.Equal(2, world.Requests.Count);
        Assert.All(world.Requests, r => Assert.Equal((From, to), (r.FromUtc, r.ToUtc)));
    }

    /// <remarks>⛔ МУТАЦІЯ: прибрати <c>InWindow</c> у <c>Track</c>/<c>RemoveMissingAsync</c> — з'являється видалення за прогалину.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-462")]
    public async Task Прогалина_наздоганяння_поза_вікном_не_видаляє_нічого()
    {
        // Покриття закінчується за три доби до вікна — наздоганяння читає [From-3d, From).
        var gapFrom = From.AddDays(-3);
        var points = HourlyPoints(Flow, Temp, gapFrom);
        var world = new World(points, coverage: [new TimeInterval(From.AddDays(-60), gapFrom)]);

        await world.Runner.RunAsync(SourceEntityId, From, To, world.Progress, CancellationToken.None);

        Assert.Contains(world.Requests, r => r.FromUtc < From);
        Assert.NotEmpty(world.Deletions);
        Assert.All(world.Deletions, d => Assert.True(d.From >= From && d.To <= To, $"видалення поза вікном: {d.From:O}"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-462")]
    public async Task Вимкнене_налаштування_не_видаляє_нічого()
    {
        var world = new World(HourlyPoints(Flow, Temp), options: new CollectionRunnerOptions(TimeSpan.FromDays(1), DeleteMissingPoints: false));

        await world.Runner.RunAsync(SourceEntityId, From, To, world.Progress, CancellationToken.None);

        Assert.Empty(world.Deletions);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-462")]
    public async Task Видалення_у_вікні_не_зсуває_матеріалізацію_раніше_за_вікно()
    {
        var world = new World(HourlyPoints(Flow, Temp)) { Removed = 3 };

        var summary = await world.Runner.RunAsync(SourceEntityId, From, To, world.Progress, CancellationToken.None);

        // Вікно матеріалізації не раніше за запитане: видалення лише в межах вікна.
        Assert.Equal(From, summary.ReadFromUtc);
        Assert.Equal(4, world.Deletions.Count);
    }

    private static List<SourceDataPoint> HourlyPoints(string a, string b, DateTime? from = null)
    {
        var start = from ?? From;
        var hours = (int)(To - start).TotalHours;

        return
        [
            .. Enumerable.Range(0, hours).SelectMany(h => new[]
            {
                new SourceDataPoint(a, start.AddHours(h), h, null, null, "Good"),
                new SourceDataPoint(b, start.AddHours(h), -h, null, null, "Good"),
            }),
        ];
    }

    private sealed record Deletion(string Path, DateTime From, DateTime To, IReadOnlyCollection<DateTime> Present);

    private sealed class World
    {
        public World(
            List<SourceDataPoint> points,
            IReadOnlyList<string>? paths = null,
            DateTime? to = null,
            IReadOnlyList<TimeInterval>? coverage = null,
            CollectionRunnerOptions? options = null)
        {
            var entity = new SourceEntity(dataSourceId: 5, "E1", RegistrySourceKind.External);
            var dataSource = new DataSource(
                EcrCode.Create("PIAF"),
                new LocalizedText(new Dictionary<string, string> { ["uk"] = "PI AF" }),
                ExternalTransport.PiSqlClient,
                "pi-sql.example",
                "PiAf.Primary");

            var maps = (paths ?? [Flow, Temp]).Select((p, i) => EntityFieldMap.ToColumn(SourceEntityId, p, 10 + i)).ToList();

            Store.FindSourceEntityAsync(SourceEntityId, Arg.Any<CancellationToken>()).Returns(entity);
            Store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(dataSource);
            Store.GetFieldMapsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(maps);
            Store.GetCoverageAsync(Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
                .Returns(coverage ?? [new TimeInterval(From.AddDays(-60), From)]);
            Store.StartRunAsync(
                    Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                    Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
                .Returns(77L);
            Store.UpsertRawPointsAsync(
                    Arg.Any<long>(), Arg.Any<int>(),
                    Arg.Any<IReadOnlyList<SourceDataPoint>>(), Arg.Any<CancellationToken>())
                .Returns(call => call.ArgAt<IReadOnlyList<SourceDataPoint>>(2).Count);
            Store.DeleteMissingRawPointsAsync(
                    Arg.Any<int>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                    Arg.Any<IReadOnlyCollection<DateTime>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Deletions.Add(new Deletion(
                        call.ArgAt<string>(1),
                        call.ArgAt<DateTime>(2),
                        call.ArgAt<DateTime>(3),
                        [.. call.ArgAt<IReadOnlyCollection<DateTime>>(4)]));
                    return Removed;
                });

            Source.Transport.Returns(ExternalTransport.PiSqlClient);
            Source.ReadAsync(Arg.Any<CollectionRequest>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var request = call.ArgAt<CollectionRequest>(0);
                    Requests.Add(request);

                    var inside = points
                        .Where(p => p.SourcePath == request.SourcePath && p.Timestamp >= request.FromUtc && p.Timestamp < request.ToUtc)
                        .ToList();

                    if (Throw?.Invoke(request) == true)
                    {
                        throw new HttpRequestException("connection reset");
                    }

                    if (Refuse?.Invoke(request) == true)
                    {
                        // Половина діапазону — і відмова на хвості, як часткова відмова батча.
                        var half = inside.Take(inside.Count / 2).ToList();
                        var tail = half.Count > 0 ? half[^1].Timestamp : request.FromUtc;
                        return Task.FromResult(new CollectionResult(
                            half, [new TimeInterval(tail, request.ToUtc)], "ECR-INT-0503"));
                    }

                    var batch = inside.Take(request.MaxPoints).ToList();
                    var truncated = batch.Count > 0 && batch.Count >= request.MaxPoints;

                    return Task.FromResult(new CollectionResult(
                        batch, truncated ? [new TimeInterval(batch[^1].Timestamp, request.ToUtc)] : [], null));
                });

            var catalog = Substitute.For<IUnitCatalog>();
            catalog.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
                new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, int>(StringComparer.Ordinal)));

            Runner = new CollectionRunner(
                [Source],
                new SourceUnitConverter(new UnitConverter(), catalog),
                new CatchUpPlanner(Store, new TestClock(to ?? To)),
                Store,
                options: options ?? new CollectionRunnerOptions(TimeSpan.FromDays(1), DeleteMissingPoints: true));
        }

        public ICollectionStore Store { get; } = Substitute.For<ICollectionStore>();

        public IExternalDataSource Source { get; } = Substitute.For<IExternalDataSource>();

        public IJobProgress Progress { get; } = Substitute.For<IJobProgress>();

        public List<CollectionRequest> Requests { get; } = [];

        public List<Deletion> Deletions { get; } = [];

        public Func<CollectionRequest, bool>? Refuse { get; init; }

        public Func<CollectionRequest, bool>? Throw { get; init; }

        public int Removed { get; init; }

        public CollectionRunner Runner { get; }
    }
}
