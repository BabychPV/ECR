// tests/Ecr.Calculations.Tests/ConstantScopeFallbackTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Константа чужої речовини чи категорії НЕ підставляється, коли немає ні
/// власної, ні загальної (аудит A1).
/// </summary>
/// <remarks>
/// ⛔ Що було. Ланцюг звуження в <see cref="ConstantResolver"/> закінчувався
/// <c>?? valid</c>: коли для речовини B не знаходилось ні точного, ні
/// загального кандидата, повертався весь набір — тобто константи ІНШИХ
/// речовин. За рівно одного такого кандидата перевірка неоднозначності мовчала,
/// і прогін для B множив на коефіцієнт A. Число правдоподібне, помилки — жодної.
/// </remarks>
public sealed class ConstantScopeFallbackTests
{
    private const int VersionId = 81;
    private const long SubstanceA = 9001;
    private const long SubstanceB = 9002;
    private const long DocumentId = 810;

    private static readonly DateOnly OnDate = new(2026, 1, 31);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Константа_лише_для_речовини_A_не_береться_для_речовини_B()
    {
        var resolver = Resolver(Constant("EF", 0.5m, category: null, SubstanceA));

        Assert.Null(await resolver.ResolveAsync(VersionId, "EF", null, SubstanceB, OnDate, default));

        // Контроль: для самої A константа на місці — «null» вище не означає,
        // що резолвер не бачить кандидатів узагалі.
        Assert.Equal(0.5m, (await resolver.ResolveAsync(VersionId, "EF", null, SubstanceA, OnDate, default))!.Number);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Для_речовини_B_береться_загальна_а_не_константа_A()
    {
        var resolver = Resolver(
            Constant("EF", 0.5m, category: null, SubstanceA),
            Constant("EF", 0.1m, category: null, substanceEntryId: null));

        Assert.Equal(0.1m, (await resolver.ResolveAsync(VersionId, "EF", null, SubstanceB, OnDate, default))!.Number);
        Assert.Equal(0.5m, (await resolver.ResolveAsync(VersionId, "EF", null, SubstanceA, OnDate, default))!.Number);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Прогін_без_речовини_не_бере_речовинну_константу()
    {
        // ⚠ Рішення: методологія без речовин (`substanceEntryId = null`) бере
        // лише загальні константи. Єдину речовинну підставити — та сама вада,
        // що й для B: вибрати «свою» речовину тут нема за чим.
        var resolver = Resolver(Constant("EF", 0.5m, category: null, SubstanceA));

        Assert.Null(await resolver.ResolveAsync(VersionId, "EF", null, null, OnDate, default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Загальна_константа_як_і_раніше_діє_для_будь_якої_речовини()
    {
        // Константа, що від речовини не залежить узагалі, — найчастіший
        // випадок; фікс A1 не сміє його зачепити.
        var resolver = Resolver(Constant("DENSITY", 1.2m, category: null, substanceEntryId: null));

        Assert.Equal(1.2m, (await resolver.ResolveAsync(VersionId, "DENSITY", null, SubstanceA, OnDate, default))!.Number);
        Assert.Equal(1.2m, (await resolver.ResolveAsync(VersionId, "DENSITY", null, SubstanceB, OnDate, default))!.Number);
        Assert.Equal(1.2m, (await resolver.ResolveAsync(VersionId, "DENSITY", null, null, OnDate, default))!.Number);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Константа_категорії_K1_не_застосовується_до_K2()
    {
        var onlyK1 = Resolver(Constant("K", 3m, "K1", substanceEntryId: null));

        Assert.Null(await onlyK1.ResolveAsync(VersionId, "K", "K2", null, OnDate, default));
        Assert.Equal(3m, (await onlyK1.ResolveAsync(VersionId, "K", "K1", null, OnDate, default))!.Number);

        // K1 і загальна за категорією: K2 отримує загальну.
        var withGeneral = Resolver(
            Constant("K", 3m, "K1", substanceEntryId: null),
            Constant("K", 7m, category: null, substanceEntryId: null));

        Assert.Equal(7m, (await withGeneral.ResolveAsync(VersionId, "K", "K2", null, OnDate, default))!.Number);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Без_заданої_категорії_звуження_за_категорією_немає_як_і_було()
    {
        // ⚠ Рушій кличе з `category = null`, а константи корпусу несуть
        // категорію-мітку («default») і там, де вона одна. Зробити й цю гілку
        // суворою означало б дати `#REF` усім таким константам.
        var resolver = Resolver(Constant("EF", 0.25m, "default", SubstanceA));

        Assert.Equal(0.25m, (await resolver.ResolveAsync(VersionId, "EF", null, SubstanceA, OnDate, default))!.Number);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Прогін_методології_для_речовини_B_дає_REF_а_не_число_речовини_A()
    {
        // ⛔ Наскрізно, через модуль: результат для B НЕ пишеться, а в трейсі
        // стоїть `#REF`. Саме тут вада й коштувала — у виході B з'являлося
        // `@X × 0.5`, коефіцієнт речовини A.
        var output = await RunModuleAsync(Constant("EF", 0.5m, category: null, SubstanceA));

        var a = Assert.Single(output.Values, v => v.SubstanceEntryId == SubstanceA);
        Assert.Equal(5m, a.Value);

        Assert.DoesNotContain(output.Values, v => v.SubstanceEntryId == SubstanceB);
        Assert.Contains(output.Trace, s => s.StepCode == "Total" && s.TraceJson == ExpressionErrors.BadReference);
    }

    /// <summary>Резолвер над сховищем, що віддає цих кандидатів.</summary>
    private static ConstantResolver Resolver(params MethodologyConstant[] candidates)
    {
        var store = Substitute.For<IConstantStore>();
        store.GetCandidatesAsync(VersionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(call => candidates
                 .Where(c => string.Equals(c.Code, call.ArgAt<string>(1), StringComparison.OrdinalIgnoreCase))
                 .ToList());

        return new ConstantResolver(store);
    }

    /// <summary>Методологія <c>Total = @X * CST.EF</c> на дві речовини, <c>X = 10</c>.</summary>
    private static async Task<CalculationOutput> RunModuleAsync(params MethodologyConstant[] constants)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create("Total"), "@X * CST.EF");
        formula.SetEvaluationOrder(1);

        var store = Substitute.For<IMethodologyStore>();
        store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns([formula]);
        store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns([new MethodologyOutput(VersionId, EcrCode.Create("Total"), unitId: 1)]);
        store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns([new MethodologySubstance(VersionId, SubstanceA), new MethodologySubstance(VersionId, SubstanceB)]);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, 202601, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["kg"] = new(1, "kg", 1, 1m) },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        var module = new GenericCalculationModule(
            new RealFormulaEngine(),
            store,
            Resolver(constants),
            new CalendarContext(),
            units,
            periods,
            Substitute.For<ICalculationBindingStore>());

        var descriptor = new MethodologyDescriptor(
            MethodologyId: 8,
            MethodologyVersionId: VersionId,
            Code: "A1_SCOPE",
            VersionNumber: "1.0.0.0",
            Level: CalculationLevel.Configuration,
            NumericMode: NumericMode.Strict,
            CalendarMode: CalendarMode.Actual,
            TraceLevel: TraceLevel.ErrorsOnly);

        var input = new CalculationInput(
            descriptor,
            DocumentId,
            TableInstanceId: 500,
            PeriodKey: new PeriodKey(202601),
            SourceRowKey: "R1",
            Arguments: [new CalculationArgument("X", 10m, null, null)]);

        return await module.ExecuteAsync(input, CancellationToken.None);
    }

    private static MethodologyConstant Constant(
        string code, decimal value, string? category, long? substanceEntryId)
    {
        var constant = new MethodologyConstant(VersionId, EcrCode.Create(code), value, unitId: 23);
        constant.SetScope(category, substanceEntryId);
        return constant;
    }
}
