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
/// D-462 (P1 аудиту PI): довгий діапазон читається ШМАТКАМИ, обрив годинником (Q-250) дає місце продовження, і
/// продовження дочитує решту, не перечитуючи пройденого; навантаження на синтетичному PI — кожна точка рівно раз.
/// </summary>
/// <remarks>
/// ⚠ Тести не міряють стіну: годинник прогону спрацьовує лише на «зависанні» синтетичного джерела, а твердження —
/// про властивості (місце зупинки — межа шматка не далі за зависання, покриття — рівно до нього, кожна точка
/// записана рівно раз, паралельність не вища за стелю, кількість звернень — точно за шматками й сторінками).
/// Стіна прогону на CI — секунди: точки синтетичного джерела обчислюються, а не зберігаються.
/// </remarks>
public sealed class CollectionChunkResumeLoadTests
{
    private const int SourceEntityId = 42;

    private static readonly DateTime Origin = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-462")]
    public void Шматки_ріжуть_інтервал_без_проміжків_і_перекриттів()
    {
        var from = Origin.AddHours(3);
        var to = from.AddDays(2).AddHours(12);

        var chunks = CollectionRunner.Chunks(new TimeInterval(from, to), TimeSpan.FromDays(1)).ToList();

        Assert.Equal(
            [
                new TimeInterval(from, from.AddDays(1)),
                new TimeInterval(from.AddDays(1), from.AddDays(2)),
                new TimeInterval(from.AddDays(2), to),
            ],
            chunks);

        // Без довжини шматка (і з коротким інтервалом) — інтервал цілком, як до D-462.
        Assert.Single(CollectionRunner.Chunks(new TimeInterval(from, to), null));
        Assert.Single(CollectionRunner.Chunks(new TimeInterval(from, from.AddHours(5)), TimeSpan.FromDays(1)));
    }

    /// <summary>
    /// Навантаження, пакетний адаптер (PI Web API): 50 тегів × точка щохвилини × 28 діб = 2 016 000 точок, шматок
    /// тиждень (10 080 точок тега — три сторінки по 5 000).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-462")]
    public async Task Навантаження_пакетного_адаптера_кожна_точка_рівно_раз_і_звернень_рівно_за_шматками_й_сторінками()
    {
        var end = Origin.AddDays(28);
        var source = new SyntheticBatchPi(50, Origin, TimeSpan.FromMinutes(1), end) { Latency = TimeSpan.FromMilliseconds(5) };
        var world = new World(source, end, TimeSpan.FromDays(7));

        var summary = await world.Runner.RunAsync(SourceEntityId, Origin, end, world.Progress, CancellationToken.None);

        Assert.False(summary.Degraded);
        Assert.Null(summary.ResumeFromUtc);
        world.AssertEachPointOnce(source, 0, source.PointsPerTag);

        // 4 шматки × 3 раунди (5 000 + 5 000 + 80 точок на тег) — по одному пакетному зверненню на раунд.
        Assert.Equal(12, source.Calls);
        Assert.InRange(source.MaxInFlight, 1, CollectionRunner.DefaultMaxParallelReads);
        Assert.Equal(Merged(world.Coverage), [new TimeInterval(Origin, end)]);
    }

    /// <summary>
    /// Навантаження, послідовний адаптер (PI SQL Client): 50 тегів × точка щохвилини × 7 діб, шматок доба —
    /// 350 звернень (тег × доба, 1 440 точок — одна сторінка), кожна точка рівно раз.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-462")]
    public async Task Навантаження_послідовного_адаптера_по_зверненню_на_тег_і_добу()
    {
        var end = Origin.AddDays(7);
        var source = new SyntheticPi(50, Origin, TimeSpan.FromMinutes(1), end);
        var world = new World(source, end, TimeSpan.FromDays(1));

        var summary = await world.Runner.RunAsync(SourceEntityId, Origin, end, world.Progress, CancellationToken.None);

        Assert.False(summary.Degraded);
        world.AssertEachPointOnce(source, 0, source.PointsPerTag);
        Assert.Equal(7 * 50, source.Calls);
        Assert.Equal(1, source.MaxInFlight);
        Assert.Equal(Merged(world.Coverage), [new TimeInterval(Origin, end)]);

        // Прогрес — по шматку: «крок N із 7».
        await world.Progress.Received().ReportAsync(
            Arg.Any<int>(),
            Arg.Is<string?>(m => IsProgress(m, total: "7")),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// P1: рік (10 тегів × погодинно) послідовним адаптером; джерело «зависає» на 30-й добі — годинник прогону
    /// обриває, підсумок називає місце продовження, і другий прогін від нього дочитує рік, не повертаючись назад.
    /// (50 тегів — у пакетному варіанті нижче: послідовний адаптер платить звернення на тег × добу.)
    /// </summary>
    /// <remarks>
    /// ⛔ МУТАЦІЙНІ ДОКАЗИ: прибрати нарізку (<c>Chunks</c> повертає інтервал цілком) — послідовний адаптер
    /// читає рік тег за тегом, і обрив не покриває НІЧОГО (<c>ResumeFromUtc</c> = початок, покриття порожнє);
    /// прибрати <c>resumeFrom</c> у гілці годинника — <c>ResumeFromUtc</c> = <c>null</c>.
    /// </remarks>
    [Fact(Timeout = 60_000)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-462")]
    public async Task Рік_обірваний_годинником_продовжується_з_місця_зупинки_без_перечитування()
    {
        var end = Origin.AddDays(365);
        var hangAt = Origin.AddDays(30);
        var source = new SyntheticPi(10, Origin, TimeSpan.FromHours(1), end)
        {
            HangWhen = r => r.FromUtc >= hangAt,
        };

        var first = new World(source, end, TimeSpan.FromDays(1), maxRunDuration: TimeSpan.FromSeconds(3));
        var stopped = await first.Runner.RunAsync(SourceEntityId, Origin, end, first.Progress, CancellationToken.None);

        Assert.True(stopped.Degraded);
        var resume = Assert.IsType<DateTime>(stopped.ResumeFromUtc);

        // Місце зупинки — межа шматка, не раніше за початок і не пізніше за зависання; покриття — рівно до нього.
        Assert.InRange(resume, Origin.AddDays(1), hangAt);
        Assert.Equal(TimeSpan.Zero, TimeSpan.FromTicks((resume - Origin).Ticks % TimeSpan.TicksPerDay));
        Assert.Equal(Merged(first.Coverage), [new TimeInterval(Origin, resume)]);

        // Продовження (те, що ставить CollectionJob): джерело ожило, діапазон — від місця зупинки.
        source.HangWhen = null;
        var before = source.Requests.Count;
        var second = new World(
            source, end, TimeSpan.FromDays(1),
            coverage: [new TimeInterval(Origin.AddDays(-400), Origin), .. first.Coverage],
            written: first);
        var done = await second.Runner.RunAsync(SourceEntityId, resume, end, second.Progress, CancellationToken.None);

        Assert.False(done.Degraded);
        Assert.Null(done.ResumeFromUtc);

        // ⛔ Пройдене до зупинки НЕ перечитується: жодного запиту другого прогону раніше за місце зупинки.
        Assert.DoesNotContain(source.Requests.Skip(before), r => r.FromUtc < resume);

        // Разом — кожна точка року рівно раз.
        second.AssertEachPointOnce(source, 0, source.PointsPerTag);
    }

    /// <summary>
    /// Пакетний адаптер, рік (50 тегів × погодинно): обрив годинником на 200-й добі дає місце продовження, і
    /// продовження дочитує рік — кожна точка рівно раз.
    /// </summary>
    [Fact(Timeout = 60_000)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-462")]
    public async Task Пакетний_адаптер_рік_обірваний_годинником_продовжується_з_місця_зупинки()
    {
        var end = Origin.AddDays(365);
        var hangAt = Origin.AddDays(200);
        var source = new SyntheticBatchPi(50, Origin, TimeSpan.FromHours(1), end)
        {
            HangWhen = r => r.FromUtc >= hangAt,
        };

        var first = new World(source, end, TimeSpan.FromDays(1), maxRunDuration: TimeSpan.FromSeconds(3));
        var stopped = await first.Runner.RunAsync(SourceEntityId, Origin, end, first.Progress, CancellationToken.None);

        var resume = Assert.IsType<DateTime>(stopped.ResumeFromUtc);
        Assert.InRange(resume, Origin.AddDays(1), hangAt);
        Assert.Equal(Merged(first.Coverage), [new TimeInterval(Origin, resume)]);

        source.HangWhen = null;
        var before = source.Requests.Count;
        var second = new World(
            source, end, TimeSpan.FromDays(1),
            coverage: [new TimeInterval(Origin.AddDays(-400), Origin), .. first.Coverage],
            written: first);
        var done = await second.Runner.RunAsync(SourceEntityId, resume, end, second.Progress, CancellationToken.None);

        Assert.Null(done.ResumeFromUtc);
        Assert.DoesNotContain(source.Requests.Skip(before), r => r.FromUtc < resume);

        // ⚠ Читання НАПЕРЕД (P7) могло встигнути прочитати шматки за місцем зупинки, але не записати: записане —
        // лише в порядку шматків, тож і тут кожна точка рівно раз.
        second.AssertEachPointOnce(source, 0, source.PointsPerTag);
    }

    private static bool IsProgress(string? raw, string total)
        => JobProgressMessageCodec.TryDecode(raw, out var envelope)
           && envelope.Key == "jobs.collectionProgress"
           && envelope.Params?.GetValueOrDefault("total") == total;

    private static List<TimeInterval> Merged(IEnumerable<TimeInterval> intervals)
    {
        var result = new List<TimeInterval>();

        foreach (var interval in intervals.OrderBy(i => i.FromUtc))
        {
            if (result.Count > 0 && interval.FromUtc <= result[^1].ToUtc)
            {
                var last = result[^1];
                result[^1] = new TimeInterval(last.FromUtc, interval.ToUtc > last.ToUtc ? interval.ToUtc : last.ToUtc);
            }
            else
            {
                result.Add(interval);
            }
        }

        return result;
    }

    /// <summary>Збирач над синтетичним джерелом і сховищем-лічильником (точка → скільки разів записано).</summary>
    private sealed class World
    {
        public World(
            SyntheticPi source,
            DateTime now,
            TimeSpan chunk,
            TimeSpan? maxRunDuration = null,
            IReadOnlyList<TimeInterval>? coverage = null,
            World? written = null)
        {
            Counts = written?.Counts ?? source.Paths.ToDictionary(p => p, _ => new int[source.PointsPerTag]);

            var entity = new SourceEntity(dataSourceId: 5, "STACK-1", RegistrySourceKind.External);
            var dataSource = new DataSource(
                EcrCode.Create("PIAF"),
                new LocalizedText(new Dictionary<string, string> { ["uk"] = "PI AF" }),
                ExternalTransport.PiSqlClient,
                "pi-sql.example",
                "PiAf.Primary");

            var maps = source.Paths
                .Select((path, i) => EntityFieldMap.ToColumn(SourceEntityId, path, 1000 + i))
                .ToList();

            Store.FindSourceEntityAsync(SourceEntityId, Arg.Any<CancellationToken>()).Returns(entity);
            Store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(dataSource);
            Store.GetFieldMapsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(maps);
            Store.GetCoverageAsync(Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
                .Returns(coverage ?? [new TimeInterval(Origin.AddDays(-400), Origin)]);
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

                    foreach (var point in batch)
                    {
                        Counts[point.SourcePath][source.IndexOf(point.Timestamp)]++;
                    }

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

            Runner = new CollectionRunner(
                [source],
                new SourceUnitConverter(new UnitConverter(), catalog),
                new CatchUpPlanner(Store, new TestClock(now)),
                Store,
                maxRunDuration,
                options: new CollectionRunnerOptions(chunk, DeleteMissingPoints: true));
        }

        public ICollectionStore Store { get; } = Substitute.For<ICollectionStore>();

        public IJobProgress Progress { get; } = Substitute.For<IJobProgress>();

        public Dictionary<string, int[]> Counts { get; }

        public List<TimeInterval> Coverage { get; } = [];

        public CollectionRunner Runner { get; }

        /// <summary>Кожна точка кожного тега в <c>[first, last)</c> записана рівно раз.</summary>
        public void AssertEachPointOnce(SyntheticPi source, long first, long last)
        {
            foreach (var path in source.Paths)
            {
                var counts = Counts[path];

                for (var i = first; i < last; i++)
                {
                    Assert.True(counts[i] == 1, $"{path}: точку {i} записано {counts[i]} раз(и).");
                }
            }
        }
    }
}
