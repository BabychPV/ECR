// tests/Ecr.Application.Tests/Calculations/CategoryRulePublishTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Graph;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Публікація версії з правилом категорії константи (L-2, <c>calc.CategoryRule</c>): правило
/// перевіряється, версія з кількома категоріями без правила дає попередження, а версія з 16
/// категоріями та правилом публікується.
/// </summary>
/// <remarks>
/// ⛔ Мутаційні докази: прибрати <c>MethodologyCategoryRuleChecks.Check</c> з публікації — червоні
/// всі відмови й попередження нижче (версія з поганим правилом публікується мовчки).
///
/// Публікація не вимагає констант для кожної категорії: заповнювачі для категорій, яких у AF немає,
/// не створюються (L-2, пункт C) — <see cref="Версія_де_у_частини_констант_немає_якоїсь_категорії_публікується_без_заповнювачів"/>.
/// </remarks>
public sealed class CategoryRulePublishTests
{
    private const int Author = 7;
    private const int Reviewer = 9;
    private const int VersionId = 81;
    private const int OwnerId = 6;
    private const int TonneUnit = 8;

    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly From = new(2026, 11, 1);

    private readonly IMethodologyStore _store = Substitute.For<IMethodologyStore>();
    private readonly ICalculationBindingStore _bindings = Substitute.For<ICalculationBindingStore>();
    private readonly ICalculationModule _module = Substitute.For<ICalculationModule>();
    private readonly IFormulaEngine _formulas = Substitute.For<IFormulaEngine>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private readonly MethodologyVersion _version;

    public CategoryRulePublishTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(Reviewer);
        _access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Profile());

        var methodology = new Methodology(EcrCode.Create("ECW_C05_01"), Text("Land 5.1"));
        SetId(methodology, OwnerId);

        _version = new MethodologyVersion(OwnerId, "1.0.0", CalculationLevel.Configuration, Author, Now);
        _version.SetModes(NumericMode.Strict, CalendarMode.Actual, TraceLevel.ErrorsOnly);
        SetId(_version, VersionId);
        methodology.AddVersion(_version);

        _store.FindByVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(methodology);
        _store.GetTestCasesAsync(VersionId, Arg.Any<CancellationToken>()).Returns(TestCases());
        _store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>()).Returns(new List<MethodologyOutput>());
        _store.GetRulesAsync(VersionId, Arg.Any<CancellationToken>()).Returns(new List<MethodologyRule>());
        _store.ResolveImportsAsync(VersionId, From, Arg.Any<CancellationToken>())
              .Returns(new List<MethodologyLibrary>());
        _bindings.ListAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<CalculationBinding>());

        _module.ExecuteAsync(Arg.Any<CalculationInput>(), Arg.Any<CancellationToken>())
               .Returns(call => Output(call.Arg<CalculationInput>()));

        var engine = new RealFormulaEngine();
        _formulas.Parse(Arg.Any<string>(), Arg.Any<ExpressionDialect>())
                 .Returns(call => engine.Parse(call.ArgAt<string>(0), call.ArgAt<ExpressionDialect>(1)));
        _formulas.ExtractDependencies(
                     Arg.Any<ParsedExpression>(),
                     Arg.Any<TemplateVersionSnapshot?>(),
                     Arg.Any<DependencyContext>())
                 .Returns(call => engine.ExtractDependencies(
                     call.ArgAt<ParsedExpression>(0),
                     call.ArgAt<TemplateVersionSnapshot?>(1),
                     call.ArgAt<DependencyContext>(2)));
        _formulas.BuildEvaluationOrder(Arg.Any<IReadOnlyList<FormulaNode>>())
                 .Returns(call => new OrderingResult(
                     true, call.Arg<IReadOnlyList<FormulaNode>>().Select(n => n.FormulaDefId).ToList(), null));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Шістнадцять_категорій_і_правило_публікуються_без_попереджень_про_правило()
    {
        // 5.1: EF_x_ у 16 категоріях + Common-константа; правило задає ключ.
        Setup("@Cat", Sixteen(), formulas: [Formula(1, "T", "CST.EF * 5", MethodologyFormulaScope.Substance)]);

        var diff = await Publish();

        Assert.True(_version.IsPublished);
        Assert.DoesNotContain(diff.Warnings ?? [], w => w.Contains("category", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Версія_де_у_частини_констант_немає_якоїсь_категорії_публікується_без_заповнювачів()
    {
        // L-2, пункт C: Diesel є лише в EF_A, EF_B є лише в Gas. Публікація не вимагає EF_B для Diesel.
        Setup(
            "@Cat",
            [Num("EF_A", 2m, "Diesel"), Num("EF_A", 5m, "Gas"), Num("EF_B", 7m, "Gas")],
            formulas:
            [
                Formula(1, "A", "CST.EF_A * 5", MethodologyFormulaScope.Substance),
                Formula(2, "B", "CST.EF_B * 5", MethodologyFormulaScope.Substance),
            ]);

        await Publish();

        Assert.True(_version.IsPublished);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Кілька_категорій_без_правила_публікуються_але_з_попередженням()
    {
        Setup(null, Sixteen(), formulas: [Formula(1, "T", "CST.EF * 5", MethodologyFormulaScope.Substance)]);

        var diff = await Publish();

        Assert.True(_version.IsPublished);
        var warning = Assert.Single(diff.Warnings ?? [], w => w.Contains("EF", StringComparison.Ordinal));
        Assert.Contains("category rule", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Row_формула_з_категорійною_константою_дає_попередження_коли_правило_є()
    {
        Setup(
            "@Cat",
            Sixteen(),
            formulas:
            [
                Formula(1, "R", "CST.EF * 5", MethodologyFormulaScope.Row),
                Formula(2, "T", "!R * 2", MethodologyFormulaScope.Substance),
            ]);

        var diff = await Publish();

        Assert.Contains(diff.Warnings ?? [], w => w.Contains("Row formula R", StringComparison.Ordinal));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("@Cat +", "publish.problem.categoryRuleInvalid")]
    [InlineData("CST.NOPE", "publish.problem.categoryRuleUnknownConstant")]
    [InlineData("!T", "publish.problem.categoryRuleBadFormula")]
    [InlineData("!NOPE", "publish.problem.categoryRuleBadFormula")]
    [InlineData("1 + 1", "publish.problem.categoryRuleNotText")]
    public async Task Несправне_правило_відхиляє_публікацію_з_названою_проблемою(string rule, string messageKey)
    {
        Setup(rule, Sixteen(), formulas: [Formula(1, "T", "CST.EF * 5", MethodologyFormulaScope.Substance)]);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(Publish);

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        var problems = Assert.IsAssignableFrom<IReadOnlyList<PublishProblem>>(error.Details!["problems"]);
        Assert.Contains(problems, p => p.MessageKey == messageKey);
        Assert.False(_version.IsPublished);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Правило_з_Row_формулою_версії_публікується()
    {
        Setup(
            "!ECW_Category",
            Sixteen(),
            formulas:
            [
                Formula(1, "ECW_Category", "if(@Month >= 4, 'Summer', 'Winter')", MethodologyFormulaScope.Row, text: true),
                Formula(2, "T", "CST.EF * 5", MethodologyFormulaScope.Substance),
            ]);

        await Publish();

        Assert.True(_version.IsPublished);
    }

    /// <summary>
    /// Формула 5.1 <c>!ECW_RepairStatus + '_' + !ECW_Category</c> (простий і обидва захищені варіанти)
    /// публікується: груба оцінка типу дає Text, <c>categoryRuleNotText</c> не виникає.
    /// </summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("!ECW_RepairStatus + '_' + !ECW_Category")]
    [InlineData("if(!ECW_RepairStatus = NULL, '', !ECW_RepairStatus) + '_' + if(!ECW_Category = NULL, '', !ECW_Category)")]
    [InlineData("if(!ECW_RepairStatus = NULL or !ECW_RepairStatus = '', 'NA', !ECW_RepairStatus) + '_' "
                + "+ if(!ECW_Category = NULL or !ECW_Category = '', 'NA', !ECW_Category)")]
    public async Task Правило_5_1_з_плюсом_на_текстах_публікується(string rule)
    {
        Setup(
            rule,
            Sixteen(),
            formulas:
            [
                Formula(1, "ECW_RepairStatus", "'Repair'", MethodologyFormulaScope.Row, text: true),
                Formula(2, "ECW_Category", "'Cat1'", MethodologyFormulaScope.Row, text: true),
                Formula(3, "T", "CST.EF * 5", MethodologyFormulaScope.Substance),
            ]);

        await Publish();

        Assert.True(_version.IsPublished);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private void Setup(string? rule, List<MethodologyConstant> constants, List<MethodologyFormula> formulas)
    {
        _store.GetCategoryRuleAsync(VersionId, Arg.Any<CancellationToken>()).Returns(rule);
        _store.GetConstantsAsync(VersionId, Arg.Any<CancellationToken>()).Returns(constants);
        _store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(formulas);
    }

    private Task<MethodologyPublicationDiff> Publish()
        => new PublishMethodologyHandler(
            _module, _store, _formulas, _bindings, Units(), _uow, _audit, _access, _user, _clock)
            .HandleAsync(VersionId, "L-2", From, CancellationToken.None);

    private static IUnitCatalog Units()
    {
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);
        return units;
    }

    private static MethodologyFormula Formula(
        int id, string code, string expression, MethodologyFormulaScope scope, bool text = false)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create(code), expression);
        formula.SetScope(scope);
        if (text)
        {
            formula.SetResultType(FormulaResultType.Text);
        }

        SetId(formula, id);
        return formula;
    }

    /// <summary>EF у 16 категоріях (Loc/EU × BeforeMR/AfterMR × A…D) і Common-константа K.</summary>
    private static List<MethodologyConstant> Sixteen()
    {
        var constants = new List<MethodologyConstant>();
        var value = 0;
        foreach (var place in new[] { "Loc", "EU" })
        {
            foreach (var repair in new[] { "BeforeMR", "AfterMR" })
            {
                foreach (var cls in new[] { "A", "B", "C", "D" })
                {
                    constants.Add(Num("EF", ++value, $"{place}_{repair}_{cls}"));
                }
            }
        }

        constants.Add(Num("K", 2m, MethodologyConstant.CommonCategory));
        return constants;
    }

    private static MethodologyConstant Num(string code, decimal value, string category)
    {
        var constant = new MethodologyConstant(VersionId, EcrCode.Create(code), value, unitId: 1);
        constant.SetScope(category, null);
        return constant;
    }

    private static void SetId(Entity<int> entity, int id)
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p",
        UserId = Reviewer,
        SecurityStamp = "s",
        Permissions = new HashSet<string>(StringComparer.Ordinal) { "Calculation.Publish" },
        Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(),
        RoleIds = new HashSet<int>(),
    };

    private static List<MethodologyTestCase> TestCases() =>
    [
        new("golden-l2",
            new CalculationInput(
                new MethodologyDescriptor(
                    OwnerId, VersionId, "ECW_C05_01", "1.0.0", CalculationLevel.Configuration,
                    NumericMode.Strict, CalendarMode.Actual, TraceLevel.Off),
                DocumentId: 700,
                TableInstanceId: 500,
                PeriodKey: new PeriodKey(202601),
                SourceRowKey: "E-1",
                Arguments: []),
            new Dictionary<string, decimal> { ["tons"] = 1m },
            Tolerance: 0.000001m),
    ];

    private static CalculationOutput Output(CalculationInput input) =>
        new(input.DocumentId, input.SourceRowKey,
            [new CalculationOutputValue(input.Methodology.MethodologyVersionId, null, "tons", 1m, TonneUnit)],
            []);

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
