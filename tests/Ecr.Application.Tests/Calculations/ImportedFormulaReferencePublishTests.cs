// tests/Ecr.Application.Tests/Calculations/ImportedFormulaReferencePublishTests.cs
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
/// Аудит A3: посилання <c>!Code</c> у формулу імпортованої методології
/// публікація відхиляє — рантайм його не обчислює.
/// </summary>
/// <remarks>
/// ⛔ До виправлення така версія публікувалася (резолвінг у бібліотеку є), а
/// кожен прогін давав <c>#REF</c>: <c>MethodologyEvaluationContext.
/// GetFormulaResult</c> бачить лише формули своєї версії. Вихід мовчки не
/// писався, помилки публікації не було.
/// <para>
/// ⛔ Другий предмет — план прогону. <c>CalculationOrchestrator</c> будує
/// пакети з ПОРОЖНІМИ залежностями між методологіями. Це правильно рівно доти,
/// доки жодна опублікована версія не пише ребра <c>calc.MethodologyDependency</c>,
/// а єдине джерело ребра — саме імпортоване <c>!Code</c>. Тест нижче тримає цей
/// інваріант: прийнята публікація пише порожню множину ребер, відхилена —
/// не пише нічого.
/// </para>
/// </remarks>
public sealed class ImportedFormulaReferencePublishTests
{
    private const int Author = 7;
    private const int Reviewer = 9;
    private const int VersionId = 61;
    private const int OwnerId = 5;
    private const int CommonId = 900;

    private const string Shared = "Common_WtCi_Methane";

    private static readonly DateTime Now = new(2026, 2, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly From = new(2026, 6, 1);

    private readonly IMethodologyStore _store = Substitute.For<IMethodologyStore>();
    private readonly ICalculationBindingStore _bindings = Substitute.For<ICalculationBindingStore>();
    private readonly ICalculationModule _module = Substitute.For<ICalculationModule>();
    private readonly IFormulaEngine _formulas = Substitute.For<IFormulaEngine>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private readonly Methodology _methodology;
    private readonly MethodologyVersion _version;

    /// <summary>Кожен виклик <c>ReplaceDependenciesAsync</c> — множина ребер.</summary>
    private readonly List<IReadOnlyCollection<int>> _edgeWrites = [];

    public ImportedFormulaReferencePublishTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(Reviewer);
        _access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Profile());

        _methodology = new Methodology(EcrCode.Create("HSE400"), Text("Gas composition"));
        _methodology.SetKind(MethodologyKind.Bespoke);
        SetId(_methodology, OwnerId);

        _version = new MethodologyVersion(OwnerId, "1.1.0.0", CalculationLevel.Module, Author, Now);
        SetId(_version, VersionId);
        _methodology.AddVersion(_version);

        _store.FindByVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(_methodology);
        _store.GetTestCasesAsync(VersionId, Arg.Any<CancellationToken>()).Returns(TestCases());
        _store.GetConstantsAsync(VersionId, Arg.Any<CancellationToken>())
              .Returns(new List<MethodologyConstant>());
        _store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>())
              .Returns(new List<MethodologyOutput>());
        _store.GetRulesAsync(VersionId, Arg.Any<CancellationToken>())
              .Returns(new List<MethodologyRule>());
        _bindings.ListAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(new List<CalculationBinding>());

        _store.ReplaceDependenciesAsync(
                  Arg.Any<int>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
              .Returns(call =>
              {
                  _edgeWrites.Add([.. call.ArgAt<IReadOnlyCollection<int>>(1)]);
                  return Task.CompletedTask;
              });

        _module.ExecuteAsync(Arg.Any<CalculationInput>(), Arg.Any<CancellationToken>())
               .Returns(call => Output(call.Arg<CalculationInput>()));

        // ⚠ Розбір і обхід AST — справжні: позиція посилання береться з дерева.
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
                     var nodes = call.Arg<IReadOnlyList<FormulaNode>>();
                     return new OrderingResult(true, nodes.Select(n => n.FormulaDefId).ToList(), null);
                 });
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Посилання_у_формулу_імпортованої_методології_відхиляє_публікацію_з_позицією()
    {
        // ⛔ Червоний на коді до A3: публікація проходила, і `HSE400` щоразу
        // отримувала `#REF` замість `!Common_WtCi_Methane`.
        Formulas([Formula(103, "Total", $"1 + !{Shared} * 2")]);
        Imports([new MethodologyLibrary(CommonId, "Common", 910, [Shared])]);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-0422.publishChecksFailed", error.Details!["messageKey"]);

        var problem = Assert.Single(Assert.IsType<List<PublishProblem>>(error.Details["problems"]));
        Assert.Equal("publish.problem.importedFormulaNotEvaluated", problem.MessageKey);
        Assert.Equal("Total", problem.Args["formula"]);
        Assert.Equal(Shared, problem.Args["name"]);

        // Позиція знака `!` у виразі — як у діагностиках парсера.
        Assert.Equal("4", problem.Args["position"]);

        // Посилання резолвилося саме в бібліотеку, а не «не знайдено».
        Assert.Equal("Common", problem.Args["library"]);

        Assert.False(_version.IsPublished);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    [InlineData("2 * 3", true)]
    [InlineData("!Local * 2", true)]
    [InlineData($"!{Shared} * 2", true)] // своя формула перекриває бібліотечну
    [InlineData("!Other * 2", false)]
    [InlineData("!Local + !Other", false)]
    public async Task Прийнята_публікація_не_пише_ребер_між_методологіями(string expression, bool accepted)
    {
        // ⛔ Інваріант, на якому стоїть порожній план `CalculationOrchestrator`:
        // жодна опублікована версія не залежить від іншої методології. Зніміть
        // заборону A3 — і рядки з `!Other` опублікуються з ребром {900}, а
        // оркестратор далі ставитиме обидві методології в один паралельний пакет.
        Formulas(
        [
            Formula(101, "Local", "1"),
            Formula(102, Shared, "5"),
            Formula(103, "Total", expression),
        ]);
        Imports([new MethodologyLibrary(CommonId, "Common", 910, [Shared, "Other"])]);

        var publish = () => Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None);

        if (accepted)
        {
            await publish();
            Assert.True(_version.IsPublished);

            // Не порожньо за відсутністю виклику: ребра справді записано — нуль.
            Assert.NotEmpty(_edgeWrites);
        }
        else
        {
            await Assert.ThrowsAsync<BusinessRuleException>(publish);
            Assert.False(_version.IsPublished);
        }

        Assert.All(_edgeWrites, Assert.Empty);
    }

    /// <remarks>
    /// ⛔ ФВ-9.14: <c>FORMULA_NOT_FOUND</c> виявляється ПРИ ПУБЛІКАЦІЇ. До виправлення
    /// посилання, що не резолвилося ні у свою версію, ні в імпорт, мовчки
    /// пропускалося (<c>default: break</c>), версія публікувалася, а кожен прогін
    /// давав <c>#REF</c> і не писав вихід.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.14")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Посилання_що_не_веде_нікуди_відхиляє_публікацію_з_позицією(bool withImport)
    {
        Formulas([Formula(101, "Local", "1"), Formula(103, "Total", "!Local + !Missing * 2")]);
        Imports(withImport ? [new MethodologyLibrary(CommonId, "Common", 910, [Shared])] : []);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-0422.publishChecksFailed", error.Details!["messageKey"]);

        var problem = Assert.Single(Assert.IsType<List<PublishProblem>>(error.Details["problems"]));
        Assert.Equal("publish.problem.formulaNotFound", problem.MessageKey);
        Assert.Equal("Total", problem.Args["formula"]);
        Assert.Equal("Missing", problem.Args["name"]);

        // Позиція знака `!` другого посилання: «!Local + » — дев'ять символів.
        Assert.Equal("9", problem.Args["position"]);

        Assert.False(_version.IsPublished);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ─────────────────────────────────────────────────────────────────────────

    private PublishMethodologyHandler Handler()
        => new(_module, _store, _formulas, _bindings, _uow, _audit, _access, _user, _clock);

    private void Formulas(List<MethodologyFormula> formulas)
        => _store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(formulas);

    private void Imports(List<MethodologyLibrary> libraries)
        => _store.ResolveImportsAsync(VersionId, From, Arg.Any<CancellationToken>()).Returns(libraries);

    private static MethodologyFormula Formula(int id, string code, string expression)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create(code), expression);
        SetId(formula, id);
        return formula;
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
