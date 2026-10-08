// tests/Ecr.Calculations.Tests/CategoryRuleEngineTests.cs
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
/// L-2 (<c>calc.CategoryRule</c>): рушій обчислює правило категорії раз на рядок, ПІСЛЯ Row-формул і
/// ДО циклу речовин, і передає ключ у вибір констант.
/// </summary>
/// <remarks>
/// ⛔ Що було. <c>GenericCalculationModule</c> кликав <c>ConstantResolver.Resolve(…, category: null, …)</c>,
/// тож константа AF з кількома категоріями (5.1 — 16, 5.2 — Summer/Winter, 6.1 — Diesel/Gas/Gasoline)
/// давала <c>ECR-CALC-0422 constantAmbiguous</c> на КОЖНОМУ рядку, а 5.1/5.2/5.4/6.1/7.7/7.8/7.21 не
/// публікувалися.
///
/// Мутаційні докази: рушій ігнорує правило — червоні всі тести з правилом
/// (<see cref="Правило_вибирає_константу_категорії_рядка"/> дає constantAmbiguous); передає ключ у
/// Row-фазу — червоний <see cref="Row_фаза_не_має_категорії"/>.
/// </remarks>
public sealed class CategoryRuleEngineTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Правило_вибирає_константу_категорії_рядка()
    {
        var stand = new RuleStand(rule: "@Cat")
            .WithSubstanceFormula("Total", "@X * CST.EF")
            .WithConstants(RuleStand.Number("EF", 1m, "A"), RuleStand.Number("EF", 3m, "B"));

        var output = await stand.RunAsync(RuleStand.In("X", 10m), RuleStand.In("Cat", "B"));

        // Обидві речовини беруть коефіцієнт категорії B, а не A і не помилку неоднозначності.
        Assert.Equal([30m, 30m], output.Values.Where(v => v.OutputCode == "Total").Select(v => v.Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Інший_рядок_тієї_самої_версії_бере_іншу_категорію()
    {
        var stand = new RuleStand(rule: "@Cat")
            .WithSubstanceFormula("Total", "@X * CST.EF")
            .WithConstants(RuleStand.Number("EF", 1m, "A"), RuleStand.Number("EF", 3m, "B"));

        var output = await stand.RunAsync(RuleStand.In("X", 10m), RuleStand.In("Cat", "A"));

        Assert.Equal([10m, 10m], output.Values.Where(v => v.OutputCode == "Total").Select(v => v.Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Без_правила_кілька_категорій_лишаються_неоднозначними_як_і_було()
    {
        // Контроль: версії без правила поводяться побітно як до L-2.
        var stand = new RuleStand(rule: null)
            .WithSubstanceFormula("Total", "@X * CST.EF")
            .WithConstants(RuleStand.Number("EF", 1m, "A"), RuleStand.Number("EF", 3m, "B"));

        var error = await Assert.ThrowsAsync<DomainException>(
            () => stand.RunAsync(RuleStand.In("X", 10m), RuleStand.In("Cat", "B")));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-0422.constantAmbiguous", error.Details?["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Константа_Common_доступна_поряд_із_категорійною()
    {
        // 5.2: k1/k2 лежать у AF як Category = "Common", EF — по Summer/Winter.
        var stand = new RuleStand(rule: "@Cat")
            .WithSubstanceFormula("Total", "@X * CST.EF * CST.K")
            .WithConstants(
                RuleStand.Number("EF", 0.5m, "Summer"),
                RuleStand.Number("EF", 0.25m, "Winter"),
                RuleStand.Number("K", 2m, MethodologyConstant.CommonCategory));

        var output = await stand.RunAsync(RuleStand.In("X", 10m), RuleStand.In("Cat", "Winter"));

        Assert.Equal([5m, 5m], output.Values.Where(v => v.OutputCode == "Total").Select(v => v.Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Row_формула_дає_ключ_через_Common_константи_як_у_5_2()
    {
        // Ключ — результат Row-формули, яка сама читає Common-константи k1/k2 (Row-фаза без категорії).
        var stand = new RuleStand(rule: "!ECW_Category")
            .WithRowFormula("ECW_Category", "if(@Month >= 4 and @Month <= 9, CST.k1_Sel, CST.k2_Sel)", FormulaResultType.Text)
            .WithSubstanceFormula("Total", "@X * CST.EF")
            .WithConstants(
                RuleStand.Text("k1_Sel", "Summer", MethodologyConstant.CommonCategory),
                RuleStand.Text("k2_Sel", "Winter", MethodologyConstant.CommonCategory),
                RuleStand.Number("EF", 0.5m, "Summer"),
                RuleStand.Number("EF", 0.25m, "Winter"));

        var summer = await stand.RunAsync(RuleStand.In("X", 10m), RuleStand.In("Month", 6m));
        var winter = await stand.RunAsync(RuleStand.In("X", 10m), RuleStand.In("Month", 12m));

        Assert.Equal([5m, 5m], summer.Values.Where(v => v.OutputCode == "Total").Select(v => v.Value));
        Assert.Equal([2.5m, 2.5m], winter.Values.Where(v => v.OutputCode == "Total").Select(v => v.Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Шістнадцять_категорій_як_у_5_1_не_дають_неоднозначності()
    {
        // 5.1: Loc/EU × BeforeMR/AfterMR × A…D — 16 кандидатів одного коду.
        string[] places = ["Loc", "EU"];
        string[] repairs = ["BeforeMR", "AfterMR"];
        string[] classes = ["A", "B", "C", "D"];
        var constants = new List<MethodologyConstant>();
        var value = 0;
        foreach (var place in places)
        {
            foreach (var repair in repairs)
            {
                foreach (var cls in classes)
                {
                    constants.Add(RuleStand.Number("EF", ++value, $"{place}_{repair}_{cls}"));
                }
            }
        }

        var stand = new RuleStand(rule: "if(@Repair = 'AfterMR', @AfterKey, @BeforeKey)")
            .WithSubstanceFormula("Total", "@X * CST.EF")
            .WithConstants([.. constants]);

        // Loc_AfterMR_B — шостий у порядку створення (Loc: Before A…D = 1…4, After A…D = 5…8).
        var output = await stand.RunAsync(
            RuleStand.In("X", 1m), RuleStand.In("Repair", "AfterMR"),
            RuleStand.In("AfterKey", "Loc_AfterMR_B"), RuleStand.In("BeforeKey", "Loc_BeforeMR_B"));

        Assert.Equal([6m, 6m], output.Values.Where(v => v.OutputCode == "Total").Select(v => v.Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Row_фаза_не_має_категорії()
    {
        // ⚠ Row-формула, що читає категорійну константу, лишається неоднозначною: категорію
        // правило дає ПІСЛЯ Row-фази (рішення 5.4 дизайну; публікація про це попереджає).
        var stand = new RuleStand(rule: "@Cat")
            .WithRowFormula("Base", "@X * CST.EF", FormulaResultType.Number)
            .WithSubstanceFormula("Total", "!Base")
            .WithConstants(RuleStand.Number("EF", 1m, "A"), RuleStand.Number("EF", 3m, "B"));

        await Assert.ThrowsAsync<DomainException>(
            () => stand.RunAsync(RuleStand.In("X", 10m), RuleStand.In("Cat", "B")));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Категорія_без_константи_дає_REF_а_не_заповнювач()
    {
        // L-2, пункт C: для Diesel у AF немає EF_B — вихід EF_B для Diesel НЕ пишеться (#REF), заповнювач
        // не створюється, а вихід EF_A, який у Diesel є, рахується.
        var stand = new RuleStand(rule: "@Cat")
            .WithSubstanceFormula("Total", "@X * CST.EF_A")
            .WithSubstanceFormula("Extra", "@X * CST.EF_B")
            .WithConstants(
                RuleStand.Number("EF_A", 2m, "Diesel"),
                RuleStand.Number("EF_A", 5m, "Gas"),
                RuleStand.Number("EF_B", 7m, "Gas"));

        var output = await stand.RunAsync(RuleStand.In("X", 10m), RuleStand.In("Cat", "Diesel"));

        Assert.Equal([20m, 20m], output.Values.Where(v => v.OutputCode == "Total").Select(v => v.Value));
        Assert.DoesNotContain(output.Values, v => v.OutputCode == "Extra");
        Assert.Contains(output.Trace, s => s.StepCode == "Extra" && s.TraceJson == Expressions.ExpressionErrors.BadReference);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("@Missing")]
    [InlineData("@X")]
    [InlineData("@Cat + 1")]
    public async Task Несправне_правило_відмовляє_рядку_а_не_підміняє_категорію_на_null(string rule)
    {
        // @Missing — аргументу в рядку немає (порожньо); @X — число, а не текст; `@Cat + 1` — помилка обчислення.
        // Без відмови рядок мовчки впав би на constantAmbiguous (або, гірше, взяв би не ту категорію).
        var stand = new RuleStand(rule)
            .WithSubstanceFormula("Total", "@X * CST.EF")
            .WithConstants(RuleStand.Number("EF", 1m, "A"), RuleStand.Number("EF", 3m, "B"));

        var error = await Assert.ThrowsAsync<DomainException>(
            () => stand.RunAsync(RuleStand.In("X", 10m), RuleStand.In("Cat", "B")));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-0422.categoryRuleFailed", error.Details?["messageKey"]);
    }
}

/// <summary>Стенд версії з правилом категорії: формули, константи, дві речовини.</summary>
internal sealed class RuleStand
{
    public const int VersionId = 1101;
    public const long DocumentId = 1100;
    public const long SubstanceA = 9001;
    public const long SubstanceB = 9002;

    private readonly List<MethodologyFormula> _formulas = [];
    private readonly List<MethodologyOutput> _outputs = [];
    private readonly IMethodologyStore _store = Substitute.For<IMethodologyStore>();
    private int _order;

    public RuleStand(string? rule)
    {
        _store.GetCategoryRuleAsync(VersionId, Arg.Any<CancellationToken>()).Returns(rule);
        _store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>())
              .Returns([new MethodologySubstance(VersionId, SubstanceA), new MethodologySubstance(VersionId, SubstanceB)]);
    }

    public static CalculationArgument In(string code, decimal value) => new(code, value, null, null);

    public static CalculationArgument In(string code, string value) => new(code, null, value, null);

    public static MethodologyConstant Number(string code, decimal value, string category)
    {
        var constant = new MethodologyConstant(VersionId, EcrCode.Create(code), value, unitId: 23);
        constant.SetScope(category, substanceEntryId: null);
        return constant;
    }

    public static MethodologyConstant Text(string code, string text, string category)
    {
        var constant = MethodologyConstant.OfText(VersionId, EcrCode.Create(code), text, ConstantKind.Text);
        constant.SetScope(category, substanceEntryId: null);
        return constant;
    }

    public RuleStand WithRowFormula(string code, string expression, FormulaResultType type)
        => Add(code, expression, MethodologyFormulaScope.Row, type, isOutput: false);

    public RuleStand WithSubstanceFormula(string code, string expression)
        => Add(code, expression, MethodologyFormulaScope.Substance, FormulaResultType.Number, isOutput: true);

    public RuleStand WithConstants(params MethodologyConstant[] constants)
    {
        _store.GetConstantsAsync(VersionId, Arg.Any<CancellationToken>()).Returns(constants);
        return this;
    }

    public async Task<CalculationOutput> RunAsync(params CalculationArgument[] arguments)
    {
        _store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(_formulas);
        _store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>()).Returns(_outputs);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, 202601, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["kg"] = new(1, "kg", 1, 1m) },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        var module = new GenericCalculationModule(
            new RealFormulaEngine(),
            _store,
            new ConstantResolver(Substitute.For<IConstantStore>()),
            new CalendarContext(),
            units,
            periods,
            Substitute.For<ICalculationBindingStore>());

        var descriptor = new MethodologyDescriptor(
            MethodologyId: 11,
            MethodologyVersionId: VersionId,
            Code: "L2_CATEGORY",
            VersionNumber: "1.0.0.0",
            Level: CalculationLevel.Configuration,
            NumericMode: NumericMode.Strict,
            CalendarMode: CalendarMode.Actual,
            TraceLevel: TraceLevel.Full);

        var input = new CalculationInput(
            descriptor,
            DocumentId,
            TableInstanceId: 500,
            PeriodKey: new PeriodKey(202601),
            SourceRowKey: "R1",
            Arguments: arguments);

        return await module.ExecuteAsync(input, CancellationToken.None);
    }

    private RuleStand Add(
        string code, string expression, MethodologyFormulaScope scope, FormulaResultType type, bool isOutput)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create(code), expression);
        formula.SetResultType(type);
        formula.SetScope(scope);
        formula.SetEvaluationOrder(++_order);
        _formulas.Add(formula);

        if (isOutput)
        {
            _outputs.Add(new MethodologyOutput(VersionId, EcrCode.Create(code), unitId: 1, ordinal: _order));
        }

        return this;
    }
}
