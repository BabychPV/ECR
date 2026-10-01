using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Errors;
using Ecr.Application.Common;
using Ecr.Application.Security;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>D9: перелік сутностей збору відкритий і для Integration.View (лише читання), закритий без обох прав.</summary>
public sealed class ListSourceEntitiesPermissionTests
{
    private static ListSourceEntitiesHandler Handler(string? permission, ICollectionStore store)
    {
        var access = Substitute.For<IAccessDecisionService>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(7);
        var builder = new AccessBuilder { UserId = 7 };
        if (permission is not null)
        {
            builder.Permission(permission);
        }

        access.BuildProfileAsync(7, Arg.Any<CancellationToken>()).Returns(builder.Build());

        return new ListSourceEntitiesHandler(store, access, user);
    }

    [Theory]
    [InlineData("Integration.View")]
    [InlineData("Integration.Manage")]
    public async Task View_і_Manage_бачать_перелік(string permission)
    {
        var store = Substitute.For<ICollectionStore>();
        store.ListSourceEntitiesAsync(Arg.Any<CancellationToken>()).Returns([]);

        var rows = await Handler(permission, store).HandleAsync(CancellationToken.None);

        Assert.Empty(rows);
        await store.Received(1).ListSourceEntitiesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Стороннє_право_дає_відмову_і_до_сховища_не_доходить()
    {
        var store = Substitute.For<ICollectionStore>();

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler("Registry.EditData", store).HandleAsync(CancellationToken.None));

        await store.DidNotReceive().ListSourceEntitiesAsync(Arg.Any<CancellationToken>());
    }
}
