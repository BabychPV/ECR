// tests/Ecr.Calculations.Tests/FlareEventArgumentsTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// AN-5: методологія з <c>@Total</c>, <c>@Duration</c>, <c>@FlareUnitMode</c> рахує з події факела.
/// </summary>
/// <remarks>
/// ⛔ Правило подачі — дослівно з чинного модуля (<c>R_Air_HSE30X_Cont_General.cs:116–139</c>):
/// HP → 1, LP → 2, інше → 0; <c>Duration</c> = 86400 завжди (не End − Start).
/// </remarks>
public sealed class FlareEventArgumentsTests
{
    private const int VersionId = 71;
    private const int KilogramUnit = 1;
    private const long DocumentId = 800;

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("HP", 1)]
    [InlineData("LP", 2)]
    [InlineData("Other", 0)]
    [InlineData(null, 0)]
    public void Режим_факела_береться_з_типу_тиску(string? pressureType, int expected)
    {
        var args = FlareEventArguments.Build(5m, pressureType, "Purge", "F1");

        Assert.Equal(expected, Assert.Single(args, a => a.ArgumentCode == "FlareUnitMode").Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Тривалість_завжди_доба_а_пілот_і_категорія_з_атрибутів()
    {
        var args = FlareEventArguments.Build(5m, "HP", "Pilot", "D Island - HP Flare");

        Assert.Equal(86400m, Assert.Single(args, a => a.ArgumentCode == "Duration").Value);
        Assert.Equal(1m, Assert.Single(args, a => a.ArgumentCode == "IsPilot").Value);
        Assert.Equal("D Island - HP Flare", Assert.Single(args, a => a.ArgumentCode == "Category").ValueString);
        Assert.Equal(5m, Assert.Single(args, a => a.ArgumentCode == "Total").Value);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("HP", 0)]
    [InlineData("LP", 1)]
    public async Task Методологія_з_Total_Duration_FlareUnitMode_рахує_з_події(string pressureType, int expected)
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
            MethodologyId: 7,
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
            TableInstanceId: 501,
            PeriodKey: new PeriodKey(202601),
            SourceRowKey: "8001001",
            Arguments: FlareEventArguments.Build(8640m, pressureType, "Purge", "F1"));

        var output = await module.ExecuteAsync(input, CancellationToken.None);

        // HP: 0; LP: 8640 * 10 / 86400 = 1.
        Assert.Equal((decimal)expected, Assert.Single(output.Values).Value);
    }

    private static IMethodologyStore Methodologies()
    {
        var formula = new MethodologyFormula(
            VersionId, EcrCode.Create("Emission"), "if(@FlareUnitMode = 1, 0, @Total * 10 / @Duration)");
        formula.SetEvaluationOrder(1);

        var store = Substitute.For<IMethodologyStore>();
        store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns([formula]);
        store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns([new MethodologyOutput(VersionId, EcrCode.Create("Emission"), KilogramUnit)]);
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
