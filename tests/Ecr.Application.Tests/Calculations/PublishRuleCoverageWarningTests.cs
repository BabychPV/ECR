// tests/Ecr.Application.Tests/Calculations/PublishRuleCoverageWarningTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Common;
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
/// ФВ-13.9 (AN-13): публікація методології повідомляє про ПОКРИТТЯ правил на реальних даних - комбінації значень
/// рядків таблиць прив'язок, яких не зачіпає жодне правило версії, - попередженням у відповіді публікації.
/// </summary>
/// <remarks>
/// Матриця - та сама, що й на екрані покриття (<see cref="RuleCoverageHandler"/>): вікно періодів, проєкти, які той,
/// хто публікує, може читати. Перетин (нічию за пріоритетом) блокує раніше V-18 (<c>RequireConsistentSet</c>).
///
/// Мутаційний доказ: прибрати виклик <c>RuleCoverageWarningsAsync</c> у <c>PublishMethodologyHandler.HandleAsync</c>
/// - червоний <see cref="Рядки_без_правила_дають_попередження_а_публікація_проходить"/>; прибрати
/// <c>catch (AccessDeniedException)</c> - червоний <see cref="Без_права_перегляду_даних_попередження_немає_і_дані_не_читаються"/>.
/// </remarks>
public sealed class PublishRuleCoverageWarningTests
{
    private const int Author = 7;
    private const int Reviewer = 9;
    private const int VersionId = 81;
    private const int OwnerId = 6;
    private const int TableDefId = 3;
    private const int ColumnDefId = 42;
    private const int TonneUnit = 8;

    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly From = new(2026, 11, 1);

    private readonly IMethodologyStore _store = Substitute.For<IMethodologyStore>();
    private readonly IMethodologyDraftStore _drafts = Substitute.For<IMethodologyDraftStore>();
    private readonly ICalculationBindingStore _bindings = Substitute.For<ICalculationBindingStore>();
    private readonly IRuleCoverageReader _reader = Substitute.For<IRuleCoverageReader>();
    private readonly ICalculationModule _module = Substitute.For<ICalculationModule>();
    private readonly IFormulaEngine _formulas = Substitute.For<IFormulaEngine>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private readonly MethodologyVersion _version;

    public PublishRuleCoverageWarningTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(Reviewer);
        Profile(withView: true);

        var methodology = new Methodology(EcrCode.Create("ECW_C05_02"), Text("Land 5.2"));
        SetId(methodology, OwnerId);
        _version = new MethodologyVersion(OwnerId, "1.0.0", CalculationLevel.Configuration, Author, Now);
        _version.SetModes(NumericMode.Strict, CalendarMode.Actual, TraceLevel.ErrorsOnly);
        SetId(_version, VersionId);
        methodology.AddVersion(_version);

        _store.FindByVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(methodology);
        _store.GetTestCasesAsync(VersionId, Arg.Any<CancellationToken>()).Returns(TestCases());
        _store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>()).Returns(new List<MethodologyOutput>());
        _store.GetRulesAsync(VersionId, Arg.Any<CancellationToken>())
              .Returns(new List<MethodologyRule>
              {
                  new(VersionId, EcrCode.Create("CO2"), $$"""{"{{ColumnDefId}}":"CO2"}""", 10),
              });
        _store.GetConstantsAsync(VersionId, Arg.Any<CancellationToken>()).Returns(new List<MethodologyConstant>());
        _store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(new List<MethodologyFormula>());
        _store.ResolveImportsAsync(VersionId, From, Arg.Any<CancellationToken>()).Returns(new List<MethodologyLibrary>());
        _drafts.GetAllVersionsAsync(OwnerId, Arg.Any<CancellationToken>())
               .Returns((IReadOnlyList<MethodologyVersion>)[_version]);
        _bindings.ListAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(new List<CalculationBinding> { new(TableDefId, 50, OwnerId, "OUT1", "{}") });

        var engine = new RealFormulaEngine();
        _formulas.Parse(Arg.Any<string>(), Arg.Any<ExpressionDialect>())
                 .Returns(call => engine.Parse(call.ArgAt<string>(0), call.ArgAt<ExpressionDialect>(1)));
        _formulas.BuildEvaluationOrder(Arg.Any<IReadOnlyList<FormulaNode>>())
                 .Returns(call => new OrderingResult(
                     true, call.Arg<IReadOnlyList<FormulaNode>>().Select(n => n.FormulaDefId).ToList(), null));
        _module.ExecuteAsync(Arg.Any<CalculationInput>(), Arg.Any<CancellationToken>())
               .Returns(call => Task.FromResult(Output(call.Arg<CalculationInput>(), 1m)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.9")]
    public async Task Рядки_без_правила_дають_попередження_а_публікація_проходить()
    {
        // CO2 - під правилом; CH4 (3 рядки) і порожнє значення (2 рядки) - жодне правило не зачіпає.
        Combinations(("CO2", 4), ("CH4", 3), (null, 2));

        var diff = await Publish();

        Assert.True(_version.IsPublished);
        var warning = Assert.Single(diff.Warnings ?? [], w => w.StartsWith("Rule coverage", StringComparison.Ordinal));
        Assert.Contains("2 value combinations (5 rows)", warning, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.9")]
    public async Task Повне_покриття_попередження_не_дає()
    {
        Combinations(("CO2", 4));

        var diff = await Publish();

        Assert.True(_version.IsPublished);
        Assert.DoesNotContain(diff.Warnings ?? [], w => w.StartsWith("Rule coverage", StringComparison.Ordinal));
    }

    /// <remarks>
    /// Матриця - дані проєктів (аудит S7). Без <c>Calculation.View</c> її не видно на екрані - не видно й через
    /// публікацію; сама публікація від цього не падає.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.9")]
    public async Task Без_права_перегляду_даних_попередження_немає_і_дані_не_читаються()
    {
        Profile(withView: false);
        Combinations(("CH4", 3));

        var diff = await Publish();

        Assert.True(_version.IsPublished);
        Assert.DoesNotContain(diff.Warnings ?? [], w => w.StartsWith("Rule coverage", StringComparison.Ordinal));
        await _reader.DidNotReceiveWithAnyArgs().ReadAsync(default!, default!, default!, default, default, default, default);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private Task<MethodologyPublicationDiff> Publish()
    {
        var coverage = new RuleCoverageHandler(_drafts, _store, _bindings, _reader, _clock, _access, _user);
        return new PublishMethodologyHandler(
                _module, _store, _formulas, _bindings, Units(), _uow, _audit, _access, _user, _clock,
                ruleCoverage: coverage)
            .HandleAsync(VersionId, "ФВ-13.9", From, CancellationToken.None);
    }

    private void Combinations(params (string? Value, long Rows)[] combinations)
        => _reader.ReadAsync(
                Arg.Any<IReadOnlyList<int>>(), Arg.Any<IReadOnlyList<int>>(), Arg.Any<IReadOnlyCollection<int>>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RuleCoverageCombination>)
            [
                .. combinations.Select(c => new RuleCoverageCombination(
                    [c.Value is null ? new CellValueData() : new CellValueData { ValueString = c.Value }], c.Rows, 1)),
            ]);

    private void Profile(bool withView)
    {
        var builder = new AccessBuilder { UserId = Reviewer }
            .Permission(PublishMethodologyHandler.Permission)
            .Grant(ResourceKind.Project, 1, GrantLevel.Read);
        if (withView)
        {
            builder = builder.Permission(RuleCoverageHandler.Permission);
        }

        _access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(builder.Build());
    }

    private static IUnitCatalog Units()
    {
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);
        return units;
    }

    private static void SetId(Entity<int> entity, int id)
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);

    private static List<MethodologyTestCase> TestCases() =>
    [
        new("golden-cov",
            new CalculationInput(
                new MethodologyDescriptor(
                    OwnerId, VersionId, "ECW_C05_02", "1.0.0", CalculationLevel.Configuration,
                    NumericMode.Strict, CalendarMode.Actual, TraceLevel.Off),
                DocumentId: 700,
                TableInstanceId: 500,
                PeriodKey: new PeriodKey(202601),
                SourceRowKey: "E-1",
                Arguments: []),
            new Dictionary<string, decimal> { ["tons"] = 1m },
            Tolerance: 0.000001m),
    ];

    private static CalculationOutput Output(CalculationInput input, decimal value) =>
        new(input.DocumentId, input.SourceRowKey,
            [new CalculationOutputValue(input.Methodology.MethodologyVersionId, null, "tons", value, TonneUnit)],
            []);

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
