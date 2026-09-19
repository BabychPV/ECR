// tests/Ecr.Application.Tests/Security/RoleLifecycleTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// Клонування, перейменування й видалення ролі (директива №15, BE-14).
/// </summary>
public sealed class RoleLifecycleTests
{
    private const int Actor = 7;

    private readonly FakeUserStore _users = new();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public RoleLifecycleTests()
    {
        _clock.UtcNow.Returns(new DateTime(2026, 9, 19, 8, 0, 0, DateTimeKind.Utc));
        _user.UserId.Returns(Actor);
        _user.CorrelationId.Returns("test");
        Allow("Security.ManageRoles");

        _users.Roles.Add(new RoleView(1, "SystemAdministrator", IsBuiltIn: true, IsActive: true, ["Security.ManageRoles"], []));
        _users.Roles.Add(new RoleView(2, "Reviewers", IsBuiltIn: false, IsActive: true, ["Document.View", "Template.View"], []));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Роль_із_призначенням_не_видаляється_409_з_кількістю_і_роль_лишилась()
    {
        var holder = _users.Seed(FakeUserStore.DomainUser("ivanov"));
        await _users.GrantRoleAsync(holder, "Reviewers", CancellationToken.None);
        _users.GroupAssignments.Add(new GroupRoleAssignment("S-1-5-21-9", "Reviewers"));
        _users.GrantsByRole[2] = [new ResourceGrantDto(Ecr.Domain.Enums.ResourceKind.Project, 5, Ecr.Domain.Enums.GrantLevel.Read, IsDeny: false)];

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Delete().HandleAsync(2, CancellationToken.None));

        Assert.Equal("ECR-SEC-0409", error.ErrorCode);
        Assert.Equal("err.ECR-SEC-0409.roleInUse", error.Details!["messageKey"]);
        Assert.Equal("2", error.Details["assignments"]);
        Assert.Equal("1", error.Details["grants"]);

        Assert.Contains(_users.Roles, r => r.Id == 2);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Вільна_роль_видаляється_і_лишає_слід_у_журналі_безпеки()
    {
        await Delete().HandleAsync(2, CancellationToken.None);

        Assert.DoesNotContain(_users.Roles, r => r.Id == 2);
        await _audit.Received(1).WriteSecurityEventAsync(
            Arg.Is<SecurityEventRecord>(e => e.EventType == "RoleDeleted" && e.TargetRoleId == 2 && e.ChangedByUserId == Actor),
            Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Вбудована_роль_не_видаляється_й_не_перейменовується()
    {
        var onDelete = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Delete().HandleAsync(1, CancellationToken.None));
        var onRename = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Rename().HandleAsync(1, "Admins", null, CancellationToken.None));

        Assert.Equal("ECR-SEC-0409", onDelete.ErrorCode);
        Assert.Equal("err.ECR-SEC-0409.roleBuiltIn", onDelete.Details!["messageKey"]);
        Assert.Equal("err.ECR-SEC-0409.roleBuiltIn", onRename.Details!["messageKey"]);

        Assert.Equal("SystemAdministrator", _users.Roles.Single(r => r.Id == 1).Code);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Перейменування_змінює_код_і_пише_обидва_коди_в_журнал()
    {
        await Rename().HandleAsync(2, "Auditors", null, CancellationToken.None);

        Assert.Equal("Auditors", _users.Roles.Single(r => r.Id == 2).Code);
        await _audit.Received(1).WriteSecurityEventAsync(
            Arg.Is<SecurityEventRecord>(e =>
                e.EventType == "RoleRenamed"
                && e.DetailsJson!.Contains("Reviewers", StringComparison.Ordinal)
                && e.DetailsJson.Contains("Auditors", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Перейменування_на_зайнятий_код_відхиляється_тим_самим_кодом_що_й_створення()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Rename().HandleAsync(2, "SystemAdministrator", null, CancellationToken.None));

        Assert.Equal("ECR-SEC-0409", error.ErrorCode);
        Assert.Equal("err.ECR-SEC-0409.roleCodeTaken", error.Details!["messageKey"]);
        Assert.Equal("Reviewers", _users.Roles.Single(r => r.Id == 2).Code);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Клон_отримує_новий_код_і_копію_набору_прав_без_грантів()
    {
        _users.GrantsByRole[2] = [new ResourceGrantDto(Ecr.Domain.Enums.ResourceKind.Project, 5, Ecr.Domain.Enums.GrantLevel.Read, IsDeny: false)];

        var cloneId = await Clone().HandleAsync(2, "ReviewersCopy", null, CancellationToken.None);

        var clone = _users.Roles.Single(r => r.Id == cloneId);
        Assert.Equal("ReviewersCopy", clone.Code);
        Assert.False(clone.IsBuiltIn);
        Assert.Equal(["Document.View", "Template.View"], clone.Permissions);
        Assert.False(_users.GrantsByRole.ContainsKey(cloneId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Неіснуюча_роль_дає_404_а_без_права_жодна_дія_не_виконується()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => Delete().HandleAsync(99, CancellationToken.None));

        Allow("Document.View");

        await Assert.ThrowsAsync<AccessDeniedException>(() => Delete().HandleAsync(2, CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Rename().HandleAsync(2, "X1", null, CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Clone().HandleAsync(2, "X2", null, CancellationToken.None));

        Assert.Equal(2, _users.Roles.Count);
        Assert.Equal("Reviewers", _users.Roles.Single(r => r.Id == 2).Code);
    }

    private void Allow(string permission)
        => _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }.Permission(permission).Build());

    private DeleteRoleHandler Delete() => new(_users, _access, _uow, _audit, _user, _clock);

    private RenameRoleHandler Rename() => new(_users, _access, _uow, _audit, _user, _clock);

    private CloneRoleHandler Clone()
        => new(_users, _access, new CreateRoleHandler(_users, _access, _uow, _audit, _user, _clock), _user);
}
