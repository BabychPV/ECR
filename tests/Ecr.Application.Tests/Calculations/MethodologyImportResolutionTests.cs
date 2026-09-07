// tests/Ecr.Application.Tests/Calculations/MethodologyImportResolutionTests.cs
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
/// <c>!Name</c> перетинає межу методології (директива ПК-1 №05, поправка 10).
/// </summary>
/// <remarks>
/// ⛔ Регресія тут ламає дві різні речі, і жодна з них не видна в журналі.
/// Перша: 149 посилань із <c>HSE400</c> і 116 з <c>Flert</c> у <c>Common</c>
/// перестають резолвитися — публікація відхиляє цілком правильний корпус.
/// Друга, тихіша: посилання резолвиться, але ребра
/// <c>calc.MethodologyDependency</c> не з'являється, і <c>HSE400</c> рахується
/// раніше за <c>Common</c>, читаючи торішній результат. Число правдоподібне,
/// помилки немає.
/// </remarks>
public sealed class MethodologyImportResolutionTests
{
    private const int Author = 7;
    private const int Reviewer = 9;
    private const int VersionId = 51;
    private const int OwnerId = 4;

    /// <summary>Бібліотека спільних формул — <c>Common</c> корпусу.</summary>
    private const int CommonId = 900;

    /// <summary>Друга бібліотека з формулою того самого імені — джерело неоднозначності.</summary>
    private const int OtherLibraryId = 901;

    private const int LocalFormulaId = 101;
    private const int ReferencingId = 103;

    private const string Shared = "Common_WtCi_Methane";

    private static readonly DateTime Now = new(2026, 2, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly From = new(2026, 6, 1);

    private readonly IMethodologyStore _store = Substitute.For<IMethodologyStore>();
    private readonly ICalculationModule _module = Substitute.For<ICalculationModule>();
    private readonly IFormulaEngine _formulas = Substitute.For<IFormulaEngine>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private readonly Methodology _methodology;

    /// <summary>Вузли, з якими публікація пішла в топологічне сортування.</summary>
    private IReadOnlyList<FormulaNode> _nodes = [];

    /// <summary>Ребра між методологіями, які публікація записала.</summary>
    private IReadOnlyCollection<int> _edges = [];

    public MethodologyImportResolutionTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(Reviewer);
        _access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Profile());

        _methodology = new Methodology(EcrCode.Create("HSE400"), Text("Gas composition"));
        _methodology.SetKind(MethodologyKind.Bespoke);
        SetId(_methodology, OwnerId);

        var version = new MethodologyVersion(
            OwnerId, "1.1.0.0", CalculationLevel.Module, Author, Now);

        SetId(version, VersionId);
        _methodology.AddVersion(version);

        _store.FindByVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(_methodology);
        _store.GetTestCasesAsync(VersionId, Arg.Any<CancellationToken>()).Returns(TestCases());
        _store.GetConstantsAsync(VersionId, Arg.Any<CancellationToken>())
              .Returns(new List<MethodologyConstant>());
        _store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>())
              .Returns(new List<MethodologyOutput>());
        _store.GetRulesAsync(VersionId, Arg.Any<CancellationToken>())
              .Returns(new List<MethodologyRule>());

        _store.ReplaceDependenciesAsync(
                  Arg.Any<int>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
              .Returns(call =>
              {
                  _edges = call.ArgAt<IReadOnlyCollection<int>>(1);
                  return Task.CompletedTask;
              });

        _module.ExecuteAsync(Arg.Any<CalculationInput>(), Arg.Any<CancellationToken>())
               .Returns(call => Output(call.Arg<CalculationInput>()));

        // ⚠ Розбір і обхід AST — СПРАВЖНІ: саме з них публікація дізнається,
        // на що посилається вираз.
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
                 .Returns(call =>
                 {
                     _nodes = call.Arg<IReadOnlyList<FormulaNode>>();
                     return new OrderingResult(true, _nodes.Select(n => n.FormulaDefId).ToList(), null);
                 });
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Посилання_у_бібліотеку_резолвиться_і_дає_ребро_між_методологіями()
    {
        // ⛔ Упаде, якщо `!` знову шукатиме лише у власній версії: 265 посилань
        // корпусу стануть нерезолвленими. І впаде вдруге, якщо резолвінг є, а
        // ребра немає, — тоді `HSE400` порахується раніше за `Common`.
        Formulas([Formula(ReferencingId, "Total", $"!{Shared} * 2")]);
        Imports([Library(CommonId, "Common", 910, [Shared])]);

        await Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None);

        Assert.Equal([CommonId], _edges);

        // ⚠ Формула бібліотеки НЕ входить у топологічний порядок цієї версії:
        // її рахує своя методологія, і чуже ребро тут стало б посиланням на
        // ідентифікатор із іншої нумерації.
        Assert.Empty(_nodes.Single(n => n.FormulaDefId == ReferencingId).DependsOnFormulaDefIds);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Своя_формула_виграє_над_бібліотечною()
    {
        // ⚠ Не неоднозначність, а навмисне перекриття: саме так методологія
        // відходить від спільної поведінки, не форкаючи `Common`.
        Formulas([Formula(LocalFormulaId, Shared, "1"), Formula(ReferencingId, "Total", $"!{Shared} * 2")]);
        Imports([Library(CommonId, "Common", 910, [Shared])]);

        await Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None);

        Assert.Equal([LocalFormulaId], _nodes.Single(n => n.FormulaDefId == ReferencingId).DependsOnFormulaDefIds);

        // Ребра між методологіями немає: посилання нікуди за межу не пішло.
        Assert.Empty(_edges);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Однакова_формула_у_двох_бібліотеках_відхиляє_публікацію()
    {
        // ⛔ «Перший за списком» означав би, що число залежить від порядку
        // рядків у `calc.MethodologyImport` і змінюється від переіндексації.
        // Саме тому в оголошенні імпорту немає поля порядку.
        Formulas([Formula(ReferencingId, "Total", $"!{Shared} * 2")]);
        Imports(
        [
            Library(CommonId, "Common", 910, [Shared]),
            Library(OtherLibraryId, "Flert", 911, [Shared]),
        ]);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Contains("Common", error.Message, StringComparison.Ordinal);
        Assert.Contains("Flert", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Імпорт_без_чинної_на_дату_версії_відхиляє_публікацію()
    {
        // ⛔ Версія бібліотеки добирається на ДАТУ ПЕРІОДУ, як будь-яка інша.
        // Мовчазний пропуск такого імпорту перетворив би «бібліотеки не видно»
        // на «формули не існує» — і методолог шукав би одруку в імені.
        Formulas([Formula(ReferencingId, "Total", "1 + 2")]);
        Imports([Library(CommonId, "Common", null, [])]);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Contains("Common", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-13.3")]
    public async Task Бібліотека_з_правилом_прив_язки_відхиляється()
    {
        // ⛔ Бібліотека не рахує ні для кого: правило прив'язки на ній означає,
        // що планувальник запускатиме `Common` окремо, з порожнім набором
        // аргументів, і щоночі писатиме нулі або помилку.
        _methodology.SetKind(MethodologyKind.Library);
        Formulas([Formula(ReferencingId, "Total", "1 + 2")]);
        Imports([]);

        _store.GetRulesAsync(VersionId, Arg.Any<CancellationToken>()).Returns(
            new List<MethodologyRule> { new(VersionId, EcrCode.Create("Rule_017"), "{}", 100) });

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Contains("бібліотекою", error.Message, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private PublishMethodologyHandler Handler()
        => new(_module, _store, _formulas, _uow, _audit, _access, _user, _clock);

    private void Formulas(List<MethodologyFormula> formulas)
        => _store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(formulas);

    private void Imports(List<MethodologyLibrary> libraries)
        => _store.ResolveImportsAsync(VersionId, From, Arg.Any<CancellationToken>()).Returns(libraries);

    private static MethodologyLibrary Library(
        int methodologyId, string code, int? versionId, IReadOnlyList<string> formulaCodes)
        => new(methodologyId, code, versionId, formulaCodes);

    private static MethodologyFormula Formula(int id, string code, string expression)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create(code), expression);
        SetId(formula, id);
        return formula;
    }

    /// <summary>Ідентифікатор дає база; у тесті — руками, бо за ним зіставляється граф.</summary>
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
        new("golden-hse400",
            new CalculationInput(
                new MethodologyDescriptor(
                    OwnerId, VersionId, "HSE400", "1.1.0.0", CalculationLevel.Module,
                    NumericMode.Legacy, CalendarMode.Actual, TraceLevel.Off),
                DocumentId: 700,
                TableInstanceId: 500,
                PeriodKey: new PeriodKey(202601),
                SourceRowKey: "7001001",
                Arguments: []),
            new Dictionary<string, decimal> { ["tons"] = 1m },
            Tolerance: 0.000001m),
    ];

    private static CalculationOutput Output(CalculationInput input) =>
        new(input.DocumentId, input.SourceRowKey,
            [new CalculationOutputValue(input.Methodology.MethodologyVersionId, null, "tons", 1m, 8)],
            []);

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
