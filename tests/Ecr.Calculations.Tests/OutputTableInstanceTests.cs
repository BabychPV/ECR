// tests/Ecr.Calculations.Tests/OutputTableInstanceTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// D-401: результат рядка несе екземпляр таблиці входу (<c>CalculationInput.TableInstanceId</c>), а не відкидає його.
/// </summary>
/// <remarks>
/// ⛔ Мутаційний доказ: прибрати останній аргумент у <c>new CalculationOutput(...)</c> в
/// <c>GenericCalculationModule</c> — <see cref="Вихід_несе_екземпляр_таблиці_входу"/> червоний.
/// </remarks>
public sealed class OutputTableInstanceTests
{
    private const int VersionId = 62;
    private const int KilogramUnit = 1;
    private const long DocumentId = 701;

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData(500L, 500L)]
    [InlineData(0L, null)]
    public async Task Вихід_несе_екземпляр_таблиці_входу(long inputTable, long? expected)
    {
        var module = new GenericCalculationModule(
            new RealFormulaEngine(),
            Methodologies(),
            new ConstantResolver(Substitute.For<IConstantStore>()),
            new CalendarContext(),
            Units(),
            Periods(),
            Substitute.For<ICalculationBindingStore>());

        var descriptor = new MethodologyDescriptor(
            MethodologyId: 6,
            MethodologyVersionId: VersionId,
            Code: "FLARE",
            VersionNumber: "1.0.0.0",
            Level: CalculationLevel.Configuration,
            NumericMode: NumericMode.Legacy,
            CalendarMode: CalendarMode.Actual,
            TraceLevel: TraceLevel.Off);

        var input = new CalculationInput(
            descriptor,
            DocumentId,
            TableInstanceId: inputTable,
            PeriodKey: new PeriodKey(202601),
            SourceRowKey: "M01",
            Arguments: [new CalculationArgument("Jan", 2m, null, null)]);

        var output = await module.ExecuteAsync(input, CancellationToken.None);

        Assert.Equal(expected, output.TableInstanceId);
        Assert.Equal("M01", output.SourceRowKey);
    }

    private static IMethodologyStore Methodologies()
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create("Total"), "@jan * 2");
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
