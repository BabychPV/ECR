// tests/Ecr.Infrastructure.Tests/Jobs/MaterializeCoverageGapTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// AN-105 / D2-02 (HU-13 Q2, варіант A): матеріалізація скінченого періоду не мовчить
/// про неповне покриття й про відсутність даних.
/// </summary>
/// <remarks>
/// ⚠ Справжній SQL Server (точки — бойовим шляхом збору), патчер і журнал — підробки:
/// перевіряється рішення задачі, а не запис. Пояс проєкту будівника — <c>Asia/Atyrau</c>
/// (UTC+5 без переходів), межі січня рахуються з бази поясів, як у
/// <see cref="MaterializePeriodBoundsTests"/>. Годинник детермінований (<see cref="TestClock"/>).
/// </remarks>
[Collection("SqlServer")]
public sealed class MaterializeCoverageGapTests(SqlServerFixture sql)
{
    /// <summary>Після кінця січня.</summary>
    private static readonly DateTime AfterJanuary = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Усередині січня: період ще відкритий, хвіст без точок природний.</summary>
    private static readonly DateTime MidJanuaryClock = new(2026, 1, 25, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime JanStartUtc = LocalMidnightUtc(new DateTime(2026, 1, 1));
    private static readonly DateTime JanEndUtc = LocalMidnightUtc(new DateTime(2026, 2, 1));

    /// <summary>60 % січня (31 доба = 2 678 400 с; 60 % — рівно 1 607 040 с).</summary>
    private static readonly DateTime SixtyPercent = JanStartUtc.AddSeconds(1_607_040);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D2-02")]
    public async Task Згортка_за_часом_з_покриттям_60_відсотків_після_кінця_періоду_пише_число_і_PartialCoverage()
    {
        var (db, chain, stand) = await ArrangeAsync(AggregationKind.TimeWeightedAvg);
        await using var _ = db;

        // Добрі точки покривають [початок, 60 %]; далі погана точка (збій приладу)
        // робить прогалиною решту місяця аж до першої точки лютого.
        await PointsAsync(db, stand.SourceEntityId,
        [
            (stand.Field, JanStartUtc, 10m, "Good"),
            (stand.Field, SixtyPercent, 10m, "Good"),
            (stand.Field, SixtyPercent.AddSeconds(1), 99m, "Bad"),
            (stand.Field, JanEndUtc, 10m, "Good"),
        ]);

        var patcher = RecordingPatcher();
        var coverage = Substitute.For<ICoverageJournal>();

        await RunAsync(db, chain, stand, patcher, coverage, AfterJanuary);

        // Варіант A: число ЗАПИСАНО (середнє за покритим часом)…
        Assert.Equal(10m, Assert.Single(Written(patcher)).Value);

        // …але не мовчки. ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати гілку `good < MinPercentGood`
        // у `AggregateAsync` → подій немає, червоний.
        var partial = Assert.Single(Events(coverage));
        Assert.Equal(CollectionCoverage.PartialCoverage, partial.Status);
        Assert.Equal(chain.PeriodKey, partial.PeriodKey);
        Assert.True(JobProgressMessageCodec.TryDecode(partial.Details, out var envelope));
        Assert.Equal(CoverageDetails.PartialCoverageKey, envelope.Key);
        Assert.Equal(stand.Field, envelope.Params!["field"]);
        Assert.Equal("60", envelope.Params["percentGood"]);
        Assert.Equal("95", envelope.Params["min"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D2-02")]
    public async Task Відкритий_період_з_хвостом_без_точок_подій_не_дає()
    {
        var (db, chain, stand) = await ArrangeAsync(AggregationKind.TimeWeightedAvg);
        await using var _ = db;

        await PointsAsync(db, stand.SourceEntityId,
        [
            (stand.Field, JanStartUtc, 10m, "Good"),
            (stand.Field, SixtyPercent, 10m, "Good"),
        ]);

        var patcher = RecordingPatcher();
        var coverage = Substitute.For<ICoverageJournal>();

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: `ended = true` без порівняння з кінцем періоду →
        // `PartialCoverage` у відкритому місяці щопрогону, червоний.
        await RunAsync(db, chain, stand, patcher, coverage, MidJanuaryClock);

        Assert.Equal(10m, Assert.Single(Written(patcher)).Value);
        Assert.Empty(Events(coverage));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D2-02")]
    [InlineData(AggregationKind.Sum)]
    [InlineData(AggregationKind.TimeWeightedAvg)]
    public async Task Поле_без_придатних_точок_після_кінця_періоду_дає_SkippedNoData_і_не_чіпає_комірку(AggregationKind kind)
    {
        var (db, chain, stand) = await ArrangeAsync(kind);
        await using var _ = db;

        // Sum: у січні жодної точки (лише лютий). TimeWeightedAvg: точки є, але всі
        // погані — `covered == 0`, згортка повертає `null` (HSE301 §4.1: не 0).
        await PointsAsync(db, stand.SourceEntityId,
            kind == AggregationKind.Sum
                ? [(stand.Field, JanEndUtc.AddHours(1), 5m, "Good")]
                : [
                    (stand.Field, JanStartUtc, 10m, "Bad"),
                    (stand.Field, SixtyPercent, 10m, "Bad"),
                    (stand.Field, JanEndUtc, 10m, "Bad"),
                ]);

        var patcher = RecordingPatcher();
        var coverage = Substitute.For<ICoverageJournal>();

        await RunAsync(db, chain, stand, patcher, coverage, AfterJanuary);

        // Комірку не записано й не очищено: запису немає зовсім.
        Assert.Empty(Written(patcher));

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: повернути голий `continue` у гілках «серія порожня» /
        // `folded.Value is null` → подій немає, червоний.
        var noData = Assert.Single(Events(coverage));
        Assert.Equal(CollectionCoverage.SkippedNoData, noData.Status);
        Assert.True(JobProgressMessageCodec.TryDecode(noData.Details, out var envelope));
        Assert.Equal(CoverageDetails.NoDataKey, envelope.Key);
        Assert.Equal(stand.Field, envelope.Params!["field"]);
    }

    private static DateTime LocalMidnightUtc(DateTime local)
        => TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(local, DateTimeKind.Unspecified),
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Atyrau"));

    private static async Task RunAsync(
        EcrDbContext db, TestDocument chain, Stand stand, ICellPatcher patcher, ICoverageJournal coverage, DateTime now)
    {
        var job = new MaterializeCollectedDataJob(
            db, patcher, coverage, Actor(db), recalculation: null, clock: new TestClock(now));

        await job.ExecuteAsync(
            new MaterializeTask(
                stand.SourceEntityId, chain.ProjectId, chain.DocumentId, chain.TableInstanceId,
                chain.PeriodKey.Value,
                new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 16, 0, 0, 0, DateTimeKind.Utc)),
            Substitute.For<IJobProgress>(),
            CancellationToken.None);
    }

    /// <summary>Ланцюг із відкритим січнем, неактивна сутність і один мапінг поля.</summary>
    private async Task<(EcrDbContext Db, TestDocument Chain, Stand Stand)> ArrangeAsync(AggregationKind kind)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var db = builder.CreateContext();

        var period = await db.Periods.SingleAsync(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == chain.PeriodKey.Value);
        period.TransitionTo(PeriodState.Open, AfterJanuary);
        await db.SaveChangesAsync(CancellationToken.None);

        var tag = Guid.NewGuid().ToString("N")[..8];
        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"), Name("D2-02"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        // ⛔ Неактивна навмисно: активна сутність без завершеного збору робить
        // `SourcesHealthCheck` жовтим для наступних прогонів спільної бази.
        var entity = new SourceEntity(dataSource.Id, $"Ent{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        var field = $"Flare_{tag}";
        var map = EntityFieldMap.ToColumn(entity.Id, field, chain.ColumnDefIds[1]);
        map.SetMaterialization($"R1_{tag}", kind);
        db.EntityFieldMaps.Add(map);
        await db.SaveChangesAsync(CancellationToken.None);

        return (db, chain, new Stand(entity.Id, field));
    }

    /// <summary>Кладе точки бойовим шляхом збору (<c>CollectionStore.UpsertRawPointsAsync</c>).</summary>
    private static async Task PointsAsync(
        EcrDbContext db, int sourceEntityId, IReadOnlyList<(string Field, DateTime At, decimal Value, string Quality)> points)
    {
        var store = new CollectionStore(db, new TestClock(AfterJanuary));
        var runId = await store.StartRunAsync(
            sourceEntityId,
            points.Min(p => p.At),
            points.Max(p => p.At).AddSeconds(1),
            isCatchUp: false,
            triggeredByUserId: null,
            CancellationToken.None);

        await store.UpsertRawPointsAsync(
            runId,
            sourceEntityId,
            [.. points.Select(p => new SourceDataPoint(p.Field, p.At, p.Value, null, null, p.Quality))],
            CancellationToken.None);
    }

    private static IntegrationActor Actor(EcrDbContext db) => new(db, new Ecr.Application.Common.JobActorScope());

    private static ICellPatcher RecordingPatcher()
    {
        var patcher = Substitute.For<ICellPatcher>();
        patcher
            .ApplyIntegrationAsync(
                Arg.Any<long>(), Arg.Any<long>(), Arg.Any<PeriodKey>(),
                Arg.Any<IReadOnlyList<IntegrationCellValue>>(), Arg.Any<CancellationToken>())
            .Returns(call => new IntegrationWriteResult(((IReadOnlyList<IntegrationCellValue>)call[3]).Count, []));
        return patcher;
    }

    private static List<IntegrationCellValue> Written(ICellPatcher patcher)
        => [.. patcher.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICellPatcher.ApplyIntegrationAsync))
            .SelectMany(c => (IReadOnlyList<IntegrationCellValue>)c.GetArguments()[3]!)];

    private static List<CoverageEvent> Events(ICoverageJournal coverage)
        => [.. coverage.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICoverageJournal.RecordManyAsync))
            .SelectMany(c => (IReadOnlyList<CoverageEvent>)c.GetArguments()[0]!)];

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Сутність і її єдине поле.</summary>
    private sealed record Stand(int SourceEntityId, string Field);
}
