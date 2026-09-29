// tests/Ecr.Calculations.Tests/RowScopeTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Область формули (HSE301 A3a, <c>D-176</c>, V-7): Row-формула — раз на рядок, вихід
/// «раз на рядок» — один рядок результату без речовини.
/// </summary>
/// <remarks>
/// ⛔ До кроку модуль рахував КОЖНУ формулу на кожну речовину: для методології 301 з
/// одинадцятьма речовинами <c>M_t</c> лягав у результати 11 разів, і сума по рядку давала
/// одинадцятикратну масу (FEATURE-HSE301-VIEW §6.1).
///
/// Мутаційні докази: рушій ігнорує <c>Scope</c> (усі формули в циклі речовин) —
/// <see cref="M_t_рахується_один_раз_на_рядок_а_не_на_кожну_з_11_речовин"/> червоний
/// (кроків <c>M_t</c> у трейсі 11); типове <c>Scope = Row</c> у
/// <c>MethodologyFormula</c> — червоні <c>GoldenCalculationTests</c> (константа по речовині
/// резолвиться без речовини).
/// </remarks>
public sealed class RowScopeTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.1")]
    public async Task M_t_рахується_один_раз_на_рядок_а_не_на_кожну_з_11_речовин()
    {
        var stand = new ScopeStand();

        var output = await stand.RunAsync(TraceLevel.Full);

        // Один крок трейсу — одне обчислення. У циклі речовин їх було б одинадцять.
        Assert.Single(output.Trace, s => s.StepCode == "M_t");
        Assert.Single(output.Trace, s => s.StepCode == "V_Sm3");

        // …і один рядок результату, без речовини, як вихід.
        var mass = Assert.Single(output.Values, v => v.OutputCode == "M_t");
        Assert.Null(mass.SubstanceEntryId);
        Assert.Equal(CalculationResultKind.Output, mass.Kind);
        Assert.Equal(ScopeStand.TonneUnit, mass.UnitId);
        Assert.Equal(ScopeStand.ExpectedMass, mass.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.1")]
    public async Task Формула_речовини_бачить_Row_результат_і_власну_константу()
    {
        var stand = new ScopeStand();

        var output = await stand.RunAsync(TraceLevel.Full);

        var tons = output.Values.Where(v => v.OutputCode == "tons").ToList();
        Assert.Equal(ScopeStand.SubstanceCount, tons.Count);
        Assert.Equal(ScopeStand.SubstanceIds, tons.Select(v => (long)v.SubstanceEntryId!.Value));

        // Кожна речовина — зі СВОЇМ коефіцієнтом і тим самим M_t, порахованим раз.
        foreach (var value in tons)
        {
            Assert.Equal(ScopeStand.ExpectedTons(value.SubstanceEntryId!.Value), value.Value);
        }

        // Формула речовини рахується, як і раніше, на кожну речовину.
        Assert.Equal(ScopeStand.SubstanceCount, output.Trace.Count(s => s.StepCode == "tons"));
    }

    /// <remarks>
    /// ⚠ Сумісність: вихід із Row-формули, оголошений «на кожну речовину» (типове
    /// <c>IsPerSubstance</c>), пишеться N разів тим самим числом — так, як до кроку. Змінює
    /// це лише явний <c>IsPerSubstance = false</c>, а не область формули.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Вихід_Row_формули_на_кожну_речовину_пишеться_як_до_кроку()
    {
        var stand = new ScopeStand(massPerSubstance: true);

        var output = await stand.RunAsync(TraceLevel.Full);

        var mass = output.Values.Where(v => v.OutputCode == "M_t").ToList();
        Assert.Equal(ScopeStand.SubstanceCount, mass.Count);
        Assert.All(mass, v => Assert.Equal(ScopeStand.ExpectedMass, v.Value));

        // Рахувалося однаково один раз — повторюється лише запис.
        Assert.Single(output.Trace, s => s.StepCode == "M_t");
    }

    /// <remarks>
    /// ⛔ Row-формула резолвить константи БЕЗ речовини: константа, задана лише по
    /// речовинах, для неї не існує (<c>#REF</c>), і вихід рядка не пишеться. Публікація таку
    /// версію не пропускає (<c>rowScopeReferencesSubstance</c>); тут — що рушій не підставляє
    /// «коефіцієнт першої речовини» мовчки.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Row_формула_не_бере_константу_першої_речовини()
    {
        var stand = new ScopeStand(massExpression: "!V_Sm3 * CST.K_MASS");

        var output = await stand.RunAsync(TraceLevel.Full);

        Assert.DoesNotContain(output.Values, v => v.OutputCode == "M_t");
        Assert.Contains(output.Trace, s => s.StepCode == "M_t" && s.TraceJson != null);
    }
}

/// <summary>
/// Стенд методології на кшталт 301: 11 речовин, Row-формули <c>V_Sm3</c> і <c>M_t</c>,
/// формули речовини <c>W_COMP</c> і <c>tons</c>.
/// </summary>
internal sealed class ScopeStand
{
    public const int MethodologyId = 301;
    public const int VersionId = 3019;
    public const int TonneUnit = 8;
    public const int Sm3Unit = 50;
    public const int PercentUnit = 41;
    public const int SubstanceCount = 11;

    private const decimal Volume = 269.258m;
    private const decimal Density = 0.9589m;
    private const decimal SulphurWt = 17.5m;

    /// <summary>269.258 · 0.9589 кг → т.</summary>
    public const decimal ExpectedMass = 0.2581914962m;

    /// <summary>Речовини 901…911 — порядок <c>Ordinal</c>.</summary>
    public static readonly long[] SubstanceIds = [.. Enumerable.Range(901, SubstanceCount).Select(i => (long)i)];

    private readonly IMethodologyStore _store = Substitute.For<IMethodologyStore>();

    public ScopeStand(
        bool visible = true,
        bool massPerSubstance = false,
        string massExpression = "CONVERT(!V_Sm3 * @Rho20, 'kg', 't')",
        bool visibleWithoutUnit = false)
    {
        var volume = Formula("V_Sm3", "@Volume", 1, MethodologyFormulaScope.Row, visible, Sm3Unit);
        var mass = Formula("M_t", massExpression, 2, MethodologyFormulaScope.Row, visible, TonneUnit);
        var composition = Formula(
            "W_COMP", "CST.SEL * @S_wt", 3, MethodologyFormulaScope.Substance, visible,
            visibleWithoutUnit ? null : PercentUnit);
        var tons = Formula(
            "tons", "!M_t * (CST.K_MASS + !W_COMP / 100)", 4, MethodologyFormulaScope.Substance, visible, TonneUnit);

        Formulas = [volume, mass, composition, tons];

        var massOutput = new MethodologyOutput(VersionId, EcrCode.Create("M_t"), TonneUnit, 1);
        massOutput.SetPerSubstance(massPerSubstance);

        _store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(Formulas);
        _store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>()).Returns(
        [
            massOutput,
            new MethodologyOutput(VersionId, EcrCode.Create("tons"), TonneUnit, 2),
        ]);
        _store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>()).Returns(
            [.. SubstanceIds.Select((id, i) => new MethodologySubstance(VersionId, id, i + 1))]);
        _store.GetConstantsAsync(VersionId, Arg.Any<CancellationToken>()).Returns(Constants());
    }

    public List<MethodologyFormula> Formulas { get; }

    /// <summary><c>tons</c> речовини: <c>M_t · (K_MASS + SEL · S_wt / 100)</c>.</summary>
    public static decimal ExpectedTons(long substance)
        => ExpectedMass * (KMass(substance) + (Selector(substance) * SulphurWt / 100m));

    public async Task<CalculationOutput> RunAsync(TraceLevel trace)
    {
        var module = new GenericCalculationModule(
            new RealFormulaEngine(),
            _store,
            new ConstantResolver(Substitute.For<IConstantStore>()),
            new CalendarContext(),
            Units(),
            Periods(),
            Bindings());

        var input = new CalculationInput(
            new MethodologyDescriptor(
                MethodologyId, VersionId, "HSE301_A3A", "1.0.0", CalculationLevel.Configuration,
                NumericMode.Strict, CalendarMode.Actual, trace),
            DocumentId: 700,
            TableInstanceId: 500,
            PeriodKey: new PeriodKey(202601),
            SourceRowKey: "E-2026-01-001",
            Arguments:
            [
                new CalculationArgument("Volume", Volume, null, Sm3Unit),
                new CalculationArgument("Rho20", Density, null, null),
                new CalculationArgument("S_wt", SulphurWt, null, PercentUnit),
            ]);

        return await module.ExecuteAsync(input, CancellationToken.None);
    }

    private static decimal KMass(long substance) => (substance - 900) * 0.001m;

    /// <summary>Селектор wt% (V-11): одиниця лише в «своєї» речовини — тут 905.</summary>
    private static decimal Selector(long substance) => substance == 905 ? 1m : 0m;

    private static MethodologyFormula Formula(
        string code, string expression, int order, MethodologyFormulaScope scope, bool visible, int? unit)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create(code), expression);
        formula.SetEvaluationOrder(order);
        formula.SetScope(scope);
        formula.SetVisible(visible);

        if (unit is { } unitId)
        {
            formula.SetOutputUnit(unitId);
        }

        return formula;
    }

    private static List<MethodologyConstant> Constants()
    {
        var constants = new List<MethodologyConstant>();

        foreach (var substance in SubstanceIds)
        {
            constants.Add(Constant("K_MASS", KMass(substance), substance));
            constants.Add(Constant("SEL", Selector(substance), substance));
        }

        return constants;
    }

    private static MethodologyConstant Constant(string code, decimal value, long substance)
    {
        var constant = new MethodologyConstant(VersionId, EcrCode.Create(code), value, unitId: 1);
        constant.SetScope(null, substance);
        return constant;
    }

    private static IUnitCatalog Units()
    {
        var catalog = Substitute.For<IUnitCatalog>();
        catalog.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
            {
                ["kg"] = new(1, "kg", 1, 1m),
                ["t"] = new(TonneUnit, "t", 1, 1000m),
                ["Sm3"] = new(Sm3Unit, "Sm3", 3, 1m),
            },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        return catalog;
    }

    private static IPeriodStore Periods()
    {
        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(700, 202601, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        return periods;
    }

    private static ICalculationBindingStore Bindings()
    {
        var store = Substitute.For<ICalculationBindingStore>();
        store.ListOutputScalesAsync(MethodologyId, Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, byte?>(StringComparer.OrdinalIgnoreCase));

        return store;
    }
}
