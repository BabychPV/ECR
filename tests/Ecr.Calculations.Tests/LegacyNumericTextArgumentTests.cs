// tests/Ecr.Calculations.Tests/LegacyNumericTextArgumentTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Аудит 2026-10-09c, C1-05: у <c>Legacy</c> текстовий аргумент, що є числом, — число.
/// </summary>
/// <remarks>
/// ⛔ Що було. Чинна збірка подає параметром <c>double</c> будь-який рядок, що
/// розібрався <c>double.TryParse(InvariantCulture)</c> (<c>NCalcLegacyTests</c>,
/// <c>Utilities.cs:188-215</c>). ECR подавав <c>Text("1")</c>, і <c>@Cat = 1</c> та
/// <c>in(@Cat, 1, 2)</c> мовчки давали FALSE — <c>if</c> ішов в іншу гілку без
/// жодної помилки. <c>Strict</c> лишається як є: текст — це текст.
/// </remarks>
public sealed class LegacyNumericTextArgumentTests
{
    private const int VersionId = 62;
    private const int KilogramUnit = 1;
    private const long DocumentId = 700;

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData(NumericMode.Legacy, "if(@Cat = 1, 10, 20)", "1", 10)]
    [InlineData(NumericMode.Legacy, "if(in(@Cat, 1, 2), 10, 20)", "2", 10)]
    [InlineData(NumericMode.Legacy, "if(@Cat = 1, 10, 20)", "1.0", 10)]
    [InlineData(NumericMode.Legacy, "if(@Cat = 1, 10, 20)", "A1", 20)]
    [InlineData(NumericMode.Strict, "if(@Cat = 1, 10, 20)", "1", 20)]
    [InlineData(NumericMode.Legacy, "if(@Cat = '1', 10, 20)", "1", 10)]
    [InlineData(NumericMode.Strict, "if(@Cat = '1', 10, 20)", "1", 10)]
    public async Task Текст_число_в_Legacy_дорівнює_числу(
        NumericMode mode, string expression, string text, int expected)
    {
        var module = new GenericCalculationModule(
            new RealFormulaEngine(),
            Methodologies(expression),
            new ConstantResolver(Substitute.For<IConstantStore>()),
            new CalendarContext(),
            Units(),
            Periods(),
            Substitute.For<ICalculationBindingStore>());

        var descriptor = new MethodologyDescriptor(
            MethodologyId: 6,
            MethodologyVersionId: VersionId,
            Code: "CAT",
            VersionNumber: "1.0.0.0",
            Level: CalculationLevel.Configuration,
            NumericMode: mode,
            CalendarMode: CalendarMode.Actual,
            TraceLevel: TraceLevel.Off);

        var input = new CalculationInput(
            descriptor,
            DocumentId,
            TableInstanceId: 500,
            PeriodKey: new PeriodKey(202601),
            SourceRowKey: "7001001",
            Arguments: [new CalculationArgument("Cat", null, text, null)]);

        var output = await module.ExecuteAsync(input, CancellationToken.None);

        Assert.Equal((decimal)expected, Assert.Single(output.Values).Value);
    }

    private static IMethodologyStore Methodologies(string expression)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create("Total"), expression);
        formula.SetEvaluationOrder(1);

        var store = Substitute.For<IMethodologyStore>();
        store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns([formula]);
        store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns([new MethodologyOutput(VersionId, EcrCode.Create("Total"), KilogramUnit)]);
        store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>()).Returns([]);

        return store;
    }

    private static IPeriodStore Periods()
    {
        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, 202601, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        return periods;
    }

    private static IUnitCatalog Units()
    {
        var catalog = Substitute.For<IUnitCatalog>();
        catalog.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
            {
                ["kg"] = new(KilogramUnit, "kg", 1, 1m),
            },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        return catalog;
    }
}
