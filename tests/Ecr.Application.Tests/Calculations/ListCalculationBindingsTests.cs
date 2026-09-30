// tests/Ecr.Application.Tests/Calculations/ListCalculationBindingsTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Перелік прив'язок несе код і назву таблиці (<see cref="ListCalculationBindingsHandler"/>):
/// без них панель покриття правил показувала сирий <c>TableDefId</c>.
/// </summary>
public sealed class ListCalculationBindingsTests
{
    private const int MethodologyId = 7;

    private readonly ICalculationBindingStore _bindings = Substitute.For<ICalculationBindingStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public ListCalculationBindingsTests()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission(ListCalculationBindingsHandler.Permission)
                .Build());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Кожна_прив_язка_несе_код_і_назву_своєї_таблиці_одним_запитом()
    {
        _bindings.ListAsync(MethodologyId, Arg.Any<CancellationToken>())
            .Returns(new List<CalculationBinding>
            {
                new(3, 42, MethodologyId, "OUT1", "{}"),
                new(3, 43, MethodologyId, "OUT2", "{}"),
                new(5, 50, MethodologyId, "OUT1", "{}"),
            });

        var t3 = new LocalizedText(new Dictionary<string, string> { ["en"] = "Fuel", ["uk"] = "Паливо" });
        var t5 = new LocalizedText(new Dictionary<string, string> { ["en"] = "Waste" });
        _bindings.ListTableNamesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, BoundTableName>
            {
                [3] = new("T_FUEL", t3),
                [5] = new("T_WASTE", t5),
            });

        var result = await new ListCalculationBindingsHandler(_bindings, Substitute.For<IMethodologyDraftStore>(), _access, _user)
            .HandleAsync(MethodologyId, CancellationToken.None);

        Assert.Equal(["T_FUEL", "T_FUEL", "T_WASTE"], result.Select(b => b.TableCode));
        Assert.Equal("Паливо", result[0].TableNameL10n!.Values["uk"]);
        Assert.Equal("Waste", result[2].TableNameL10n!.Values["en"]);

        // Без N+1: один запит назв на весь перелік, по одному ідентифікатору на таблицю.
        await _bindings.Received(1).ListTableNamesAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.Count == 2 && ids.Contains(3) && ids.Contains(5)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Невідома_таблиця_дає_порожні_поля_а_не_помилку()
    {
        _bindings.ListAsync(MethodologyId, Arg.Any<CancellationToken>())
            .Returns(new List<CalculationBinding> { new(9, 90, MethodologyId, "OUT1", "{}") });
        _bindings.ListTableNamesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, BoundTableName>());

        var result = await new ListCalculationBindingsHandler(_bindings, Substitute.For<IMethodologyDraftStore>(), _access, _user)
            .HandleAsync(MethodologyId, CancellationToken.None);

        Assert.Null(Assert.Single(result).TableCode);
        Assert.Null(result[0].TableNameL10n);
    }
}
