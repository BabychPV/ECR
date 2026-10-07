// tests/Ecr.Application.Tests/Calculations/RuleCoverageProjectScopeTests.cs
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
/// Матриця покриття правил бере рядки лише з проєктів, які користувач може читати
/// (<see cref="RuleCoverageHandler"/>, аудит S7).
/// </summary>
/// <remarks>
/// <c>Calculation.View</c> — право на методологію, а значення комірок і лічильники
/// рядків — дані проєктів. До виправлення обробник звертався до сховища без жодного
/// обмеження проєктів. Наскрізний доказ (справжній SQL і HTTP, два проєкти) —
/// <c>Ecr.Api.Tests.RuleCoverageTests</c>; тут — що обробник передає рівно набір
/// читабельних проєктів, заборони включно, і не ходить у сховище без жодного.
/// </remarks>
public sealed class RuleCoverageProjectScopeTests
{
    private const int MethodologyId = 7;
    private const int VersionId = 70;
    private const int TableDefId = 3;
    private const int ColumnDefId = 42;

    private readonly IMethodologyDraftStore _drafts = Substitute.For<IMethodologyDraftStore>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly ICalculationBindingStore _bindings = Substitute.For<ICalculationBindingStore>();
    private readonly IRuleCoverageReader _reader = Substitute.For<IRuleCoverageReader>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public RuleCoverageProjectScopeTests()
    {
        _user.UserId.Returns(9);
        _clock.UtcNow.Returns(new DateTime(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc));

        var version = new MethodologyVersion(
            MethodologyId, "1.0", CalculationLevel.Configuration, 9,
            new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc));
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(version, VersionId);
        _drafts.GetAllVersionsAsync(MethodologyId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MethodologyVersion>)[version]);

        _methodologies.GetRulesAsync(VersionId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MethodologyRule>)
                [new MethodologyRule(VersionId, EcrCode.Create("CO2"), $$"""{"{{ColumnDefId}}":"CO2"}""", 10)]);

        _bindings.ListAsync(MethodologyId, Arg.Any<CancellationToken>())
            .Returns(new List<CalculationBinding> { new(TableDefId, 50, MethodologyId, "OUT1", "{}") });

        _reader.ReadAsync(
                Arg.Any<IReadOnlyList<int>>(), Arg.Any<IReadOnlyList<int>>(), Arg.Any<IReadOnlyCollection<int>>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RuleCoverageCombination>)
                [new RuleCoverageCombination([new CellValueData { ValueString = "CO2" }], 4, 2)]);
    }

    private RuleCoverageHandler Handler()
        => new(_drafts, _methodologies, _bindings, _reader, _clock, _access, _user);

    private void Profile(Func<AccessBuilder, AccessBuilder> grants)
        => _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(grants(new AccessBuilder { UserId = 9 }.Permission(RuleCoverageHandler.Permission)).Build());

    /// <remarks>
    /// Проєкт 1 — Read, 2 — Write, 3 — Read під явною забороною, 4 — грант рівня
    /// None. Мутація: передати в сховище всі проєкти з <c>Grants</c> без
    /// <c>LevelFor</c> (або прибрати аргумент-фільтр) → тест червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.9")]
    public async Task Сховище_отримує_рівно_проєкти_з_правом_читання_заборона_виграє()
    {
        Profile(b => b
            .Grant(ResourceKind.Project, 1, GrantLevel.Read)
            .Grant(ResourceKind.Project, 2, GrantLevel.Write)
            .Grant(ResourceKind.Project, 3, GrantLevel.Read)
            .Deny(ResourceKind.Project, 3)
            .Grant(ResourceKind.Project, 4, GrantLevel.None)
            .Grant(ResourceKind.Table, 5, GrantLevel.Read));

        var result = await Handler().HandleAsync(MethodologyId, VersionId, null, 202601, 202612, CancellationToken.None);

        await _reader.Received(1).ReadAsync(
            Arg.Is<IReadOnlyList<int>>(t => t.Count == 1 && t[0] == TableDefId),
            Arg.Is<IReadOnlyList<int>>(c => c.Count == 1 && c[0] == ColumnDefId),
            Arg.Is<IReadOnlyCollection<int>>(p => p.Count == 2 && p.Contains(1) && p.Contains(2)),
            202601, 202612, RuleCoverageHandler.MaxCombinations, Arg.Any<CancellationToken>());

        var combination = Assert.Single(result.Combinations);
        Assert.Equal(4, combination.Rows);
        Assert.Equal(2, combination.Documents);
    }

    /// <remarks>
    /// C1: таблиця прив'язки на клон-версії шаблону — комірки читаються за колонкою, що відповідає колонці з правила
    /// ЗА ШЛЯХОМ у версії таблиці; вісь матриці лишається в Id правила, тож класифікація працює як на джерелі.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "C1")]
    public async Task Таблиця_клон_версії_читається_за_локальною_колонкою_а_вісь_лишається_в_Id_правила()
    {
        Profile(b => b.Grant(ResourceKind.Project, 1, GrantLevel.Read));
        const int CloneVersion = 9;
        const int CloneColumn = 142;
        var mapper = Substitute.For<IColumnPathMapper>();
        mapper.GetTemplateVersionsOfTablesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, int> { [TableDefId] = CloneVersion });
        mapper.MapToVersionAsync(Arg.Any<IReadOnlyCollection<int>>(), CloneVersion, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, int> { [ColumnDefId] = CloneColumn });

        var handler = new RuleCoverageHandler(_drafts, _methodologies, _bindings, _reader, _clock, _access, _user, mapper);
        var result = await handler.HandleAsync(MethodologyId, VersionId, null, 202601, 202612, CancellationToken.None);

        await _reader.Received(1).ReadAsync(
            Arg.Any<IReadOnlyList<int>>(),
            Arg.Is<IReadOnlyList<int>>(c => c.Count == 1 && c[0] == CloneColumn),
            Arg.Any<IReadOnlyCollection<int>>(), 202601, 202612, RuleCoverageHandler.MaxCombinations, Arg.Any<CancellationToken>());
        Assert.Equal([ColumnDefId], result.ColumnDefIds);
        Assert.Equal(RuleCoverageState.Covered, Assert.Single(result.Combinations).State);
    }

    /// <remarks>C1: таблиці двох версій шаблону — читання по версіях, однакові комбінації складаються.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "C1")]
    public async Task Таблиці_двох_версій_шаблону_дають_одну_суму_для_однакової_комбінації()
    {
        Profile(b => b.Grant(ResourceKind.Project, 1, GrantLevel.Read));
        _bindings.ListAsync(MethodologyId, Arg.Any<CancellationToken>())
            .Returns(new List<CalculationBinding>
            {
                new(TableDefId, 50, MethodologyId, "OUT1", "{}"),
                new(TableDefId + 1, 51, MethodologyId, "OUT1", "{}"),
            });
        var mapper = Substitute.For<IColumnPathMapper>();
        mapper.GetTemplateVersionsOfTablesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, int> { [TableDefId] = 8, [TableDefId + 1] = 9 });
        mapper.MapToVersionAsync(Arg.Any<IReadOnlyCollection<int>>(), 8, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, int> { [ColumnDefId] = ColumnDefId });
        mapper.MapToVersionAsync(Arg.Any<IReadOnlyCollection<int>>(), 9, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, int> { [ColumnDefId] = 142 });

        var handler = new RuleCoverageHandler(_drafts, _methodologies, _bindings, _reader, _clock, _access, _user, mapper);
        var result = await handler.HandleAsync(MethodologyId, VersionId, null, 202601, 202612, CancellationToken.None);

        var combination = Assert.Single(result.Combinations);
        Assert.Equal(8, combination.Rows);
        Assert.Equal(4, combination.Documents);
    }

    /// <remarks>
    /// Без жодного гранта — порожня матриця, а не «усі проєкти» і не 403 (так само,
    /// як перелік документів і пошук). Сховище не викликається зовсім: порожній
    /// набір у SQL легко перетворити на «без фільтра» однією помилкою. Мутація:
    /// прибрати коротке замикання й передавати порожній набір — лишається другий
    /// рубіж у сховищі, але цей тест червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Без_жодного_гранта_на_проєкт_порожня_матриця_і_сховище_не_викликається()
    {
        Profile(b => b.Grant(ResourceKind.Project, 3, GrantLevel.Read).Deny(ResourceKind.Project, 3));

        var result = await Handler().HandleAsync(MethodologyId, VersionId, null, 202601, 202612, CancellationToken.None);

        Assert.Empty(result.Combinations);
        Assert.False(result.Truncated);
        Assert.Equal([ColumnDefId], result.ColumnDefIds);
        Assert.Equal(["CO2"], result.Rules.Select(r => r.Code));
        await _reader.DidNotReceiveWithAnyArgs().ReadAsync(default!, default!, default!, default, default, default, default);
    }
}
