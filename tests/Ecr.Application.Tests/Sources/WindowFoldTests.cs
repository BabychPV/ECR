using Ecr.Application.Ports;
using Ecr.Application.Sources;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Sources;

/// <summary>
/// Локальна згортка вікна — типова реалізація <see cref="IExternalDataSource.ReadWindowAsync"/>
/// (HSE301 F4, §4.3, §4.6, <c>D-172</c>).
/// </summary>
/// <remarks>
/// ⛔ Підставне джерело віддає ЛИШЕ точки з запитаного діапазону, як справжнє.
/// Інакше тест «точка до вікна» був би зеленим і без запасу на межі.
/// </remarks>
public sealed class WindowFoldTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double seconds) => T0.AddSeconds(seconds);

    private static SourceDataPoint Pt(double seconds, decimal? value, string? quality = "Good", string? text = null)
        => new("EL|Flow", At(seconds), value, text, "Sm3/h", quality);

    private static WindowRequest Window(
        double from, double to, SourceSummaryKind summary, bool isStep = false, TimeSpan? maxGap = null)
        => new(1, 7, "EL|Flow", At(from), At(to), summary, isStep, maxGap);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Подія_без_точок_усередині_інтерполюється_з_точок_до_і_після_вікна()
    {
        // ⛔ Мутаційний доказ DoD: типова реалізація без точки ДО вікна дає тут
        // null — ліва межа не має з чого інтерполюватися, вікно стає прогалиною.
        IExternalDataSource source = new FakeSource(Pt(-100, 10m), Pt(200, 10m));

        var result = await source.ReadWindowAsync(Window(0, 60, SourceSummaryKind.Total), CancellationToken.None);

        Assert.Equal(600m, result.Value);
        Assert.Equal(100m, result.PercentGood);
        Assert.Equal(0, result.PointCount);
        Assert.Empty(result.Gaps);
        Assert.Equal(WindowComputedBy.Local, result.ComputedBy);
        Assert.Equal("Sm3/h", result.SourceUnitSymbol);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Типова_реалізація_запитує_діапазон_раніше_за_вікно()
    {
        var source = new FakeSource(Pt(-100, 10m), Pt(200, 10m));

        await ((IExternalDataSource)source).ReadWindowAsync(
            Window(0, 60, SourceSummaryKind.Average), CancellationToken.None);

        var first = Assert.Single(source.Requests);
        Assert.Equal(At(0) - WindowFold.DefaultBoundarySearch, first.FromUtc);
        Assert.Equal(At(60) + WindowFold.DefaultBoundarySearch, first.ToUtc);
        Assert.Equal(SourceQueryKind.Raw, first.Kind);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData(SourceSummaryKind.Total, false)]
    [InlineData(SourceSummaryKind.Total, true)]
    [InlineData(SourceSummaryKind.Average, false)]
    [InlineData(SourceSummaryKind.Average, true)]
    public async Task Локальне_вікно_дорівнює_PeriodFold_на_тих_самих_точках(SourceSummaryKind summary, bool isStep)
    {
        SourceDataPoint[] raw = [Pt(-5, 2m), Pt(3, 4m), Pt(7, 8m), Pt(12, 6m), Pt(25, 1m)];
        TimedPoint[] timed = [.. raw.Select(p => new TimedPoint(p.Timestamp, p.ValueNumeric!.Value))];
        var kind = summary == SourceSummaryKind.Total ? AggregationKind.TimeIntegral : AggregationKind.TimeWeightedAvg;

        var expected = PeriodFold.Fold(kind, timed, At(0), At(20), isStep);
        var result = await ((IExternalDataSource)new FakeSource(raw)).ReadWindowAsync(
            Window(0, 20, summary, isStep), CancellationToken.None);

        Assert.Equal(expected.Value, result.Value);
        Assert.Equal(expected.PercentGood, result.PercentGood);
        Assert.Equal(3, result.PointCount);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Точка_з_якістю_Bad_не_входить_у_згортку_а_її_відрізок_іде_в_прогалини()
    {
        // Ступінчастий: 10 тримається [0,10), Bad — [10,20) прогалина.
        SourceDataPoint[] raw = [Pt(0, 10m), Pt(10, 1000m, "Bad"), Pt(20, 10m)];

        var total = WindowFold.Fold(Window(0, 20, SourceSummaryKind.Total, isStep: true), raw, [], null);
        var max = WindowFold.Fold(Window(0, 20, SourceSummaryKind.Maximum, isStep: true), raw, [], null);
        var count = WindowFold.Fold(Window(0, 20, SourceSummaryKind.Count, isStep: true), raw, [], null);

        Assert.Equal(100m, total.Value);
        Assert.Equal(50m, total.PercentGood);
        Assert.Equal(new[] { new TimeInterval(At(10), At(20)) }, total.Gaps);

        // ⛔ Без відсіву поганих Maximum дав би 1000 — число з точки, якій джерело не вірить.
        Assert.Equal(10m, max.Value);
        Assert.Equal(1m, count.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Системний_стан_PI_текстом_непридатний_навіть_з_якістю_Good()
    {
        var state = Pt(10, null, "Good", "I/O Timeout");

        Assert.False(WindowFold.IsGood(state));
        Assert.False(WindowFold.IsGood(Pt(10, 5m, "Questionable")));
        Assert.True(WindowFold.IsGood(Pt(10, 5m, "good")));
        Assert.True(WindowFold.IsGood(Pt(10, 5m, quality: null)));

        var result = WindowFold.Fold(
            Window(0, 20, SourceSummaryKind.Average), [Pt(0, 4m), state, Pt(20, 4m)], [], null);

        // Лінійний: обидва відрізки спираються на непридатну точку — покриття нуль.
        Assert.Null(result.Value);
        Assert.Equal(0m, result.PercentGood);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, 6.0)]
    public void Прогалини_в_сумі_дорівнюють_непокритій_частці_PeriodFold(bool isStep, double? maxGapSeconds)
    {
        SourceDataPoint[] raw =
            [Pt(2, 1m), Pt(5, 3m, "Bad"), Pt(9, 2m), Pt(17, 5m), Pt(19, 5m), Pt(33, 1m)];
        var maxGap = maxGapSeconds is { } s ? TimeSpan.FromSeconds(s) : (TimeSpan?)null;

        var result = WindowFold.Fold(Window(0, 30, SourceSummaryKind.Total, isStep, maxGap), raw, [], null);

        var gapSeconds = result.Gaps.Sum(g => (decimal)(g.ToUtc - g.FromUtc).TotalSeconds);

        // Точність 10 знаків: частка покриття — неперіодичний дріб (2/30).
        Assert.Equal(30m * (100m - result.PercentGood!.Value) / 100m, gapSeconds, 10);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Обрізаний_батч_дочитується_до_сусіда_правої_межі()
    {
        // 12 021 точка щосекунди — більше за два батчі по MaxPointsPerRead.
        var raw = Enumerable.Range(-10, 12_021).Select(s => Pt(s, 1m)).ToArray();
        var source = new FakeSource(raw);

        var result = await ((IExternalDataSource)source).ReadWindowAsync(
            Window(0, 12_000, SourceSummaryKind.Total), CancellationToken.None);

        Assert.Equal(12_000m, result.Value);
        Assert.Equal(100m, result.PercentGood);
        Assert.Equal(3, source.Requests.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Відмова_джерела_дає_код_і_прогалину_на_все_вікно_а_не_нуль()
    {
        var source = new FakeSource { ErrorCode = "ECR-INT-0503" };

        var result = await ((IExternalDataSource)source).ReadWindowAsync(
            Window(0, 60, SourceSummaryKind.Total), CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal("ECR-INT-0503", result.ErrorCode);
        Assert.Equal(new[] { new TimeInterval(At(0), At(60)) }, result.Gaps);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Дві_точки_з_однаковою_міткою_лишається_пізніше_прочитана()
    {
        var result = WindowFold.Fold(
            Window(0, 10, SourceSummaryKind.Average, isStep: true),
            [Pt(0, 1m), Pt(0, 3m), Pt(10, 3m)],
            [],
            null);

        Assert.Equal(3m, result.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Перевернуте_вікно_відхиляється_до_читання_джерела()
    {
        var source = new FakeSource();

        await Assert.ThrowsAsync<ArgumentException>(() => ((IExternalDataSource)source).ReadWindowAsync(
            Window(60, 0, SourceSummaryKind.Total), CancellationToken.None));
        Assert.Empty(source.Requests);
    }

    /// <summary>Джерело, що віддає точки лише із запитаного діапазону, зі стелею батча.</summary>
    private sealed class FakeSource(params SourceDataPoint[] points) : IExternalDataSource
    {
        public List<CollectionRequest> Requests { get; } = [];

        public string? ErrorCode { get; init; }

        public ExternalTransport Transport => ExternalTransport.PiSqlClient;

        public Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(int dataSourceId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct)
        {
            Requests.Add(request);

            if (ErrorCode is not null)
            {
                return Task.FromResult(new CollectionResult(
                    [], [new TimeInterval(request.FromUtc, request.ToUtc)], ErrorCode));
            }

            var batch = points
                .Where(p => p.Timestamp >= request.FromUtc && p.Timestamp < request.ToUtc)
                .OrderBy(p => p.Timestamp)
                .Take(request.MaxPoints)
                .ToList();
            var truncated = batch.Count > 0 && batch.Count >= request.MaxPoints;

            return Task.FromResult(new CollectionResult(
                batch, truncated ? [new TimeInterval(batch[^1].Timestamp, request.ToUtc)] : [], null));
        }
    }
}
