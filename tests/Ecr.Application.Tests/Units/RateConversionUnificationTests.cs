// tests/Ecr.Application.Tests/Units/RateConversionUnificationTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Application.Units;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Units;

/// <summary>
/// Z2-05: <c>CONVERT</c> швидкості у швидкість - одна арифметика (доменний <c>UnitConverter</c> через
/// <c>BoundaryUnitConversion</c>) в <c>POST /units/convert</c> і в <c>CONVERT</c> формул шаблону, а не копія формули
/// через заокруглений <c>FactorToBase</c>.
/// </summary>
/// <remarks>
/// Мутація: повернути в <c>ConvertUnitHandler</c> / <c>SliceEvaluationContext</c> множення на
/// <c>FactorToBase</c> швидкості - <c>1 Sm3/s</c> стає <c>3599.99999999999712</c>, обидва тести червоні.
/// </remarks>
public sealed class RateConversionUnificationTests
{
    private const byte StdVolume = 12;
    private const byte Time = 4;
    private const byte StdFlow = 13;

    private static readonly UnitCatalogSnapshot Catalog = new(
        new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
        {
            ["Sm3"] = new UnitRef(1, "Sm3", StdVolume, 1m),
            ["s"] = new UnitRef(2, "s", Time, 1m),
            ["h"] = new UnitRef(3, "h", Time, 3600m),
            ["Sm3_per_s"] = new UnitRef(4, "Sm3_per_s", StdFlow, 1m),
            ["Sm3_per_h"] = new UnitRef(5, "Sm3_per_h", StdFlow, 0.000277777777777778m),
        },
        new Dictionary<string, int>(StringComparer.Ordinal) { ["1|2"] = 4, ["1|3"] = 5 });

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "Z2-05")]
    public async Task Z2_05_POST_units_convert_швидкість_у_швидкість_рівно()
    {
        var catalog = Substitute.For<IUnitCatalog>();
        catalog.GetAsync(Arg.Any<CancellationToken>()).Returns(Catalog);
        var handler = new ConvertUnitHandler(catalog);

        Assert.Equal(3600m, await handler.HandleAsync(1m, "Sm3_per_s", "Sm3_per_h", default));
        Assert.Equal(0.001m, await handler.HandleAsync(3.6m, "Sm3_per_h", "Sm3_per_s", default));

        // Відмова лишилася BusinessRuleException з тим самим кодом і ключем (потік - не об'єм).
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync(1m, "Sm3_per_h", "Sm3", default));
        Assert.Equal("ECR-UOM-0422", error.ErrorCode);
        Assert.Equal("err.ECR-UOM-0422.incompatibleDimensions", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "Z2-05")]
    public void Z2_05_CONVERT_у_формулі_шаблону_швидкість_у_швидкість_рівно()
    {
        var snapshot = new TemplateVersionSnapshot(
            1, 1, [], new Dictionary<int, ColumnDef>(), new Dictionary<(int TableDefId, string RowKey), RowDef>());
        var context = new SliceEvaluationContext(
            snapshot,
            new Dictionary<CellKey, ExpressionValue>(),
            new Dictionary<string, ExpressionValue>(),
            new PeriodContext(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), CalendarMode.Actual, 2026, 1),
            Catalog,
            new Dictionary<int, IReadOnlyDictionary<string, long>>());

        Assert.Equal(3600m, context.Convert(ExpressionValue.Number(1m), "Sm3_per_s", "Sm3_per_h").AsNumber());
        Assert.Equal(0.001m, context.Convert(ExpressionValue.Number(3.6m), "Sm3_per_h", "Sm3_per_s").AsNumber());
        Assert.Equal(
            ExpressionErrors.BadUnit, context.Convert(ExpressionValue.Number(1m), "Sm3_per_h", "Sm3").ErrorCode);
    }
}
