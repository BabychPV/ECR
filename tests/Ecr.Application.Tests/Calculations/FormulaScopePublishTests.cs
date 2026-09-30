// tests/Ecr.Application.Tests/Calculations/FormulaScopePublishTests.cs
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
/// Перевірки публікації для області формули й видимості (HSE301 A3a, <c>D-175</c>,
/// <c>D-176</c>; FEATURE-HSE301-VIEW §6.1, §7.1).
/// </summary>
/// <remarks>
/// ⛔ Рушій рахує Row-формулу раз на рядок, БЕЗ речовини. Посилання на формулу речовини,
/// на константу, задану по речовинах, чи <c>SUBSTANCE(…)</c> там не впало б — воно дало
/// б <c>#REF</c> і тихо не записаний вихід. Тому це відмова публікації.
///
/// Мутаційні докази: прибрати <c>RejectScopeViolations</c> з публікації — червоні
/// всі відмови нижче (версія публікується).
/// </remarks>
public sealed class FormulaScopePublishTests
{
    private const int Author = 7;
    private const int Reviewer = 9;
    private const int VersionId = 71;
    private const int OwnerId = 6;
    private const int TonneUnit = 8;
    private const long Substance = 901;

    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly From = new(2026, 10, 1);

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

    private List<MethodologyOutput> _outputs = [];

    public FormulaScopePublishTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(Reviewer);
        _access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Profile());

        var methodology = new Methodology(EcrCode.Create("HSE301"), Text("Flare"));
        SetId(methodology, OwnerId);

        _version = new MethodologyVersion(OwnerId, "1.0.0", CalculationLevel.Configuration, Author, Now);

        // ⚠ Strict: `SUBSTANCE` — функція ярусу Extension, і в Legacy її відхилила б
        // інша перевірка (`ECR-CALC-0433`) раніше за цю.
        _version.SetModes(NumericMode.Strict, CalendarMode.Actual, TraceLevel.ErrorsOnly);
        SetId(_version, VersionId);
        methodology.AddVersion(_version);

        _store.FindByVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(methodology);
        _store.GetTestCasesAsync(VersionId, Arg.Any<CancellationToken>()).Returns(TestCases());
        _store.GetConstantsAsync(VersionId, Arg.Any<CancellationToken>()).Returns(Constants());
        _store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>()).Returns(_ => _outputs);
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

    /// <remarks>
    /// Row-формули на Row-формулі й загальній константі, формула речовини — на Row і
    /// константі речовини: саме так складено 301 (§6.3). Публікується.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Правильні_області_публікуються()
    {
        Formulas(
        [
            Formula(1, "V", "CST.G * 3", MethodologyFormulaScope.Row),
            Formula(2, "M", "!V * 2", MethodologyFormulaScope.Row, visible: true, unit: TonneUnit),
            Formula(3, "T", "!M * CST.K", MethodologyFormulaScope.Substance),
        ]);

        await Publish();

        Assert.True(_version.IsPublished);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    [InlineData("!T * 2", "!T")]
    [InlineData("CST.K * 2", "CST.K")]
    [InlineData("SUBSTANCE('T') * 2", "SUBSTANCE()")]
    [InlineData("if(1 > 0, 1, !T)", "!T")]
    public async Task Row_формула_з_посиланням_лише_для_речовини_відхиляється(string expression, string reference)
    {
        Formulas(
        [
            Formula(1, "T", "CST.K * 5", MethodologyFormulaScope.Substance),
            Formula(2, "M", expression, MethodologyFormulaScope.Row),
        ]);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(Publish);

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-0422.rowScopeReferencesSubstance", error.Details!["messageKey"]);
        Assert.Equal("M", error.Details["formula"]);
        Assert.Equal(reference, error.Details["reference"]);
        Assert.False(_version.IsPublished);
    }

    /// <remarks>
    /// ⚠ Транзитивність без окремого обходу: A (Row) → B (Row) → T (Substance) відхиляється
    /// на B — прямому порушнику, і тоді A теж не отримує значення речовини.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Транзитивне_посилання_відхиляється_на_прямому_порушнику()
    {
        Formulas(
        [
            Formula(1, "T", "CST.K * 5", MethodologyFormulaScope.Substance),
            Formula(2, "B", "!T + 1", MethodologyFormulaScope.Row),
            Formula(3, "A", "!B * 2", MethodologyFormulaScope.Row),
        ]);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(Publish);

        Assert.Equal("err.ECR-CALC-0422.rowScopeReferencesSubstance", error.Details!["messageKey"]);
        Assert.Equal("B", error.Details["formula"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-16.6")]
    public async Task Видима_формула_без_одиниці_відхиляється()
    {
        Formulas([Formula(1, "V", "CST.G * 3", MethodologyFormulaScope.Row, visible: true)]);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(Publish);

        Assert.Equal("err.ECR-CALC-0422.visibleFormulaNoUnit", error.Details!["messageKey"]);
        Assert.Equal("V", error.Details["formula"]);
        Assert.False(_version.IsPublished);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Вихід_раз_на_рядок_з_формули_речовини_відхиляється()
    {
        Formulas([Formula(1, "T", "CST.K * 5", MethodologyFormulaScope.Substance, unit: TonneUnit)]);

        var output = new MethodologyOutput(VersionId, EcrCode.Create("T"), TonneUnit);
        output.SetPerSubstance(false);
        _outputs = [output];

        var error = await Assert.ThrowsAsync<BusinessRuleException>(Publish);

        Assert.Equal("err.ECR-CALC-0422.rowOutputFromSubstanceFormula", error.Details!["messageKey"]);
        Assert.Equal("T", error.Details["output"]);
        Assert.False(_version.IsPublished);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private Task<MethodologyPublicationDiff> Publish()
        => new PublishMethodologyHandler(_module, _store, _formulas, _bindings, _uow, _audit, _access, _user, _clock)
            .HandleAsync(VersionId, "HSE301 A3a", From, CancellationToken.None);

    private void Formulas(List<MethodologyFormula> formulas)
        => _store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(formulas);

    private static MethodologyFormula Formula(
        int id, string code, string expression, MethodologyFormulaScope scope, bool visible = false, int? unit = null)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create(code), expression);
        formula.SetScope(scope);
        formula.SetVisible(visible);

        if (unit is { } unitId)
        {
            formula.SetOutputUnit(unitId);
        }

        SetId(formula, id);
        return formula;
    }

    /// <summary><c>G</c> — загальна, <c>K</c> — задана по речовині.</summary>
    private static List<MethodologyConstant> Constants()
    {
        var general = new MethodologyConstant(VersionId, EcrCode.Create("G"), 1m, unitId: 1);
        var bySubstance = new MethodologyConstant(VersionId, EcrCode.Create("K"), 0.02m, unitId: 1);
        bySubstance.SetScope(null, Substance);
        return [general, bySubstance];
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
        new("golden-a3a",
            new CalculationInput(
                new MethodologyDescriptor(
                    OwnerId, VersionId, "HSE301", "1.0.0", CalculationLevel.Configuration,
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
