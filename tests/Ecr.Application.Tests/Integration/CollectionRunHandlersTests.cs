// tests/Ecr.Application.Tests/Integration/CollectionRunHandlersTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>Журнал прогонів збору (ФВ-5.23): право й перевірка параметрів до сховища.</summary>
public sealed class CollectionRunHandlersTests
{
    private const int Actor = 17;

    private readonly ICollectionRunReader _runs = Substitute.For<ICollectionRunReader>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public CollectionRunHandlersTests()
    {
        _user.UserId.Returns(Actor);
        _runs.ListAsync(Arg.Any<CollectionRunFilter>(), Arg.Any<CursorRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<CollectionRunView>([], null, null));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-5.23")]
    [InlineData("Integration.View")]
    [InlineData("Integration.Manage")]
    public async Task View_або_Manage_відкривають_журнал(string permission)
    {
        Allow(permission);

        await List().HandleAsync(Filter(), new CursorRequest(), default);

        await _runs.Received(1).ListAsync(Arg.Any<CollectionRunFilter>(), Arg.Any<CursorRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-5.23")]
    public async Task Без_обох_прав_відмова_називає_Integration_View_і_сховище_не_читається()
    {
        Allow("Integration.EditSchedule");

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => List().HandleAsync(Filter(), new CursorRequest(), default));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Get().HandleAsync(1, default));

        Assert.Equal("Integration.View", denied.Details!["permission"]);
        await _runs.DidNotReceiveWithAnyArgs().ListAsync(default!, default!, default);
        await _runs.DidNotReceiveWithAnyArgs().FindAsync(default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-5.23")]
    public async Task Стан_зводиться_до_канонічного_імені_невідомий_422()
    {
        Allow("Integration.View");

        await List().HandleAsync(Filter(status: " degraded "), new CursorRequest(), default);
        await _runs.Received(1).ListAsync(
            Arg.Is<CollectionRunFilter>(f => f.Status == "Degraded"), Arg.Any<CursorRequest>(), Arg.Any<CancellationToken>());

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => List().HandleAsync(Filter(status: "Done"), new CursorRequest(), default));
        Assert.Equal("err.ECR-REQ-0422.collectionRunState", refused.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-5.23")]
    public async Task Порожній_проміжок_і_сторінка_понад_200_дають_422()
    {
        Allow("Integration.View");
        var at = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        var range = await Assert.ThrowsAsync<BusinessRuleException>(
            () => List().HandleAsync(Filter(from: at, to: at), new CursorRequest(), default));
        Assert.Equal("err.ECR-REQ-0422.collectionRunRange", range.Details!["messageKey"]);

        // Стеля — вимога, тож число літералом, а не константою обробника.
        await List().HandleAsync(Filter(), new CursorRequest(200), default);
        var page = await Assert.ThrowsAsync<BusinessRuleException>(
            () => List().HandleAsync(Filter(), new CursorRequest(201), default));
        Assert.Equal("200", page.Details!["max"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-5.23")]
    public async Task Невідомий_прогін_404_з_ключем_каталогу()
    {
        Allow("Integration.View");

        var missing = await Assert.ThrowsAsync<NotFoundException>(() => Get().HandleAsync(42, default));

        Assert.Equal("ECR-INT-0404", missing.ErrorCode);
        Assert.Equal("err.ECR-INT-0404.collectionRun", missing.Details!["messageKey"]);
    }

    private static CollectionRunFilter Filter(string? status = null, DateTime? from = null, DateTime? to = null)
        => new(null, null, status, from, to);

    private void Allow(params string[] permissions)
    {
        var builder = new AccessBuilder { UserId = Actor };

        foreach (var permission in permissions)
        {
            builder = builder.Permission(permission);
        }

        _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }

    private ListCollectionRunsHandler List() => new(_runs, _access, _user);

    private GetCollectionRunHandler Get() => new(_runs, _access, _user);
}
