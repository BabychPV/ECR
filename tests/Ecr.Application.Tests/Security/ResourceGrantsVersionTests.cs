using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// Версія набору грантів ролі й звірка <c>If-Match</c> у заміні набору.
/// </summary>
/// <remarks>
/// Звірка під справжнім <c>UPDLOCK</c> — у <c>RoleGrantsConcurrencyApiTests</c>
/// (Api, реальна БД); тут — поведінка обробника й властивості хешу.
/// </remarks>
public sealed class ResourceGrantsVersionTests
{
    private readonly FakeUserStore _users = new();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly Ecr.Application.Common.ICurrentUser _currentUser =
        Substitute.For<Ecr.Application.Common.ICurrentUser>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    public ResourceGrantsVersionTests()
    {
        _currentUser.UserId.Returns(1);
        _access.BuildProfileAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 1 }.Permission(ListResourceGrantsHandler.Permission).Build());
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _users.Roles.Add(new RoleView(10, "Auditor", true, true, [], []));
        _users.GrantsByRole[10] = [Grant(7, GrantLevel.Read)];
    }

    [Fact]
    public void Версія_не_залежить_від_порядку_і_від_назви_ресурсу()
    {
        var one = ResourceGrantsVersion.Of([Grant(7, GrantLevel.Read), Grant(8, GrantLevel.Write)]);
        var other = ResourceGrantsVersion.Of(
            [Grant(8, GrantLevel.Write) with { ResourceName = "PRJ-8" }, Grant(7, GrantLevel.Read)]);

        Assert.Equal(one, other);
    }

    [Fact]
    public void Версія_змінюється_від_рівня_і_від_заборони()
    {
        var read = ResourceGrantsVersion.Of([Grant(7, GrantLevel.Read)]);

        Assert.NotEqual(read, ResourceGrantsVersion.Of([Grant(7, GrantLevel.Write)]));
        Assert.NotEqual(read, ResourceGrantsVersion.Of([Grant(7, GrantLevel.Read) with { IsDeny = true }]));
        Assert.NotEqual(read, ResourceGrantsVersion.Of([]));
    }

    [Fact]
    public async Task Застаріла_версія_дає_409_і_набір_не_змінюється()
    {
        var stale = ResourceGrantsVersion.Of([]);

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Handler().HandleAsync(10, [Grant(9, GrantLevel.Manage)], $"\"{stale}\"", CancellationToken.None));

        Assert.Equal("ECR-SEC-0409", error.ErrorCode);
        Assert.Equal(ResourceGrantsVersion.Of(_users.GrantsByRole[10]), error.Details!["version"]);
        Assert.Equal(7, Assert.Single(_users.GrantsByRole[10]).ResourceId);
    }

    [Fact]
    public async Task Актуальна_версія_зберігає_і_повертає_нову()
    {
        var current = ResourceGrantsVersion.Of(_users.GrantsByRole[10]);

        var next = await Handler().HandleAsync(10, [Grant(9, GrantLevel.Manage)], current, CancellationToken.None);

        Assert.Equal(9, Assert.Single(_users.GrantsByRole[10]).ResourceId);
        Assert.Equal(ResourceGrantsVersion.Of([Grant(9, GrantLevel.Manage)]), next);
    }

    private static ResourceGrantDto Grant(int projectId, GrantLevel level)
        => new(ResourceKind.Project, projectId, level, IsDeny: false);

    private ReplaceResourceGrantsHandler Handler()
        => new(_users, _access, _currentUser, Substitute.For<IAuditWriter>(), _uow, Substitute.For<IClock>());
}
