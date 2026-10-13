// tests/Ecr.Application.Tests/Calculations/LiveTemplateVersionArgumentPublishTests.cs
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
/// D-400: <c>ECR-CALC-0438</c> звіряє аргументи з колонками лише «живої» (останньої опублікованої) версії
/// шаблону. Колонка, додана тільки в нову версію, не блокує публікацію через TableDef старих версій, на які
/// клон скопіював прив'язки; аргумент, якого немає й у живій версії, лишається під 0438.
/// </summary>
/// <remarks>
/// ⛔ Мутація: у <see cref="LiveBindingTables.ResolveAsync"/> повернути <c>all</c> замість <c>liveSet</c> —
/// червоні всі тести, крім fallback-тесту «Без_прив_язок_на_живій_версії…» (перевірено 4 з 5).
/// </remarks>
public sealed class LiveTemplateVersionArgumentPublishTests
{
    private const int Author = 7;
    private const int Reviewer = 9;
    private const int VersionId = 81;
    private const int OwnerId = 6;
    private const int TonneUnit = 8;

    /// <summary>TableDef v1 (стара версія шаблону): колонки без нової <c>X</c>.</summary>
    private const int OldTable = 10;

    /// <summary>TableDef v2 (жива версія): колонки v1 + нова <c>X</c>.</summary>
    private const int LiveTable = 20;

    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
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

    public LiveTemplateVersionArgumentPublishTests()
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
        _store.GetConstantsAsync(VersionId, Arg.Any<CancellationToken>()).Returns(new List<MethodologyConstant>());
        _store.GetCategoryRuleAsync(VersionId, Arg.Any<CancellationToken>()).Returns((string?)null);
        _store.ResolveImportsAsync(VersionId, From, Arg.Any<CancellationToken>())
              .Returns(new List<MethodologyLibrary>());

        // Шаблон на два TableDef: клон v2 скопіював прив'язку методології, старий TableDef не прибрано.
        _bindings.ListAsync(OwnerId, Arg.Any<CancellationToken>()).Returns(
        [
            new CalculationBinding(OldTable, columnDefId: 1, OwnerId, "tons", "{}"),
            new CalculationBinding(LiveTable, columnDefId: 2, OwnerId, "tons", "{}"),
        ]);

        // Сховище звітує колонками ЗАПИТАНИХ таблиць (як справжнє): рівно те, що попросив обробник.
        var columns = new Dictionary<int, IReadOnlyList<string>>
        {
            [OldTable] = ["Fuel", "Op"],
            [LiveTable] = ["Fuel", "Op", "X"],
        };
        _bindings.ListColumnCodesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<int, IReadOnlyList<string>>)call.Arg<IReadOnlyCollection<int>>()
                .Where(columns.ContainsKey)
                .ToDictionary(id => id, id => columns[id]));

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
    public async Task Колонка_лише_в_живій_версії_шаблону_не_блокує_публікацію()
    {
        LiveIs(LiveTable);
        Formulas("@X * 2");

        await Publish();

        Assert.True(_version.IsPublished);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Аргумент_якого_немає_і_в_живій_версії_лишається_0438()
    {
        LiveIs(LiveTable);
        Formulas("@X * @Ghost");

        var error = await Assert.ThrowsAsync<BusinessRuleException>(Publish);

        Assert.Equal("ECR-CALC-0438", error.ErrorCode);
        Assert.Contains("@Ghost", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("@X", error.Message, StringComparison.Ordinal);
        Assert.False(_version.IsPublished);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Без_прив_язок_на_живій_версії_перевірка_як_раніше_по_всіх_таблицях()
    {
        // Сховище не знайшло живої таблиці серед прив'язок (порожня відповідь = fallback) — звіряються обидві
        // таблиці, і @X, якого немає в старій, дає 0438: перевірка не слабшає там, де живої версії немає.
        LiveIs();
        Formulas("@X * 2");

        var error = await Assert.ThrowsAsync<BusinessRuleException>(Publish);

        Assert.Equal("ECR-CALC-0438", error.ErrorCode);
        Assert.Contains("@X", error.Message, StringComparison.Ordinal);
        await _bindings.Received().ListColumnCodesAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.Contains(OldTable) && ids.Contains(LiveTable)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Правило_категорії_на_публікації_теж_звіряється_лише_з_живою_версією()
    {
        LiveIs(LiveTable);
        Formulas("@Fuel * 2");
        _store.GetCategoryRuleAsync(VersionId, Arg.Any<CancellationToken>()).Returns("@X");

        await Publish();

        Assert.True(_version.IsPublished);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Правило_категорії_на_збереженні_теж_звіряється_лише_з_живою_версією()
    {
        // MethodologyCategoryRuleHandlers.RequireReferencesAsync: окремий виклик ListColumnCodesAsync.
        LiveIs(LiveTable);
        Formulas("@Fuel * 2");
        var drafts = Substitute.For<IMethodologyDraftStore>();
        drafts.FindVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(_version);
        var handler = new SaveMethodologyCategoryRuleHandler(
            drafts, _store, _bindings, _formulas, _uow, _access, _user, _clock);

        await handler.HandleAsync(VersionId, "@X", CancellationToken.None);

        drafts.Received().Add(Arg.Any<MethodologyCategoryRule>());

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync(VersionId, "@Ghost", CancellationToken.None));
        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private void LiveIs(params int[] live)
        => _bindings.ListLiveTableDefIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(live);

    private void Formulas(string expression)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create("T"), expression);
        formula.SetScope(MethodologyFormulaScope.Substance);
        SetId(formula, 1);
        _store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(new List<MethodologyFormula> { formula });
    }

    private Task<MethodologyPublicationDiff> Publish()
        => new PublishMethodologyHandler(
            _module, _store, _formulas, _bindings, Units(), _uow, _audit, _access, _user, _clock)
            .HandleAsync(VersionId, "D-400", From, CancellationToken.None);

    private static IUnitCatalog Units()
    {
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);
        return units;
    }

    private static void SetId(Entity<int> entity, int id)
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p",
        UserId = Reviewer,
        SecurityStamp = "s",
        Permissions = new HashSet<string>(StringComparer.Ordinal)
        {
            "Calculation.Publish",
            SaveMethodologyCategoryRuleHandler.Permission,
        },
        Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(),
        RoleIds = new HashSet<int>(),
    };

    private static List<MethodologyTestCase> TestCases() =>
    [
        new("golden-d399",
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
