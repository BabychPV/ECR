// tests/Ecr.Calculations.Tests/DraftFormulaOrderTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Аудит L7-02: чернетка (EvaluationOrder = 0) рахується в топологічному
/// порядку, а не в порядку створення формул.
/// </summary>
/// <remarks>
/// ⛔ Що було. Золотий прогін публікації й симуляція рахують версію ДО того, як
/// публікація проставить <c>EvaluationOrder</c>; модуль сортував за <c>Id</c>.
/// <c>gsec = !MassKg / 1000</c>, створена першою, читала ще не пораховану
/// <c>MassKg</c> → <c>#REF</c>, вихід не писався, і публікація правильної
/// методології відхилялась «золотий набір розійшовся».
/// </remarks>
public sealed class DraftFormulaOrderTests
{
    private const int VersionId = 62;
    private const int KilogramUnit = 1;
    private const long DocumentId = 701;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Чернетка_з_формулами_у_зворотному_порядку_рахує_вихід()
    {
        var output = await Module(Formulas(published: false)).ExecuteAsync(Input(), CancellationToken.None);

        Assert.Equal(6m, Assert.Single(output.Values).Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Порядок_чернетки_збігається_з_тим_що_запише_публікація()
    {
        var order = MethodologyFormulaOrder.Topological(Formulas(published: false), new RealFormulaEngine());

        Assert.Equal(["Volume", "MassKg", "gsec"], order.Select(f => f.Code));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Цикл_лишає_порядок_Id_публікація_його_відхилить()
    {
        var a = Formula(1, "A", "!B + 1");
        var b = Formula(2, "B", "!A + 1");

        Assert.Equal(["A", "B"], MethodologyFormulaOrder.Topological([b, a], new RealFormulaEngine()).Select(f => f.Code));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Опублікована_версія_не_пересортовується()
        => Assert.False(MethodologyFormulaOrder.IsUnordered(Formulas(published: true)));

    private static List<MethodologyFormula> Formulas(bool published)
    {
        // Створені «навпаки»: споживач раніше за джерело.
        var gsec = Formula(1, "gsec", "!MassKg / 1000");
        var mass = Formula(2, "MassKg", "!Volume * 2000");
        var volume = Formula(3, "Volume", "@Jan");
        if (published)
        {
            volume.SetEvaluationOrder(1);
            mass.SetEvaluationOrder(2);
            gsec.SetEvaluationOrder(3);
        }

        return [gsec, mass, volume];
    }

    private static MethodologyFormula Formula(int id, string code, string expression)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create(code), expression);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(formula, id);
        return formula;
    }

    private static CalculationInput Input()
        => new(
            new MethodologyDescriptor(
                MethodologyId: 6,
                MethodologyVersionId: VersionId,
                Code: "DRAFT",
                VersionNumber: "1.0.0.0",
                Level: CalculationLevel.Configuration,
                NumericMode: NumericMode.Legacy,
                CalendarMode: CalendarMode.Actual,
                TraceLevel: TraceLevel.Off),
            DocumentId,
            TableInstanceId: 500,
            PeriodKey: new PeriodKey(202601),
            SourceRowKey: "7001001",
            Arguments: [new CalculationArgument("Jan", 3m, null, null)]);

    private static GenericCalculationModule Module(List<MethodologyFormula> formulas)
    {
        var store = Substitute.For<IMethodologyStore>();
        store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(formulas);
        store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns([new MethodologyOutput(VersionId, EcrCode.Create("gsec"), KilogramUnit)]);
        store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>()).Returns([]);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, 202601, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["kg"] = new(KilogramUnit, "kg", 1, 1m) },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        return new GenericCalculationModule(
            new RealFormulaEngine(),
            store,
            new ConstantResolver(Substitute.For<IConstantStore>()),
            new CalendarContext(),
            units,
            periods,
            Substitute.For<ICalculationBindingStore>());
    }
}
