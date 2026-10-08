// tests/Ecr.Application.Tests/Calculations/PublishDiffPreviousVersionTests.cs
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
/// L2-5: публікація V2 над V1, коли V1 не обчислюється на вході золотого набору V2 (інший набір
/// констант і категорій). Diff — довідкова інформація (ФВ-9.6), тож збій прогону ПОПЕРЕДНЬОЇ версії
/// не валить публікацію нової: <c>before</c> порожній, у diff — попередження.
/// </summary>
/// <remarks>
/// ⛔ Що було. <c>BuildDiffAsync</c> ганяв вхід V2 на V1; <c>RunTestAsync</c> перехоплює лише
/// <c>ECR-PRD-0404</c>, тож <c>categoryRuleFailed</c> / <c>constantAmbiguous</c> V1 валили публікацію V2.
///
/// Мутаційний доказ: прибрати <c>catch</c> навколо прогону попередньої версії у <c>BuildDiffAsync</c> —
/// червоний <see cref="Попередня_версія_що_не_обчислюється_не_валить_публікацію_нової"/>.
/// Вердикт нової версії не слабшає: <see cref="Збій_нової_версії_все_ще_валить_публікацію"/> і
/// <see cref="Червоний_золотий_набір_нової_версії_все_ще_відмовляє"/>.
/// </remarks>
public sealed class PublishDiffPreviousVersionTests
{
    private const int Author = 7;
    private const int Reviewer = 9;
    private const int PreviousId = 80;
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

    public PublishDiffPreviousVersionTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(Reviewer);
        _access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Profile());

        var methodology = new Methodology(EcrCode.Create("ECW_C05_01"), Text("Land 5.1"));
        SetId(methodology, OwnerId);

        var previous = NewVersion("1.0.0", PreviousId);
        methodology.AddVersion(previous);
        methodology.PublishVersion(previous, Reviewer, "initial", new DateOnly(2026, 1, 1), testsPassed: true, Now);

        _version = NewVersion("2.0.0", VersionId);
        methodology.AddVersion(_version);

        _store.FindByVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(methodology);
        _store.GetTestCasesAsync(VersionId, Arg.Any<CancellationToken>()).Returns(TestCases());
        _store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>()).Returns(new List<MethodologyOutput>());
        _store.GetRulesAsync(VersionId, Arg.Any<CancellationToken>()).Returns(new List<MethodologyRule>());
        _store.GetConstantsAsync(VersionId, Arg.Any<CancellationToken>()).Returns(new List<MethodologyConstant>());
        _store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(new List<MethodologyFormula>());
        _store.ResolveImportsAsync(VersionId, From, Arg.Any<CancellationToken>())
              .Returns(new List<MethodologyLibrary>());
        _bindings.ListAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<CalculationBinding>());

        var engine = new RealFormulaEngine();
        _formulas.Parse(Arg.Any<string>(), Arg.Any<ExpressionDialect>())
                 .Returns(call => engine.Parse(call.ArgAt<string>(0), call.ArgAt<ExpressionDialect>(1)));
        _formulas.BuildEvaluationOrder(Arg.Any<IReadOnlyList<FormulaNode>>())
                 .Returns(call => new OrderingResult(
                     true, call.Arg<IReadOnlyList<FormulaNode>>().Select(n => n.FormulaDefId).ToList(), null));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Попередня_версія_що_не_обчислюється_не_валить_публікацію_нової()
    {
        // V1 на вході V2 дає categoryRuleFailed (інший набір категорій); V2 рахує.
        _module.ExecuteAsync(Arg.Any<CalculationInput>(), Arg.Any<CancellationToken>())
               .Returns(call => call.Arg<CalculationInput>().Methodology.MethodologyVersionId == PreviousId
                   ? throw CategoryRuleFailed()
                   : Task.FromResult(Output(call.Arg<CalculationInput>(), 1m)));

        var diff = await Publish();

        Assert.True(_version.IsPublished);
        Assert.Equal(PreviousId, diff.PreviousVersionId);

        // before порожній: вихід «новий» (Before = null), а не виняток.
        var change = Assert.Single(diff.Changes);
        Assert.Null(change.Before);
        Assert.Equal(1m, change.After);

        // Попередження називає випадок і код збою попередньої версії.
        var warning = Assert.Single(diff.Warnings ?? [], w => w.Contains("golden-l2", StringComparison.Ordinal));
        Assert.Contains("ECR-CALC-0422", warning, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Попередня_версія_що_обчислюється_не_дає_попередження_про_diff()
    {
        _module.ExecuteAsync(Arg.Any<CalculationInput>(), Arg.Any<CancellationToken>())
               .Returns(call => Task.FromResult(Output(call.Arg<CalculationInput>(), 1m)));

        var diff = await Publish();

        Assert.Empty(diff.Changes);
        Assert.DoesNotContain(diff.Warnings ?? [], w => w.Contains("golden-l2", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Збій_нової_версії_все_ще_валить_публікацію()
    {
        _module.ExecuteAsync(Arg.Any<CalculationInput>(), Arg.Any<CancellationToken>())
               .Returns(call => call.Arg<CalculationInput>().Methodology.MethodologyVersionId == VersionId
                   ? throw CategoryRuleFailed()
                   : Task.FromResult(Output(call.Arg<CalculationInput>(), 1m)));

        var error = await Assert.ThrowsAsync<DomainException>(Publish);

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.False(_version.IsPublished);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Червоний_золотий_набір_нової_версії_все_ще_відмовляє()
    {
        // V1 не обчислюється, V2 дає 2 замість очікуваних 1: попередження не маскує червоний тест.
        _module.ExecuteAsync(Arg.Any<CalculationInput>(), Arg.Any<CancellationToken>())
               .Returns(call => call.Arg<CalculationInput>().Methodology.MethodologyVersionId == PreviousId
                   ? throw CategoryRuleFailed()
                   : Task.FromResult(Output(call.Arg<CalculationInput>(), 2m)));

        await Assert.ThrowsAsync<BusinessRuleException>(Publish);

        Assert.False(_version.IsPublished);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private Task<MethodologyPublicationDiff> Publish()
        => new PublishMethodologyHandler(
                _module, _store, _formulas, _bindings, Units(), _uow, _audit, _access, _user, _clock)
            .HandleAsync(VersionId, "L2-5", From, CancellationToken.None);

    private static DomainException CategoryRuleFailed()
        => new(
            "ECR-CALC-0422",
            "Правило категорії не дало ключа категорії.",
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CALC-0422.categoryRuleFailed" });

    private static MethodologyVersion NewVersion(string number, int id)
    {
        var version = new MethodologyVersion(OwnerId, number, CalculationLevel.Configuration, Author, Now);
        version.SetModes(NumericMode.Strict, CalendarMode.Actual, TraceLevel.ErrorsOnly);
        SetId(version, id);
        return version;
    }

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
                    OwnerId, VersionId, "ECW_C05_01", "2.0.0", CalculationLevel.Configuration,
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
