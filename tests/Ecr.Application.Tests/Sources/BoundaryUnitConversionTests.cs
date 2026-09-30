using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Sources;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Sources;

/// <summary>
/// Конверсія одиниць на межі після згортки (HSE301 F3, §4.2, <c>D-173</c>, ФВ-16.10).
/// </summary>
/// <remarks>
/// ⚠ Довідник тут — дзеркало F1 (<c>09-seed.sql</c>, секція <c>HSE301:F1</c>):
/// ті самі розмірності й ті самі множники, включно з
/// <c>Sm3_per_h = 0.000277777777777778</c>, який зберігає <c>decimal(38,18)</c>.
/// </remarks>
public sealed class BoundaryUnitConversionTests
{
    private const int SecondId = 1;
    private const int HourId = 2;
    private const int KilogramId = 3;
    private const int TonneId = 4;
    private const int CubicMetreId = 5;
    private const int StdCubicMetreId = 10;
    private const int StdCubicMetrePerHourId = 11;
    private const int StdCubicMetrePerSecondId = 12;

    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static UnitCatalogSnapshot Catalog() => new(
        new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
        {
            ["s"] = new(SecondId, "s", DimensionId: 4, FactorToBase: 1m),
            ["h"] = new(HourId, "h", DimensionId: 4, FactorToBase: 3600m),
            ["kg"] = new(KilogramId, "kg", DimensionId: 1, FactorToBase: 1m),
            ["t"] = new(TonneId, "t", DimensionId: 1, FactorToBase: 1000m),
            ["m3"] = new(CubicMetreId, "m3", DimensionId: 2, FactorToBase: 1m),
            ["Sm3"] = new(StdCubicMetreId, "Sm3", DimensionId: 12, FactorToBase: 1m),
            ["Sm3_per_h"] = new(StdCubicMetrePerHourId, "Sm3_per_h", DimensionId: 13, FactorToBase: 0.000277777777777778m),
            ["Sm3_per_s"] = new(StdCubicMetrePerSecondId, "Sm3_per_s", DimensionId: 13, FactorToBase: 1m),
        },
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [$"{StdCubicMetreId}|{HourId}"] = StdCubicMetrePerHourId,
            [$"{StdCubicMetreId}|{SecondId}"] = StdCubicMetrePerSecondId,
        });

    /// <summary>Постійна витрата на вікні <c>[0, seconds)</c>: дві точки на краях.</summary>
    private static decimal IntegralOfConstant(decimal rate, double seconds)
        => PeriodFold.Fold(
                AggregationKind.TimeIntegral,
                [new TimedPoint(T0, rate), new TimedPoint(T0.AddSeconds(seconds), rate)],
                T0,
                T0.AddSeconds(seconds),
                isStep: false)
            .Value!.Value;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-16.10")]
    public void Інтеграл_Sm3_per_h_за_930_с_лягає_в_Sm3_рівно_0_93()
    {
        var integral = IntegralOfConstant(3.6m, 930);
        Assert.Equal(3348m, integral);

        var result = BoundaryUnitConversion.ConvertFolded(
            AggregationKind.TimeIntegral, integral, StdCubicMetrePerHourId, StdCubicMetreId, Catalog());

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: перерахунок через `FactorToBase` швидкості
        // (× 0.000277777777777778) замість ділення на знаменник (÷ 3600) дає
        // 0.930000000000000744 — тест червоний.
        Assert.Equal(0.93m, result.Value);
        Assert.True(result.IsConverted);
        Assert.Equal(3600m, result.SecondsPerDenominator);
        Assert.Contains("Sm3_per_h×s→Sm3 ÷3600 (s/h)", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-16.10")]
    public void Місячний_Total_Sm3_per_h_лягає_в_Sm3_без_хвоста()
    {
        // Січень: 31 доба = 2 678 400 с; 3.6 Sm3/h × 744 год = 2 678.4 Sm3.
        var integral = IntegralOfConstant(3.6m, 31 * 86_400);

        var result = BoundaryUnitConversion.ConvertFolded(
            AggregationKind.TimeIntegral, integral, StdCubicMetrePerHourId, StdCubicMetreId, Catalog());

        Assert.Equal(2678.4m, result.Value);

        // Контроль: маршрут через базову одиницю справді не точний — інакше
        // тест вище нічого б не доводив.
        Assert.NotEqual(2678.4m, integral * 0.000277777777777778m);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-16.10")]
    public void Інтеграл_Sm3_per_s_ділиться_на_одиницю()
    {
        var result = BoundaryUnitConversion.ConvertFolded(
            AggregationKind.TimeIntegral, 42m, StdCubicMetrePerSecondId, StdCubicMetreId, Catalog());

        Assert.Equal(42m, result.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-16.10")]
    public void Інтеграл_Sm3_в_робочі_m3_відмова_а_не_число()
    {
        // ⛔ V-12: стандартний і робочий кубометр множником не перераховуються.
        var error = Assert.Throws<DomainException>(
            () => BoundaryUnitConversion.ConvertFolded(
                AggregationKind.TimeIntegral, 3348m, StdCubicMetrePerHourId, CubicMetreId, Catalog()));

        Assert.Equal("ECR-UOM-0422", error.ErrorCode);
        Assert.Equal("err.ECR-UOM-0422.incompatibleDimensions", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-16.10")]
    public void Середнє_Sm3_в_m3_відмова_а_не_тихе_число()
    {
        var error = Assert.Throws<DomainException>(
            () => BoundaryUnitConversion.ConvertFolded(
                AggregationKind.Avg, 5m, StdCubicMetreId, CubicMetreId, Catalog()));

        Assert.Equal("ECR-UOM-0422", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-16.10")]
    public void Інтеграл_без_оголошених_одиниць_відмова()
    {
        // Без одиниць число лишилося б в «одиниця × секунда» — у 3600 разів
        // більше за об'єм, якщо джерело міряє за годину.
        var error = Assert.Throws<BusinessRuleException>(
            () => BoundaryUnitConversion.ConvertFolded(
                AggregationKind.TimeIntegral, 3348m, null, StdCubicMetreId, Catalog()));

        Assert.Equal("ECR-UOM-0422", error.ErrorCode);
        Assert.Equal("err.ECR-UOM-0422.integralUnitsUndeclared", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-16.10")]
    public void Інтеграл_величини_що_не_є_швидкістю_відмова()
    {
        var error = Assert.Throws<BusinessRuleException>(
            () => BoundaryUnitConversion.ConvertFolded(
                AggregationKind.TimeIntegral, 3348m, StdCubicMetreId, StdCubicMetreId, Catalog()));

        Assert.Equal("err.ECR-UOM-0422.integralSourceNotRate", error.Details!["messageKey"]);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-16.12")]
    [InlineData(null, null)]
    [InlineData(KilogramId, null)]
    [InlineData(null, TonneId)]
    [InlineData(KilogramId, KilogramId)]
    public void Згортка_точок_без_двох_різних_одиниць_побітно_та_сама(int? source, int? target)
    {
        // Avg старих мапінгів — те саме число до останнього біта, що й до F3.
        var folded = PeriodFold.Fold(AggregationKind.Avg, [1m, 2m, 2m]);

        var result = BoundaryUnitConversion.ConvertFolded(AggregationKind.Avg, folded, source, target, Catalog());

        Assert.Equal(decimal.GetBits(folded), decimal.GetBits(result.Value));
        Assert.False(result.IsConverted);
        Assert.Equal(string.Empty, result.Describe());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-16.10")]
    public void Середнє_за_часом_kg_в_t_конвертується_й_описується_множником()
    {
        var result = BoundaryUnitConversion.ConvertFolded(
            AggregationKind.TimeWeightedAvg, 2500m, KilogramId, TonneId, Catalog());

        Assert.Equal(2.5m, result.Value);
        Assert.Equal(0.001m, result.Factor);
        Assert.Equal("kg→t ×0.001", result.Describe());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-16.10")]
    [Trait("Requirement", "ФВ-13.14")]
    public void Перегляд_мапінгу_показує_інтеграл_у_цільовій_одиниці_тим_самим_шляхом()
    {
        var to = T0.AddSeconds(930);
        var data = PreviewData(
            Map(1, "FLOW", "TimeIntegral", "Sm3_per_h", "Sm3"),
            Point("FLOW", T0, 3.6m),
            Point("FLOW", to.AddTicks(-TimeSpan.TicksPerSecond), 3.6m));

        // ⚠ Вікно перегляду — [T0, T0+930 с), точки — на T0 і T0+929 с: сховище
        // перегляду межових точок не має, край після останньої — прогалина.
        var withCatalog = Assert.Single(PreviewMappingHandler.Compose(data, T0, to, Catalog()).Fields);
        Assert.Equal(3.6m * 929m / 3600m, withCatalog.FoldedValue);

        // ⛔ Без довідника інтеграл не показується: «Sm3/h × с» у колонці Sm3 —
        // саме те хибне число, якого перегляд має не допустити.
        var withoutCatalog = Assert.Single(PreviewMappingHandler.Compose(data, T0, to).Fields);
        Assert.Null(withoutCatalog.FoldedValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-13.14")]
    public void Перегляд_мапінгу_рахує_середнє_за_часом_а_не_просте()
    {
        // МУТАЦІЙНИЙ ДОКАЗ: повернути в перегляд стару `Fold(kind, decimals)`
        // без часу → для `TimeWeightedAvg` вона відмовляє, тест червоний.
        var data = PreviewData(
            Map(1, "RAMP", "TimeWeightedAvg", null, null),
            Point("RAMP", T0, 0m),
            Point("RAMP", T0.AddSeconds(10), 10m),
            Point("RAMP", T0.AddSeconds(20), 10m));

        var field = Assert.Single(PreviewMappingHandler.Compose(data, T0, T0.AddSeconds(20)).Fields);

        Assert.Equal(7.5m, field.FoldedValue);
        Assert.Equal(3, field.PointCount);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-16.10")]
    public void Перегляд_мапінгу_з_несумісними_одиницями_не_показує_числа()
    {
        var data = PreviewData(
            Map(1, "VOL", "Avg", "Sm3", "m3"),
            Point("VOL", T0, 5m));

        var field = Assert.Single(PreviewMappingHandler.Compose(data, T0, T0.AddHours(1), Catalog()).Fields);

        Assert.Null(field.FoldedValue);
        Assert.Equal(1, field.PointCount);
    }

    private static MappingPreviewData PreviewData(FieldMapRef map, params RawPointRef[] points)
        => new(new SourceEntityRef(1, "FLARE_01", null), [map], points, false, []);

    private static FieldMapRef Map(int id, string field, string aggregation, string? sourceUnit, string? targetUnit)
        => new(id, field, "R1", 100, "C1", TargetColumnExists: true, aggregation, sourceUnit, targetUnit, IsActive: true);

    private static RawPointRef Point(string path, DateTime at, decimal value)
        => new(path, at, value, null, "Good");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-16.10")]
    public void Одиниця_поза_довідником_відмова_ECR_INT_0422()
    {
        var error = Assert.Throws<BusinessRuleException>(
            () => BoundaryUnitConversion.Convert(1m, 99, KilogramId, Catalog()));

        Assert.Equal("ECR-INT-0422", error.ErrorCode);
        Assert.Equal("err.ECR-INT-0422.unitMissingFromSnapshot", error.Details!["messageKey"]);
    }
}
