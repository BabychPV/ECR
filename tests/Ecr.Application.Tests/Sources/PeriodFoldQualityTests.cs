using Ecr.Application.Ports;
using Ecr.Application.Sources;
using Ecr.Domain.Entities.External;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Sources;

/// <summary>
/// Згортки точок відкидають точки якості ≠ Good, а їхній інтервал іде в Gaps
/// (HSE301 §4.6). Причина: Bad-точка 9999 потрапила в Fuel=10016.5.
/// </summary>
/// <remarks>
/// ⛔ Поведінку змінено свідомо: до цього згортки точок якості не бачили.
/// Дані детерміновані, годинника немає.
/// </remarks>
public sealed class PeriodFoldQualityTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static TimedPoint P(int hour, decimal value, bool isGood = true)
        => new(T0.AddHours(hour), value, isGood);

    /// <summary>Вікно 10 год; Bad-точка 9999 на 3-й годині, до наступної точки (4-та) — 1 год.</summary>
    private static readonly TimedPoint[] WithBad =
        [P(0, 1m), P(2, 2m), P(3, 9999m, isGood: false), P(4, 3m), P(6, 4m)];

    private static readonly TimedPoint[] AllGood =
        [P(0, 1m), P(2, 2m), P(3, 9999m), P(4, 3m), P(6, 4m)];

    private static TimeFoldResult Run(AggregationKind kind, IReadOnlyList<TimedPoint> series)
        => PeriodFold.Fold(kind, series, T0, T0.AddHours(10), isStep: false);

    [Theory]
    [InlineData(AggregationKind.Sum, 10)]
    [InlineData(AggregationKind.Avg, 2.5)]
    [InlineData(AggregationKind.Min, 1)]
    [InlineData(AggregationKind.Max, 4)]
    [InlineData(AggregationKind.First, 1)]
    [InlineData(AggregationKind.Last, 4)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Bad_точка_не_входить_у_згортку_точок(AggregationKind kind, double expected)
    {
        Assert.Equal((decimal)expected, Run(kind, WithBad).Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Інтервал_Bad_точки_іде_в_Gaps_а_покриття_менше_100()
    {
        var result = Run(AggregationKind.Sum, WithBad);

        var gap = Assert.Single(result.Gaps!);
        Assert.Equal(new TimeInterval(T0.AddHours(3), T0.AddHours(4)), gap);
        Assert.Equal(90m, result.PercentGood);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Остання_Bad_точка_тягне_Gaps_до_кінця_вікна()
    {
        var result = Run(AggregationKind.Sum, [P(0, 1m), P(8, 9999m, isGood: false)]);

        Assert.Equal(1m, result.Value);
        Assert.Equal(new TimeInterval(T0.AddHours(8), T0.AddHours(10)), Assert.Single(result.Gaps!));
        Assert.Equal(80m, result.PercentGood);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Усі_Good_числа_як_раніше_без_Gaps_і_покриття()
    {
        var result = Run(AggregationKind.Sum, AllGood);

        Assert.Equal(10009m, result.Value);
        Assert.Empty(result.Gaps!);
        Assert.Null(result.PercentGood);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Усі_точки_Bad_значення_немає()
    {
        var result = Run(AggregationKind.Sum, [P(1, 9999m, isGood: false), P(2, 9999m, isGood: false)]);

        Assert.Null(result.Value);
        Assert.Equal(2, result.Gaps!.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void TimeWeightedAvg_без_змін_Bad_робить_прогалиною_відрізок()
    {
        // 0 @0, 10 @10, 10 @20 (як у PeriodFoldTimeWeightedTests): Bad посередині — лише лівий відрізок прогалина.
        var ramp = new[]
        {
            new TimedPoint(T0, 0m), new TimedPoint(T0.AddSeconds(10), 10m), new TimedPoint(T0.AddSeconds(20), 10m),
        };

        var result = PeriodFold.Fold(AggregationKind.TimeWeightedAvg, ramp, T0, T0.AddSeconds(20), isStep: false);

        Assert.Equal(7.5m, result.Value);
        Assert.Equal(100m, result.PercentGood);
        Assert.Null(result.Gaps);
    }
}
