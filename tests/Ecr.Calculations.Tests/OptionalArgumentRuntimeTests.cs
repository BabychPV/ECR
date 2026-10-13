// tests/Ecr.Calculations.Tests/OptionalArgumentRuntimeTests.cs
using Ecr.Application.Ports;
using Ecr.Calculations.Tests.Library;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// D-OA: необов'язковий аргумент (<c>Name?</c> у <c>ArgumentsCsv</c>) без комірки в рядку — <c>null</c>
/// (як порожня комірка), а не <c>#ARG</c>; не-optional аргумент лишається <c>#ARG</c>.
/// </summary>
/// <remarks>
/// ⛔ Мутація: прибрати <c>arguments.TryAdd(name, Null)</c> у <c>OptionalArguments.Fill</c> — тести 1, 5, 6
/// червоніють; контроль 2 захищає від «усе optional».
/// </remarks>
public sealed class OptionalArgumentRuntimeTests
{
    private const int VersionId = 72;
    private const long DocumentId = 800;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Необовязковий_аргумент_без_комірки_дає_NULL_а_не_ARG_і_результат_не_пишеться()
    {
        var output = await RunAsync("Jan?", cell: Cell.Absent);

        var steps = output.Trace.Where(s => s.StepCode == "Total").ToList();
        Assert.NotEmpty(steps);
        Assert.All(steps, s => Assert.Equal("#NULL", s.TraceJson));
        Assert.DoesNotContain(output.Values, v => v.OutputCode == "Total");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Не_необовязковий_аргумент_без_комірки_лишається_ARG()
    {
        // Контроль: без маркера другий рубіж 0438 живий.
        var output = await RunAsync("Jan", cell: Cell.Absent);

        var steps = output.Trace.Where(s => s.StepCode == "Total").ToList();
        Assert.NotEmpty(steps);
        Assert.All(steps, s => Assert.Equal(ExpressionErrors.ArgumentNotFound, s.TraceJson));
    }

    [Theory]
    [InlineData("Jan?")]
    [InlineData("Jan")]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Порожня_комірка_дає_NULL_в_обох_режимах(string declared)
    {
        var output = await RunAsync(declared, cell: Cell.Empty);

        var step = Assert.Single(output.Trace, s => s.StepCode == "Total");
        Assert.Equal("#NULL", step.TraceJson);
    }

    [Theory]
    [InlineData("Jan?")]
    [InlineData("Jan")]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Комірка_зі_значенням_дає_значення_маркер_дані_не_маскує(string declared)
    {
        var output = await RunAsync(declared, cell: Cell.Value);

        var total = Assert.Single(output.Values, v => v.OutputCode == "Total");
        Assert.Equal(6m, total.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Маркер_у_списку_іншої_формули_версії_звільняє_аргумент_для_всієї_версії()
    {
        // Q-OA-4: `Jan?` лише у формулі Other; `Total` оголошує `Jan` без маркера — optional для версії.
        var other = Formula("Other", "@Jan * 2", order: 2, declared: "Jan?");
        var output = await RunAsync("Jan", cell: Cell.Absent, extra: [other]);

        var step = Assert.Single(output.Trace, s => s.StepCode == "Total");
        Assert.Equal("#NULL", step.TraceJson);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Формула_бібліотеки_читає_необовязковий_аргумент_викликача()
    {
        // Common_M = @OpTime * CST.RHO; `OpTime?` оголошено у ЛІБРАРІЙНІЙ формулі, комірки в рядку викликача немає.
        var optional = await RunLibraryAsync(libraryDeclared: "OpTime?");
        var strict = await RunLibraryAsync(libraryDeclared: "OpTime");

        Assert.Contains(optional.Trace, s => s.TraceJson == "#NULL");
        Assert.DoesNotContain(optional.Trace, s => s.TraceJson == ExpressionErrors.ArgumentNotFound);

        // Контроль: без маркера бібліотечна формула дає #ARG.
        Assert.Contains(strict.Trace, s => s.TraceJson == ExpressionErrors.ArgumentNotFound);
    }

    private static async Task<CalculationOutput> RunLibraryAsync(string libraryDeclared)
    {
        var stand = new LibraryStand();
        var formulas = LibraryStand.CommonFormulas();
        formulas[0] = LibraryStand.Formula(
            LibraryStand.CommonVersionId, "Common_M", "@OpTime * CST.RHO", 1, MethodologyFormulaScope.Row, visible: true);
        formulas[0].SetArguments(libraryDeclared);
        stand.Imports(LibraryStand.CallerVersionId, LibraryStand.CommonContent(formulas));

        return await stand.RunAsync();
    }

    private enum Cell
    {
        /// <summary>Комірки немає взагалі (колонки в таблиці немає / комірку не створено).</summary>
        Absent,

        /// <summary>Комірка є, значення порожнє.</summary>
        Empty,

        /// <summary>Комірка зі значенням 5.</summary>
        Value,
    }

    private static MethodologyFormula Formula(string code, string expression, int order, string? declared)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create(code), expression);
        formula.SetEvaluationOrder(order);
        formula.SetArguments(declared);
        return formula;
    }

    /// <summary>Виконує методологію з формулою <c>Total = @Jan + 1</c> (оголошення — <paramref name="declared"/>).</summary>
    private static async Task<CalculationOutput> RunAsync(
        string declared, Cell cell, IReadOnlyList<MethodologyFormula>? extra = null)
    {
        var formulas = new List<MethodologyFormula> { Formula("Total", "@Jan + 1", 1, declared) };
        if (extra is not null)
        {
            formulas.AddRange(extra);
        }

        var store = Substitute.For<IMethodologyStore>();
        store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(formulas);
        store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns([new MethodologyOutput(VersionId, EcrCode.Create("Total"), unitId: 1)]);
        store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>()).Returns([]);

        var module = new GenericCalculationModule(
            new RealFormulaEngine(),
            store,
            new ConstantResolver(Substitute.For<IConstantStore>()),
            new CalendarContext(),
            Units(),
            Periods(),
            Substitute.For<ICalculationBindingStore>());

        var descriptor = new MethodologyDescriptor(
            MethodologyId: 9,
            MethodologyVersionId: VersionId,
            Code: "OPT_TEST",
            VersionNumber: "1.0.0.0",
            Level: CalculationLevel.Configuration,
            NumericMode: NumericMode.Strict,
            CalendarMode: CalendarMode.Actual,
            TraceLevel: TraceLevel.ErrorsOnly);

        List<CalculationArgument> arguments = cell switch
        {
            Cell.Absent => [],
            Cell.Empty => [new("Jan", null, null, null)],
            _ => [new("Jan", 5m, null, null)],
        };

        var input = new CalculationInput(
            descriptor,
            DocumentId,
            TableInstanceId: 500,
            PeriodKey: new PeriodKey(202601),
            SourceRowKey: "7001001",
            Arguments: arguments);

        return await module.ExecuteAsync(input, CancellationToken.None);
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
                ["kg"] = new(1, "kg", 1, 1m),
            },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        return catalog;
    }
}
