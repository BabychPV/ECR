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

namespace Ecr.Adapters.Tests;

/// <summary>
/// Аудит B3: інтервал, у якому точок більше за
/// <see cref="CollectionRunner.MaxPointsPerRequest"/>, дочитується сторінками —
/// без втрат і дублів на межі сторінки, з покриттям рівно за прочитане.
/// </summary>
/// <remarks>
/// ⚠ Фейкове джерело поводиться як усі три адаптери (<c>PiWebApiDataSource</c>
/// <c>maxCount</c>, <c>PiSqlClientDataSource</c> і <c>SqlDataSource</c> — стеля
/// рядків): точки <c>[From, To)</c> за часом, не більше <c>MaxPoints</c>, а
/// повний батч повертає хвіст <c>[мітка останньої точки, To)</c> без коду
/// помилки. На межі першої сторінки навмисно лежать ШІСТЬ точок з однаковою
/// міткою: три потрапляють у перший батч, три — ні.
/// </remarks>
public sealed class CollectionRunnerPagingTests
{
    private const int SourceEntityId = 42;
    private const int Total = 12_000;

    /// <summary>Індекси точок, що мають ОДНУ мітку — ту, що й точка 4997.</summary>
    private const int SameStampFirst = 4_997;
    private const int SameStampLast = 5_002;

    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime From = Now.AddMinutes(-Total);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "B3")]
    public async Task Інтервал_понад_стелю_дочитується_сторінками_без_втрат_і_дублів()
    {
        var world = new World();

        await world.Runner.RunAsync(SourceEntityId, From, Now, world.Progress, CancellationToken.None);

        // ⛔ МУТАЦІЙНІ ДОКАЗИ (CollectionRunner.RunAsync):
        //  • без сторінкування (break замість `cursor = next`) — записано 5000;
        //  • межа «>» (`cursor = next.AddTicks(1)`) — губляться точки 5000–5002
        //    з тією самою міткою, що й остання точка першого батча;
        //  • без відкидання повторів (Fresh повертає сторінку як є) — точки
        //    4997–4999 і 9996 записуються двічі.
        var values = world.Written.Select(p => (int)p.ValueNumeric!.Value).ToList();
        Assert.Equal(Total, values.Count);
        Assert.Equal(Enumerable.Range(0, Total), values.Order());

        // Третя сторінка почалася з мітки останньої точки другої, а не з
        // початку інтервалу: це справді курсор, а не перечитування.
        var requested = world.Requests.Where(r => r.FromUtc >= From).ToList();
        Assert.Equal(3, requested.Count);
        Assert.Equal([From, Stamp(SameStampFirst), Stamp(9_996)], requested.Select(r => r.FromUtc));

        // Покриття — весь запитаний інтервал (плюс порожнє наздоганяння до нього).
        Assert.Contains(new TimeInterval(From, Now), world.Coverage);

        await world.Store.Received().FinishRunAsync(
            Arg.Any<long>(), "Succeeded", Total, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "B3")]
    public async Task Ліміт_сторінок_покриває_прочитане_і_наступний_прогін_продовжує_з_місця()
    {
        // Прогін 1: дві сторінки на атрибут, далі — стоп.
        var first = new World(maxPagesPerRead: 2);
        await first.Runner.RunAsync(SourceEntityId, From, Now, first.Progress, CancellationToken.None);

        var stop = Stamp(9_996);

        // ⛔ Покриття — рівно за прочитане, [From, мітка останньої точки) —
        // не за весь інтервал (дірка назавжди) і не нуль (вічне перечитування
        // з початку). МУТАЦІЯ: повернути «покриття лише за повний інтервал» —
        // запису [From, stop) немає.
        Assert.Contains(new TimeInterval(From, stop), first.Coverage);
        Assert.DoesNotContain(first.Coverage, c => c.ToUtc > stop && c.FromUtc < Now && c.FromUtc >= From);

        // Причина правдива: ліміт сторінок, а не «джерело недоступне».
        await first.Store.Received().FinishRunAsync(
            Arg.Any<long>(),
            "Degraded",
            Arg.Any<int>(),
            Arg.Is<string?>(m => m != null
                                 && m.Contains("ліміт сторінок", StringComparison.Ordinal)
                                 && !m.Contains("джерело недоступне", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());

        // Прогін 2 — наступний за розкладом (свіже вікно після Now), бачить
        // покриття прогону 1 і дочитує прогалину як наздоганяння.
        var second = new World(coverage: first.Coverage);
        await second.Runner.RunAsync(
            SourceEntityId, Now, Now.AddHours(1), second.Progress, CancellationToken.None);

        // Наздоганяння почалося З МІСЦЯ ЗУПИНКИ, а не з початку інтервалу.
        Assert.Equal(stop, second.Requests.Min(r => r.FromUtc));

        // Разом два прогони записали всі точки; повтор межової точки 9996 —
        // це ідемпотентний upsert за природним ключем, а не втрата.
        var union = first.Written.Concat(second.Written)
            .Select(p => (int)p.ValueNumeric!.Value).Distinct().Order();
        Assert.Equal(Enumerable.Range(0, Total), union);
        Assert.Contains(new TimeInterval(stop, Now), second.Coverage);
    }

    [Fact(Timeout = 30000)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "B3")]
    public async Task Обрив_годинником_посеред_сторінок_покриває_прочитані_сторінки()
    {
        // Третя сторінка «зависає» — спрацьовує watchdog прогону (Q-250).
        var world = new World(maxRunDuration: TimeSpan.FromSeconds(2), hangFromPage: 3);

        await world.Runner.RunAsync(SourceEntityId, From, Now, world.Progress, CancellationToken.None);

        // ⛔ МУТАЦІЯ: прибрати `covered = CoveredWithInFlight()` у watchdog-гілці —
        // покриття за дві прочитані сторінки губиться, і наступний прогін знову
        // почне інтервал з початку й упреться в той самий ліміт.
        Assert.Contains(new TimeInterval(From, Stamp(9_996)), world.Coverage);
        Assert.Equal(9_997, world.Written.Count);

        await world.Store.Received().FinishRunAsync(
            Arg.Any<long>(), "Degraded", Arg.Any<int>(),
            Arg.Is<string?>(m => m != null && m.Contains("ліміт часу", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "B3")]
    public async Task Сторінка_з_однією_міткою_не_зациклює_і_називає_причину()
    {
        // Понад стелю точок з ОДНАКОВОЮ міткою: курсор за часом не просувається.
        var stamp = From.AddMinutes(10);
        var points = Enumerable.Range(0, CollectionRunner.MaxPointsPerRequest + 10)
            .Select(i => new SourceDataPoint("tag", stamp, i, null, null, "Good"))
            .ToList();
        var world = new World(points: points);

        await world.Runner.RunAsync(SourceEntityId, From, Now, world.Progress, CancellationToken.None);

        // Сторінка 1 з From, сторінка 2 з мітки — і стоп, а не нескінченний цикл.
        Assert.Equal([From, stamp], world.Requests.Where(r => r.FromUtc >= From).Select(r => r.FromUtc));

        // До мітки точок немає — це прочитано; саму мітку і далі — ні.
        Assert.Contains(new TimeInterval(From, stamp), world.Coverage);
        Assert.DoesNotContain(world.Coverage, c => c.ToUtc > stamp);
        Assert.Equal(CollectionRunner.MaxPointsPerRequest, world.Written.Count);
        await world.Store.Received().FinishRunAsync(
            Arg.Any<long>(), "Degraded", Arg.Any<int>(),
            Arg.Is<string?>(m => m != null && m.Contains("однакову мітку", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    /// <summary>Мітка точки з індексом <paramref name="index"/> у стандартному наборі.</summary>
    private static DateTime Stamp(int index)
        => From.AddMinutes(index is >= SameStampFirst and <= SameStampLast ? SameStampFirst : index);

    private static List<SourceDataPoint> StandardPoints()
        => Enumerable.Range(0, Total)
            .Select(i => new SourceDataPoint("tag", Stamp(i), i, null, null, "Good"))
            .ToList();

    /// <summary>Збирач із фейковим джерелом за семантикою адаптерів і сховищем, що все запам'ятовує.</summary>
    private sealed class World
    {
        public World(
            int? maxPagesPerRead = null,
            IReadOnlyList<TimeInterval>? coverage = null,
            TimeSpan? maxRunDuration = null,
            int? hangFromPage = null,
            List<SourceDataPoint>? points = null)
        {
            var data = points ?? StandardPoints();

            var entity = new SourceEntity(dataSourceId: 5, "STACK-1", RegistrySourceKind.External);
            entity.Describe("Димова труба", "tag");

            var dataSource = new DataSource(
                EcrCode.Create("PIAF"),
                new LocalizedText(new Dictionary<string, string> { ["uk"] = "PI AF" }),
                ExternalTransport.PiSqlClient,
                "https://pi.example",
                "PiAf.Primary");

            Store.FindSourceEntityAsync(SourceEntityId, Arg.Any<CancellationToken>()).Returns(entity);
            Store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(dataSource);
            Store.GetFieldMapsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<EntityFieldMap>());
            Store.GetCoverageAsync(Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
                .Returns(coverage ?? Array.Empty<TimeInterval>());
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

            Source.Transport.Returns(ExternalTransport.PiSqlClient);

            var page = 0;
            Source.ReadAsync(Arg.Any<CollectionRequest>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var request = call.ArgAt<CollectionRequest>(0);
                    Requests.Add(request);

                    var inside = data.Where(p => p.Timestamp >= request.FromUtc && p.Timestamp < request.ToUtc);
                    if (inside.Any() && ++page >= hangFromPage)
                    {
                        return HangAsync(call.ArgAt<CancellationToken>(1));
                    }

                    var batch = inside.Take(request.MaxPoints).ToList();
                    var truncated = batch.Count > 0 && batch.Count >= request.MaxPoints;

                    return Task.FromResult(new CollectionResult(
                        batch,
                        truncated ? [new TimeInterval(batch[^1].Timestamp, request.ToUtc)] : [],
                        null));
                });

            Catalog.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
                new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, int>(StringComparer.Ordinal)));

            Runner = new CollectionRunner(
                [Source],
                new SourceUnitConverter(new UnitConverter(), Catalog),
                new CatchUpPlanner(Store, new TestClock(Now)),
                Store,
                maxRunDuration,
                logger: null,
                maxPagesPerRead);
        }

        public ICollectionStore Store { get; } = Substitute.For<ICollectionStore>();

        public IExternalDataSource Source { get; } = Substitute.For<IExternalDataSource>();

        public IUnitCatalog Catalog { get; } = Substitute.For<IUnitCatalog>();

        public IJobProgress Progress { get; } = Substitute.For<IJobProgress>();

        public List<SourceDataPoint> Written { get; } = [];

        public List<TimeInterval> Coverage { get; } = [];

        public List<CollectionRequest> Requests { get; } = [];

        public CollectionRunner Runner { get; }

        private static async Task<CollectionResult> HangAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            throw new InvalidOperationException("Unreachable.");
        }
    }
}
