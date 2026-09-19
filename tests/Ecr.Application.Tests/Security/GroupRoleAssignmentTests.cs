// tests/Ecr.Application.Tests/Security/GroupRoleAssignmentTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>Призначення й відкликання ролі ГРУПІ каталогу (ФВ-6.15).</summary>
public sealed class GroupRoleAssignmentTests
{
    private const int Actor = 7;
    private const string Sid = "S-1-5-21-100-200-300-1105";
    private const string Name = @"CORP\EcrOperators";

    private readonly FakeUserStore _users = new();
    private readonly FakePrincipalNameResolver _resolver = new();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public GroupRoleAssignmentTests()
    {
        _clock.UtcNow.Returns(new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc));
        _user.UserId.Returns(Actor);
        Allow("Security.ManageUsers");

        _resolver.Known[Name] = Sid;
        _users.Dangerous.Add("Calculation.Publish");
        _users.Roles.Add(new RoleView(1, "Operators", IsBuiltIn: false, IsActive: true, ["Document.View"], []));
        _users.Roles.Add(new RoleView(2, "Publishers", IsBuiltIn: false, IsActive: true, ["Document.View", "Calculation.Publish"], []));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Імя_групи_зберігається_як_SID_з_подією_в_журналі_і_перше_призначення_діє_з_наступного_входу()
    {
        var first = await Assign().HandleAsync(1, Name, null, new DateOnly(2026, 12, 31), false, CancellationToken.None);
        var second = await Assign().HandleAsync(2, Name, null, null, confirmDangerous: true, CancellationToken.None);

        Assert.Equal(Sid, first.PrincipalSid);
        Assert.Equal(Name, first.PrincipalName);
        Assert.True(first.EffectiveAfterNextSignIn);
        Assert.False(second.EffectiveAfterNextSignIn);

        var stored = _users.GroupAssignments.Single(a => a.Id == first.Id);
        Assert.Equal((Sid, "Operators", new DateOnly(2026, 12, 31)), (stored.Sid, stored.RoleCode, stored.ValidTo!.Value));

        await _audit.Received(1).WriteSecurityEventAsync(
            Arg.Is<SecurityEventRecord>(e =>
                e.EventType == "GroupRoleAssigned" && e.TargetRoleId == 1 && e.ChangedByUserId == Actor
                && e.DetailsJson!.Contains(Sid, StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());

        var listed = await List().HandleAsync(CancellationToken.None);
        Assert.All(listed, row => Assert.Equal(Name, row.PrincipalName));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task SID_приймається_без_резолву_імені_а_імя_що_не_резолвиться_дає_422()
    {
        var bySid = await Assign().HandleAsync(1, "s-1-5-21-9-9-9-500", null, null, false, CancellationToken.None);

        Assert.Equal("S-1-5-21-9-9-9-500", bySid.PrincipalSid);
        Assert.Null(bySid.PrincipalName);

        var unknown = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Assign().HandleAsync(1, @"CORP\NoSuchGroup", null, null, false, CancellationToken.None));
        var malformed = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Assign().HandleAsync(1, "S-1-abc", null, null, false, CancellationToken.None));

        Assert.Equal("ECR-REQ-0422", unknown.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.principalNotResolved", unknown.Details!["messageKey"]);
        Assert.Equal("err.ECR-REQ-0422.principalSidMalformed", malformed.Details!["messageKey"]);
        Assert.Single(_users.GroupAssignments);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.12")]
    public async Task Роль_із_небезпечними_правами_групі_лише_з_підтвердженням()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Assign().HandleAsync(2, Sid, null, null, confirmDangerous: false, CancellationToken.None));

        Assert.Equal("ECR-SEC-0409", error.ErrorCode);
        Assert.Equal("err.ECR-SEC-0409.dangerousRoleNeedsConfirmation", error.Details!["messageKey"]);
        Assert.Equal(["Calculation.Publish"], Assert.IsAssignableFrom<IReadOnlyList<string>>(error.Details["dangerousPermissions"]));
        Assert.Empty(_users.GroupAssignments);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());

        await Assign().HandleAsync(2, Sid, null, null, confirmDangerous: true, CancellationToken.None);
        Assert.Single(_users.GroupAssignments);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Дублікат_і_переплутані_межі_відхиляються()
    {
        await Assign().HandleAsync(1, Sid, null, null, false, CancellationToken.None);

        var duplicate = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Assign().HandleAsync(1, Name, null, null, false, CancellationToken.None));
        var dates = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Assign().HandleAsync(1, "S-1-5-32-544", new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1), false, CancellationToken.None));

        Assert.Equal("err.ECR-SEC-0409.groupAssignmentExists", duplicate.Details!["messageKey"]);
        Assert.Equal("err.ECR-REQ-0422.validityOrder", dates.Details!["messageKey"]);
        Assert.Single(_users.GroupAssignments);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Відкликання_прибирає_призначення_з_подією_а_невідомий_Id_дає_404()
    {
        var assigned = await Assign().HandleAsync(1, Sid, null, null, false, CancellationToken.None);

        await Revoke().HandleAsync(assigned.Id, CancellationToken.None);

        Assert.Empty(_users.GroupAssignments);
        await _audit.Received(1).WriteSecurityEventAsync(
            Arg.Is<SecurityEventRecord>(e => e.EventType == "GroupRoleRevoked" && e.TargetRoleId == 1),
            Arg.Any<CancellationToken>());

        var missing = await Assert.ThrowsAsync<NotFoundException>(
            () => Revoke().HandleAsync(assigned.Id, CancellationToken.None));
        Assert.Equal("err.ECR-SEC-0404.groupAssignmentNotFound", missing.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Без_Security_ManageUsers_жодна_з_трьох_дій_не_виконується()
    {
        _users.GroupAssignments.Add(new GroupRoleAssignment(Sid, "Operators", Id: 5));
        Allow("Security.ManageRoles");

        await Assert.ThrowsAsync<AccessDeniedException>(() => List().HandleAsync(CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Assign().HandleAsync(1, "S-1-5-32-544", null, null, false, CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Revoke().HandleAsync(5, CancellationToken.None));

        Assert.Single(_users.GroupAssignments);
    }

    private void Allow(string permission)
        => _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }.Permission(permission).Build());

    private ListGroupRoleAssignmentsHandler List() => new(_users, _resolver, _access, _user);

    private AssignGroupRoleHandler Assign() => new(_users, _resolver, _access, _uow, _audit, _user, _clock);

    private RevokeGroupRoleHandler Revoke() => new(_users, _access, _uow, _audit, _user, _clock);
}
