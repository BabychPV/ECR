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
/// Публікація посилань <c>!Code</c> у формулу імпортованої методології (HSE301 L; до нього —
/// заборона аудиту A3).
/// </summary>
/// <remarks>
/// ✎ HSE301 L: модуль обчислює формулу бібліотеки в контексті рядка викликача, тож
/// посилання публікується — з ребром <c>calc.MethodologyDependency</c> для інвалідації. Ребро
/// більше не означає «читає записаний результат іншої методології»: результат бібліотеки не
/// читається, тому порожні залежності плану <c>CalculationOrchestrator</c> на число не
/// впливають.
/// <para>
/// Замість заборони — перевірки того самого замикання, яке рахуватиме прогін. Мутаційні
/// докази (кожну перевірку прибрано окремо → свій тест червоний, після відкату — зелений):
/// <c>ImportCycleAsync</c> → <see cref="Цикл_імпортів_до_власної_методології_відхиляє_публікацію"/>;
/// режими в <c>LibraryProblems</c> → <see cref="Інші_режими_бібліотеки_відхиляють_публікацію"/>;
/// область у <c>LibraryProblems</c> → <see cref="Row_формула_з_посиланням_на_формулу_речовини_бібліотеки_відхиляється"/>;
/// <c>LibraryFormulas</c> у перевірці колонок → <see cref="Аргумент_формули_бібліотеки_звіряється_з_колонками_викликача"/>.
/// </para>
/// </remarks>
public sealed class ImportedFormulaReferencePublishTests
{
    private const int Author = 7;
    private const int Reviewer = 9;
    private const int VersionId = 61;
    private const int OwnerId = 5;
    private const int CommonId = 900;
    private const int CommonVersionId = 910;

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
        _store.ResolveImportsAsync(CommonVersionId, From, Arg.Any<CancellationToken>())
              .Returns(new List<MethodologyLibrary>());
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
    public async Task Посилання_у_формулу_бібліотеки_публікується_з_ребром_між_методологіями()
    {
        // ⛔ До HSE301 L тут була відмова `importedFormulaNotEvaluated`: рантайм бібліотечних
        // формул не обчислював. Тепер обчислює — і ребро пишеться для інвалідації.
        Formulas([Formula(103, "Total", $"1 + !{Shared} * 2")]);
        Library(Formula(301, Shared, "@Flow * 2", CommonVersionId));

        await Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None);

        Assert.True(_version.IsPublished);
        Assert.Equal([CommonId], Assert.Single(_edgeWrites));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    [InlineData("2 * 3", false)]
    [InlineData("!Local * 2", false)]
    [InlineData($"!{Shared} * 2", false)] // своя формула перекриває бібліотечну
    [InlineData("!Other * 2", true)]
    [InlineData("!Local + !Other", true)]
    public async Task Ребро_між_методологіями_пишеться_рівно_тоді_коли_посилання_веде_в_бібліотеку(
        string expression, bool edge)
    {
        Formulas(
        [
            Formula(101, "Local", "1"),
            Formula(102, Shared, "5"),
            Formula(103, "Total", expression),
        ]);
        Library(Formula(301, Shared, "7", CommonVersionId), Formula(302, "Other", "3", CommonVersionId));

        await Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None);

        Assert.True(_version.IsPublished);

        int[] expected = edge ? [CommonId] : [];
        Assert.Equal(expected, Assert.Single(_edgeWrites));
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
        if (withImport)
        {
            Library(Formula(301, Shared, "7", CommonVersionId));
        }
        else
        {
            Imports([]);
        }

        var problem = Assert.Single(await ProblemsAsync());
        Assert.Equal("publish.problem.formulaNotFound", problem.MessageKey);
        Assert.Equal("Total", problem.Args["formula"]);
        Assert.Equal("Missing", problem.Args["name"]);

        // Позиція знака `!` другого посилання: «!Local + » — дев'ять символів.
        Assert.Equal("9", problem.Args["position"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Цикл_імпортів_до_власної_методології_відхиляє_публікацію()
    {
        // Common імпортує Base, Base імпортує саму HSE400.
        Formulas([Formula(103, "Total", $"!{Shared} * 2")]);
        Library(Formula(301, Shared, "7", CommonVersionId));
        _store.ResolveImportsAsync(CommonVersionId, From, Arg.Any<CancellationToken>())
              .Returns(new List<MethodologyLibrary> { new(901, "Base", 920, ["Base_K"]) });
        _store.ResolveImportsAsync(920, From, Arg.Any<CancellationToken>())
              .Returns(new List<MethodologyLibrary> { new(OwnerId, "HSE400", VersionId, ["Total"]) });

        var problem = Assert.Single(await ProblemsAsync());
        Assert.Equal("publish.problem.importCycle", problem.MessageKey);
        Assert.Equal("HSE400 → Common → Base → HSE400", problem.Args["chain"]);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData(NumericMode.Strict, CalendarMode.Actual)]
    [InlineData(NumericMode.Legacy, CalendarMode.Fixed365)]
    public async Task Інші_режими_бібліотеки_відхиляють_публікацію(NumericMode numeric, CalendarMode calendar)
    {
        // Версія — Legacy/Actual (типові); бібліотека рахує інакше хоч в одному режимі.
        Formulas([Formula(103, "Total", $"!{Shared} * 2")]);
        Library(numeric, calendar, Formula(301, Shared, "7", CommonVersionId));

        var problem = Assert.Single(await ProblemsAsync());
        Assert.Equal("publish.problem.importModeMismatch", problem.MessageKey);
        Assert.Equal("Common", problem.Args["library"]);
        Assert.Equal(numeric.ToString(), problem.Args["libraryNumeric"]);
        Assert.Equal(calendar.ToString(), problem.Args["libraryCalendar"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Row_формула_з_посиланням_на_формулу_речовини_бібліотеки_відхиляється()
    {
        var total = Formula(103, "Total", $"!{Shared} * 2");
        total.SetScope(MethodologyFormulaScope.Row);
        Formulas([total]);

        var shared = Formula(301, Shared, "7", CommonVersionId);
        shared.SetScope(MethodologyFormulaScope.Substance);
        Library(shared);

        var problem = Assert.Single(await ProblemsAsync());
        Assert.Equal("publish.problem.rowScopeReferencesLibrarySubstance", problem.MessageKey);
        Assert.Equal("Total", problem.Args["formula"]);
        Assert.Equal(Shared, problem.Args["name"]);
        Assert.Equal("Common", problem.Args["library"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Аргумент_формули_бібліотеки_звіряється_з_колонками_викликача()
    {
        // Формула бібліотеки читає @Flow з рядка ВИКЛИКАЧА; колонки Flow у його таблиці немає —
        // у рантаймі це був би #ARG.
        Formulas([Formula(103, "Total", $"!{Shared} * 2")]);
        Library(Formula(301, Shared, "@Flow * 2", CommonVersionId));

        _bindings.ListAsync(OwnerId, Arg.Any<CancellationToken>())
                 .Returns(new List<CalculationBinding> { new(44, 4401, OwnerId, "Total", "{}") });
        _bindings.ListColumnCodesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                 .Returns(new Dictionary<int, IReadOnlyList<string>> { [44] = ["Volume"] });

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None));

        Assert.Equal("ECR-CALC-0438", error.ErrorCode);
        Assert.Contains("@Flow", error.Message, StringComparison.Ordinal);
        Assert.False(_version.IsPublished);
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <remarks>
    /// F-1 (AN-5): <c>!X</c> через межу методології — формула бібліотеки, яка сама посилається на
    /// іншу формулу тієї ж бібліотеки, публікується (замикання бере обидві, імен не втрачено).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Формула_бібліотеки_що_посилається_на_іншу_формулу_бібліотеки_публікується()
    {
        Formulas([Formula(103, "Total", $"!{Shared} * 2")]);
        Library(Formula(301, Shared, "!Base * 3", CommonVersionId), Formula(302, "Base", "5", CommonVersionId));

        await Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None);

        Assert.True(_version.IsPublished);
        Assert.Equal([CommonId], Assert.Single(_edgeWrites));
    }

    /// <remarks>
    /// F-2 за межею (AN-5): нерезолвне <c>!Ghost</c> у ФОРМУЛІ БІБЛІОТЕКИ — відмова публікації
    /// <c>ECR-CALC-0422</c> з переліком, а не мовчання й <c>#REF</c> у кожному прогоні.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.14")]
    public async Task Нерезолвне_посилання_у_формулі_бібліотеки_відхиляє_публікацію_з_переліком()
    {
        Formulas([Formula(103, "Total", $"!{Shared} * 2")]);
        Library(Formula(301, Shared, "!Base * 3 + !Ghost", CommonVersionId), Formula(302, "Base", "5", CommonVersionId));

        var problem = Assert.Single(await ProblemsAsync());
        Assert.Equal("publish.problem.formulaNotFound", problem.MessageKey);
        Assert.Equal($"Common.{Shared}", problem.Args["formula"]);
        Assert.Equal("Ghost", problem.Args["name"]);
    }

    private async Task<List<PublishProblem>> ProblemsAsync()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-0422.publishChecksFailed", error.Details!["messageKey"]);
        Assert.False(_version.IsPublished);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());

        return Assert.IsType<List<PublishProblem>>(error.Details["problems"]);
    }

    private PublishMethodologyHandler Handler()
        => new(
            _module, _store, _formulas, _bindings, EmptyUnits(), _uow, _audit, _access, _user, _clock);

    /// <summary>Порожній довідник одиниць: одиниці тут не предмет (їх веде <c>MethodologyUnitPublishTests</c>).</summary>
    private static IUnitCatalog EmptyUnits()
    {
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);
        return units;
    }

    private void Formulas(List<MethodologyFormula> formulas)
        => _store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(formulas);

    private void Imports(List<MethodologyLibrary> libraries)
        => _store.ResolveImportsAsync(VersionId, From, Arg.Any<CancellationToken>()).Returns(libraries);

    /// <summary><c>Common</c> v910 у тих самих режимах, що й версія (Legacy/Actual).</summary>
    private void Library(params MethodologyFormula[] formulas)
        => Library(NumericMode.Legacy, CalendarMode.Actual, formulas);

    private void Library(NumericMode numeric, CalendarMode calendar, params MethodologyFormula[] formulas)
    {
        var library = new MethodologyLibrary(CommonId, "Common", CommonVersionId, [.. formulas.Select(f => f.Code)]);
        Imports([library]);
        _store.GetLibraryContentsAsync(VersionId, From, Arg.Any<CancellationToken>())
              .Returns(new List<MethodologyLibraryContent>
              {
                  new(library, numeric, calendar, formulas, []),
              });
    }

    private static MethodologyFormula Formula(int id, string code, string expression, int versionId = VersionId)
    {
        var formula = new MethodologyFormula(versionId, EcrCode.Create(code), expression);
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
