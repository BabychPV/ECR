using Ecr.Application.Sources;
using Ecr.Domain.Entities.External;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Sources;

/// <summary>
/// Згортки за часом: середнє, зважене за часом, і інтеграл (HSE301 F2, §4.1, <c>D-172</c>).
/// </summary>
/// <remarks>
/// ⛔ Еталон згортки — локальний, із сирих точок (V-3 → <c>D-172</c>). Тому
/// числа тут рахуються вручну, а не звіряються з «що дав PI»: розбіжність
/// із серверним summary — привід для банера, а не для зміни еталона.
/// </remarks>
public sealed class PeriodFoldTimeWeightedTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double seconds) => T0.AddSeconds(seconds);

    private static TimedPoint P(double seconds, decimal value, bool isGood = true)
        => new(At(seconds), value, isGood);

    /// <summary>Ряд із DoD: <c>[0 @0 с, 10 @10 с, 10 @20 с]</c>.</summary>
    private static readonly TimedPoint[] Ramp = [P(0, 0m), P(10, 10m), P(20, 10m)];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Лінійний_ряд_інтеграл_це_трапеція_150()
    {
        // 0→10 за 10 с — трапеція 50; далі 10 × 10 с — 100.
        var result = PeriodFold.Fold(AggregationKind.TimeIntegral, Ramp, At(0), At(20), isStep: false);

        Assert.Equal(150m, result.Value);
        Assert.Equal(100m, result.PercentGood);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Лінійний_ряд_середнє_зважене_за_часом_7_5_а_не_просте_6_67()
    {
        // ⚠ Просте середнє трьох точок дало б 20/3 — саме той дефект Д-2.
        var result = PeriodFold.Fold(AggregationKind.TimeWeightedAvg, Ramp, At(0), At(20), isStep: false);

        Assert.Equal(7.5m, result.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Ступінчастий_ряд_тримає_значення_до_наступної_точки_100()
    {
        // 0 тримається 10 с, потім 10 тримається 10 с.
        var integral = PeriodFold.Fold(AggregationKind.TimeIntegral, Ramp, At(0), At(20), isStep: true);
        var average = PeriodFold.Fold(AggregationKind.TimeWeightedAvg, Ramp, At(0), At(20), isStep: true);

        Assert.Equal(100m, integral.Value);
        Assert.Equal(5m, average.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Межа_вікна_інтерполюється_з_точки_ДО_вікна_навіть_без_точок_усередині()
    {
        // ⛔ Коротка подія, у якій стиснення PI не лишило жодної точки: без
        // точки до вікна інтеграл був би нулем, а не об'ємом (§4.1).
        // Пряма 0 @−10 с → 20 @10 с: на межі 0 с значення 10, на 10 с — 20.
        TimedPoint[] series = [P(-10, 0m), P(10, 20m)];

        var integral = PeriodFold.Fold(AggregationKind.TimeIntegral, series, At(0), At(10), isStep: false);
        var average = PeriodFold.Fold(AggregationKind.TimeWeightedAvg, series, At(0), At(10), isStep: false);

        Assert.Equal(150m, integral.Value);
        Assert.Equal(15m, average.Value);
        Assert.Equal(100m, integral.PercentGood);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Кінець_вікна_інтерполюється_з_першої_точки_ПІСЛЯ_вікна()
    {
        // Пряма 10 @0 с → 30 @20 с; на кінці вікна (10 с) значення 20.
        TimedPoint[] series = [P(0, 10m), P(20, 30m)];

        var result = PeriodFold.Fold(AggregationKind.TimeIntegral, series, At(0), At(10), isStep: false);

        Assert.Equal(150m, result.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Прогалина_понад_поріг_не_входить_ні_в_інтеграл_ні_в_знаменник()
    {
        // 10 → 10 за 10 с (100), розрив 90 с, 50 → 50 за 10 с (500).
        // ⚠ Без порогу пряма 10→50 через розрив додала б вигадані 2 700.
        TimedPoint[] series = [P(0, 10m), P(10, 10m), P(100, 50m), P(110, 50m)];
        var maxGap = TimeSpan.FromSeconds(30);

        var integral = PeriodFold.Fold(
            AggregationKind.TimeIntegral, series, At(0), At(110), isStep: false, maxGap);
        var average = PeriodFold.Fold(
            AggregationKind.TimeWeightedAvg, series, At(0), At(110), isStep: false, maxGap);

        Assert.Equal(600m, integral.Value);
        Assert.Equal(30m, average.Value);
        Assert.Equal(20m * 100m / 110m, integral.PercentGood);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Точка_непридатної_якості_робить_прогалиною_відрізки_що_на_неї_спираються()
    {
        // Лінійний: обидва відрізки навколо поганої точки — прогалина.
        // Ступінчастий: лише той, де погана точка ЛІВА (її значення тримається).
        TimedPoint[] series = [P(0, 10m), P(10, 10m), P(20, 999m, isGood: false), P(30, 10m)];

        var linear = PeriodFold.Fold(AggregationKind.TimeIntegral, series, At(0), At(30), isStep: false);
        var step = PeriodFold.Fold(AggregationKind.TimeIntegral, series, At(0), At(30), isStep: true);

        Assert.Equal(100m, linear.Value);
        Assert.Equal(200m, step.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Без_екстраполяції_хвіст_після_останньої_точки_це_прогалина()
    {
        // Дані лише за першу половину вікна: середнє — за покритим часом (10),
        // а не розмазане на все вікно (5); частку каже PercentGood.
        TimedPoint[] series = [P(0, 10m), P(10, 10m)];

        var average = PeriodFold.Fold(AggregationKind.TimeWeightedAvg, series, At(0), At(20), isStep: false);

        Assert.Equal(10m, average.Value);
        Assert.Equal(50m, average.PercentGood);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Без_покриття_значення_null_а_не_нуль()
    {
        // ⛔ «Даних не було» і «інтеграл нуль» — різні стани.
        TimedPoint[] series = [P(30, 5m), P(40, 5m)];

        var result = PeriodFold.Fold(AggregationKind.TimeIntegral, series, At(0), At(20), isStep: false);

        Assert.Null(result.Value);
        Assert.Equal(0m, result.PercentGood);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Інтеграл_у_одиниця_на_секунду_без_перерахунку_в_години()
    {
        // 1 Sm3/h протягом години = 3 600 «Sm3/h × s»; перерахунок у Sm3 — F3.
        TimedPoint[] series = [P(0, 1m), P(3600, 1m)];

        var result = PeriodFold.Fold(AggregationKind.TimeIntegral, series, At(0), At(3600), isStep: false);

        Assert.Equal(3600m, result.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Дробові_значення_й_мілісекунди_рахуються_в_decimal_точно()
    {
        // ⚠ `D-30`: у подвійній точності 0.1 + 0.2 уже не 0.3.
        var t0 = T0;
        TimedPoint[] series =
        [
            new(t0, 0.1m),
            new(t0.AddMilliseconds(500), 0.2m),
            new(t0.AddMilliseconds(1000), 0.3m),
        ];

        var integral = PeriodFold.Fold(
            AggregationKind.TimeIntegral, series, t0, t0.AddSeconds(1), isStep: false);
        var average = PeriodFold.Fold(
            AggregationKind.TimeWeightedAvg, series, t0, t0.AddSeconds(1), isStep: false);

        // 0.5 × (0.1 + 0.2)/2 + 0.5 × (0.2 + 0.3)/2 = 0.075 + 0.125.
        Assert.Equal(0.2m, integral.Value);
        Assert.Equal(0.2m, average.Value);
    }

    [Theory]
    [InlineData(AggregationKind.Sum, 12.0)]
    [InlineData(AggregationKind.Avg, 4.0)]
    [InlineData(AggregationKind.Min, -2.0)]
    [InlineData(AggregationKind.Max, 10.0)]
    [InlineData(AggregationKind.First, 4.0)]
    [InlineData(AggregationKind.Last, -2.0)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Згортки_точок_беруть_лише_точки_вікна_й_дають_те_саме_що_старе_перевантаження(
        AggregationKind kind, double expected)
    {
        // Точки до вікна й на його кінці (виключно) — поза згорткою точок.
        TimedPoint[] series = [P(-10, 1000m), P(0, 4m), P(5, 10m), P(10, -2m), P(20, 1000m)];

        var result = PeriodFold.Fold(kind, series, At(0), At(20), isStep: false);

        Assert.Equal((decimal)expected, result.Value);
        Assert.Equal(PeriodFold.Fold(kind, [4m, 10m, -2m]), result.Value);
        Assert.Null(result.PercentGood);
    }

    [Theory]
    [InlineData(AggregationKind.TimeWeightedAvg)]
    [InlineData(AggregationKind.TimeIntegral)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Старе_перевантаження_без_міток_часу_відмовляє_а_не_рахує_просте_середнє(AggregationKind kind)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PeriodFold.Fold(kind, [0m, 10m, 10m]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Некоректний_вхід_це_відмова()
    {
        Assert.Throws<ArgumentException>(() => PeriodFold.Fold(
            AggregationKind.TimeIntegral, Array.Empty<TimedPoint>(), At(0), At(20), isStep: false));

        // Мітки часу не зростають строго: порядок — частина контракту.
        Assert.Throws<ArgumentException>(() => PeriodFold.Fold(
            AggregationKind.TimeIntegral, [P(10, 1m), P(10, 2m)], At(0), At(20), isStep: false));

        // Вікно перевернуте.
        Assert.Throws<ArgumentException>(() => PeriodFold.Fold(
            AggregationKind.TimeIntegral, Ramp, At(20), At(0), isStep: false));

        Assert.Throws<ArgumentOutOfRangeException>(() => PeriodFold.Fold(
            AggregationKind.TimeIntegral, Ramp, At(0), At(20), isStep: false, TimeSpan.Zero));
    }
}
