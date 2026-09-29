// tests/Ecr.Calculations.Tests/Library/LibraryStand.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;

namespace Ecr.Calculations.Tests.Library;

/// <summary>
/// Стенд HSE301 L: викликач <c>HSE400</c> імпортує бібліотеку <c>Common</c> і посилається
/// на її формули через <c>!Code</c>. Сховище — фейк, рушій і модуль — справжні.
/// </summary>
/// <remarks>
/// <c>Common</c> v910: <c>Common_M</c> (Row) = <c>@Volume * CST.RHO</c>, RHO = 0.8;
/// <c>Common_W</c> (речовина) = <c>!Common_M * CST.EF</c>, EF: 901 → 0.1, 902 → 0.2;
/// <c>Common_Unused</c> = <c>1 / 0</c> — у замикання не входить.
/// Викликач має ВЛАСНУ константу RHO = 0.5 — інше число, ніж у бібліотеки.
/// </remarks>
internal sealed class LibraryStand
{
    public const int CallerId = 400;
    public const int CallerVersionId = 4001;
    public const int CommonId = 900;
    public const int CommonVersionId = 910;
    public const int TonneUnit = 8;
    public const decimal Volume = 100m;

    public static readonly long[] Substances = [901, 902];

    public static readonly DateOnly BusinessDate = new(2026, 1, 31);

    public LibraryStand()
    {
        Store.GetSubstancesAsync(CallerVersionId, Arg.Any<CancellationToken>()).Returns(
            [.. Substances.Select((id, i) => new MethodologySubstance(CallerVersionId, id, i + 1))]);
        Store.GetConstantsAsync(CallerVersionId, Arg.Any<CancellationToken>())
             .Returns([Constant(CallerVersionId, "RHO", 0.5m, substance: null)]);
        Store.GetLibraryContentsAsync(CommonVersionId, BusinessDate, Arg.Any<CancellationToken>())
             .Returns(new List<MethodologyLibraryContent>());

        Caller(
            [
                Formula(CallerVersionId, "M_total", "!Common_M * 2", 1, MethodologyFormulaScope.Row),
                Formula(CallerVersionId, "tons", "!Common_W + CST.RHO", 2, MethodologyFormulaScope.Substance),
            ],
            [Output("M_total", perSubstance: false), Output("tons", perSubstance: true)]);

        Imports(CallerVersionId, CommonContent());
    }

    public IMethodologyStore Store { get; } = Substitute.For<IMethodologyStore>();

    /// <summary>Склад <c>Common</c> v910 — формули в порядку бібліотеки.</summary>
    public static List<MethodologyFormula> CommonFormulas(bool visible = true) =>
    [
        Formula(CommonVersionId, "Common_M", "@Volume * CST.RHO", 1, MethodologyFormulaScope.Row, visible),
        Formula(CommonVersionId, "Common_W", "!Common_M * CST.EF", 2, MethodologyFormulaScope.Substance, visible),
        Formula(CommonVersionId, "Common_Unused", "1 / 0", 3, MethodologyFormulaScope.Substance),
    ];

    /// <summary>Константи <c>Common</c> v910 (або їхня копія в іншій версії).</summary>
    public static List<MethodologyConstant> CommonConstants(int versionId) =>
    [
        Constant(versionId, "RHO", 0.8m, substance: null),
        Constant(versionId, "EF", 0.1m, substance: 901),
        Constant(versionId, "EF", 0.2m, substance: 902),
    ];

    public static MethodologyLibraryContent CommonContent(IReadOnlyList<MethodologyFormula>? formulas = null)
    {
        var list = formulas ?? CommonFormulas();

        return new MethodologyLibraryContent(
            new MethodologyLibrary(CommonId, "Common", CommonVersionId, [.. list.Select(f => f.Code)]),
            NumericMode.Strict,
            CalendarMode.Actual,
            list,
            CommonConstants(CommonVersionId));
    }

    /// <summary>Формули й виходи викликача.</summary>
    public void Caller(List<MethodologyFormula> formulas, List<MethodologyOutput> outputs)
    {
        Store.GetFormulasAsync(CallerVersionId, Arg.Any<CancellationToken>()).Returns(formulas);
        Store.GetOutputsAsync(CallerVersionId, Arg.Any<CancellationToken>()).Returns(outputs);
    }

    /// <summary>Імпорти версії на бізнес-дату стенда.</summary>
    public void Imports(int versionId, params MethodologyLibraryContent[] contents)
        => Store.GetLibraryContentsAsync(versionId, BusinessDate, Arg.Any<CancellationToken>())
                .Returns(new List<MethodologyLibraryContent>(contents));

    public Task<CalculationOutput> RunAsync(TraceLevel trace = TraceLevel.Full)
    {
        var module = new GenericCalculationModule(
            new RealFormulaEngine(),
            Store,
            new ConstantResolver(Substitute.For<IConstantStore>()),
            new CalendarContext(),
            Units(),
            Periods(),
            Bindings());

        return module.ExecuteAsync(
            new CalculationInput(
                new MethodologyDescriptor(
                    CallerId, CallerVersionId, "HSE400", "1.0", CalculationLevel.Configuration,
                    NumericMode.Strict, CalendarMode.Actual, trace),
                DocumentId: 700,
                TableInstanceId: 500,
                PeriodKey: new PeriodKey(202601),
                SourceRowKey: "E-2026-01-001",
                Arguments: [new CalculationArgument("Volume", Volume, null, null)]),
            CancellationToken.None);
    }

    public static MethodologyFormula Formula(
        int versionId, string code, string expression, int order, MethodologyFormulaScope scope, bool visible = false)
    {
        var formula = new MethodologyFormula(versionId, EcrCode.Create(code), expression);
        formula.SetEvaluationOrder(order);
        formula.SetScope(scope);
        formula.SetVisible(visible);
        formula.SetOutputUnit(TonneUnit);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(formula, (versionId * 10) + order);
        return formula;
    }

    public static MethodologyOutput Output(string code, bool perSubstance)
    {
        var output = new MethodologyOutput(CallerVersionId, EcrCode.Create(code), TonneUnit, 1);
        output.SetPerSubstance(perSubstance);
        return output;
    }

    public static MethodologyConstant Constant(int versionId, string code, decimal value, long? substance)
    {
        var constant = new MethodologyConstant(versionId, EcrCode.Create(code), value, unitId: 1);
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
            },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        return catalog;
    }

    private static IPeriodStore Periods()
    {
        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(700, 202601, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), BusinessDate));

        return periods;
    }

    private static ICalculationBindingStore Bindings()
    {
        var store = Substitute.For<ICalculationBindingStore>();
        store.ListOutputScalesAsync(CallerId, Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, byte?>(StringComparer.OrdinalIgnoreCase));

        return store;
    }
}
