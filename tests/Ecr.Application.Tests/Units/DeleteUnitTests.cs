// tests/Ecr.Application.Tests/Units/DeleteUnitTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Units;
using Ecr.Domain.Entities.Units;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Units;

/// <summary>
/// «Де використовується» і видалення одиниці (директива №15, BE-15): одиниця,
/// на яку щось посилається, лишається на місці, а відмова несе перелік
/// залежних.
/// </summary>
public sealed class DeleteUnitTests
{
    private const int UnitId = 42;

    private readonly IUnitStore _units = Substitute.For<IUnitStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();

    private readonly Unit _unit = new(
        EcrCode.Create("lb"),
        new LocalizedText(new Dictionary<string, string> { ["en"] = "lb" }),
        new LocalizedText(new Dictionary<string, string> { ["en"] = "Pound" }),
        dimensionId: 1,
        isBase: false,
        factorToBase: 0.45359237m,
        offsetToBase: 0m);

    public DeleteUnitTests()
    {
        _user.UserId.Returns(9);
        Allow("Uom.EditCatalog");

        _units.FindUnitByIdAsync(UnitId, Arg.Any<CancellationToken>()).Returns(_unit);
        Usage();
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Одиниця_без_посилань_видаляється()
    {
        await Delete().HandleAsync(UnitId, default);

        _units.Received(1).RemoveUnit(_unit);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Одиниця_з_посиланням_дає_409_з_переліком_і_лишається()
    {
        var column = new UsageItemDto("templateColumn", "7", "Fuel.Mass", "/admin/templates/1/versions/2");
        Usage(column);

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Delete().HandleAsync(UnitId, default));

        Assert.Equal("ECR-UOM-0409", error.ErrorCode);
        Assert.Equal("err.ECR-UOM-0409.unitInUse", error.Details!["messageKey"]);
        Assert.Equal("lb", error.Details["code"]);
        Assert.Equal("1", error.Details["total"]);
        Assert.Equal(column, Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<UsageItemDto>>(error.Details["references"])));

        // ⛔ Головне: одиниця лишилась.
        _units.DidNotReceive().RemoveUnit(Arg.Any<Unit>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Неіснуюча_одиниця_дає_404_і_для_видалення_і_для_переліку()
    {
        _units.FindUnitByIdAsync(99, Arg.Any<CancellationToken>()).Returns((Unit?)null);

        var onDelete = await Assert.ThrowsAsync<NotFoundException>(() => Delete().HandleAsync(99, default));
        var onUsage = await Assert.ThrowsAsync<NotFoundException>(() => UsageOf().HandleAsync(99, default));

        Assert.Equal("ECR-UOM-0404", onDelete.ErrorCode);
        Assert.Equal("ECR-UOM-0404", onUsage.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Без_права_Uom_EditCatalog_ні_видалення_ні_переліку()
    {
        // Право задане ЗАМІНОЮ дозволеного набору (D-134).
        Allow("Calculation.View");

        await Assert.ThrowsAsync<AccessDeniedException>(() => Delete().HandleAsync(UnitId, default));
        await Assert.ThrowsAsync<AccessDeniedException>(() => UsageOf().HandleAsync(UnitId, default));

        _units.DidNotReceive().RemoveUnit(Arg.Any<Unit>());
        await _units.DidNotReceive().FindUnitUsageAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Перелік_просить_рівно_двадцять_перших()
    {
        var response = await UsageOf().HandleAsync(UnitId, default);

        Assert.Equal(0, response.Total);
        await _units.Received(1).FindUnitUsageAsync(UnitId, 20, Arg.Any<CancellationToken>());
    }

    private DeleteUnitHandler Delete() => new(_units, _uow, _access, _user);

    private UnitUsageHandler UsageOf() => new(_units, _access, _user);

    private void Usage(params UsageItemDto[] items)
        => _units.FindUnitUsageAsync(UnitId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new UsageResponse(items.Length, items));

    private void Allow(params string[] permissions)
    {
        var builder = new AccessBuilder { UserId = 9 };
        foreach (var permission in permissions)
        {
            builder = builder.Permission(permission);
        }

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }
}
