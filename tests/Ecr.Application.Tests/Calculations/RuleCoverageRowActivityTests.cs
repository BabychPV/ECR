// tests/Ecr.Application.Tests/Calculations/RuleCoverageRowActivityTests.cs
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
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// D-425: матриця покриття не бачить рядків, яких не рахує прогін (фільтр активності рядків методології).
/// </summary>
/// <remarks>
/// Мутація: прибрати <c>ReadFilteredAsync</c> з <c>HandleAsync</c> (читати без фільтра) — перший тест
/// червоніє: рядки Removed/порожні лишаються в матриці, <c>ExcludedRows = 0</c>.
/// </remarks>
public sealed class RuleCoverageRowActivityTests
{
    private const int MethodologyId = 7;
    private const int VersionId = 70;
    private const int TableDefId = 3;
    private const int RuleColumn = 42;
    private const int StatusColumn = 12;

    private readonly IMethodologyDraftStore _drafts = Substitute.For<IMethodologyDraftStore>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly ICalculationBindingStore _bindings = Substitute.For<ICalculationBindingStore>();
    private readonly IRuleCoverageReader _reader = Substitute.For<IRuleCoverageReader>();
    private readonly IRowActivityFilterSource _activity = Substitute.For<IRowActivityFilterSource>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public RuleCoverageRowActivityTests()
    {
        _user.UserId.Returns(9);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTime(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc));

        var version = new MethodologyVersion(
            MethodologyId, "1.0", CalculationLevel.Configuration, 9,
            new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc));
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(version, VersionId);
        _drafts.GetAllVersionsAsync(MethodologyId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MethodologyVersion>)[version]);

        _methodologies.GetRulesAsync(VersionId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MethodologyRule>)
                [new MethodologyRule(VersionId, EcrCode.Create("CO2"), $$"""{"{{RuleColumn}}":"CO2"}""", 10)]);
        _bindings.ListAsync(MethodologyId, Arg.Any<CancellationToken>())
            .Returns(new List<CalculationBinding> { new(TableDefId, 50, MethodologyId, "OUT1", "{}") });

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission(RuleCoverageHandler.Permission)
                .Grant(ResourceKind.Project, 1, GrantLevel.Read)
                .Build());

        _clock = clock;
    }

    private readonly IClock _clock;

    private RuleCoverageHandler Handler(bool withActivity)
        => new(_drafts, _methodologies, _bindings, _reader, _clock, _access, _user, rowActivity: withActivity ? _activity : null);

    private static CellValueData Text(string value) => new() { ValueString = value };

    private static CellValueData Entry(long id) => new() { ValueRegistryEntryId = id };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-425")]
    public async Task Рядки_не_Running_і_без_статусу_виключені_з_матриці_й_пораховані()
    {
        _activity.ResolveForVersionAsync(VersionId, Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new RowActivityFilterResolution(
                "77", new Dictionary<int, IReadOnlyList<int>> { [TableDefId] = [StatusColumn] }));
        _reader.ReadAsync(
                Arg.Any<IReadOnlyList<int>>(),
                Arg.Is<IReadOnlyList<int>>(c => c.Count == 2 && c[0] == RuleColumn && c[1] == StatusColumn),
                Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RuleCoverageCombination>)
            [
                new RuleCoverageCombination([Text("CO2"), Entry(77)], 4, 2),
                new RuleCoverageCombination([Text("CO2"), Entry(78)], 3, 1),
                new RuleCoverageCombination([Text("NOx"), null], 2, 1),
                new RuleCoverageCombination([Text("CO2"), Entry(77)], 1, 1),
            ]);

        var result = await Handler(withActivity: true)
            .HandleAsync(MethodologyId, VersionId, null, 202601, 202612, CancellationToken.None);

        var combination = Assert.Single(result.Combinations);
        Assert.Equal(5, combination.Rows);
        Assert.Equal(3, combination.Documents);
        Assert.Equal(RuleCoverageState.Covered, combination.State);
        Assert.Equal(5, result.ExcludedRows);
        Assert.Equal([RuleColumn], result.ColumnDefIds);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-425")]
    public async Task Без_прапорця_читання_як_раніше_без_колонки_статусу_і_без_виключених()
    {
        _activity.ResolveForVersionAsync(VersionId, Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns((RowActivityFilterResolution?)null);
        _reader.ReadAsync(
                Arg.Any<IReadOnlyList<int>>(), Arg.Any<IReadOnlyList<int>>(), Arg.Any<IReadOnlyCollection<int>>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RuleCoverageCombination>)[new RuleCoverageCombination([Text("CO2")], 4, 2)]);

        var result = await Handler(withActivity: true)
            .HandleAsync(MethodologyId, VersionId, null, 202601, 202612, CancellationToken.None);

        await _reader.Received(1).ReadAsync(
            Arg.Any<IReadOnlyList<int>>(),
            Arg.Is<IReadOnlyList<int>>(c => c.Count == 1 && c[0] == RuleColumn),
            Arg.Any<IReadOnlyCollection<int>>(), 202601, 202612, RuleCoverageHandler.MaxCombinations, Arg.Any<CancellationToken>());
        Assert.Equal(4, Assert.Single(result.Combinations).Rows);
        Assert.Equal(0, result.ExcludedRows);
    }
}
