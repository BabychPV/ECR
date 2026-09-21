// tests/Ecr.Application.Tests/Security/UserAdministrationTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>Скидання пароля, блокування й розблокування облікового запису (директива №15, BE-12).</summary>
public sealed class UserAdministrationTests
{
    private static readonly DateTime Now = new(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc);

    private readonly FakeUserStore _users = new();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _current = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly User _actor;
    private readonly User _local;
    private readonly User _domain;

    public UserAdministrationTests()
    {
        _clock.UtcNow.Returns(Now);
        _hasher.Hash(Arg.Any<string>()).Returns("new-hash");

        // Право виконавця приходить профілем (як через групу), а не особистим призначенням.
        _actor = _users.Seed(new User("admin", "Admin", AuthProvider.Local));
        _local = _users.Seed(new User("petrenko", "Петренко", AuthProvider.Local));
        _local.SetPassword("old-hash");
        _domain = _users.Seed(FakeUserStore.DomainUser("ivanov", "S-1-5-21-77"));
        _users.Roles.Add(new RoleView(1, "Admins", IsBuiltIn: false, IsActive: true, ["Security.ManageUsers"], []));

        _current.UserId.Returns(_actor.Id);
        _current.CorrelationId.Returns("test");
        Allow("Security.ManageUsers");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "BE-12")]
    public async Task Скидання_ставить_разовий_пароль_обриває_сесії_і_не_пише_пароля_в_журнал()
    {
        var stamp = _local.SecurityStamp;

        await Reset().HandleAsync(_local.Id, "Temporary-Pass-2026", CancellationToken.None);

        Assert.Equal("new-hash", _local.PasswordHash);
        Assert.True(_local.MustChangePassword);
        Assert.NotEqual(stamp, _local.SecurityStamp);
        await _audit.Received(1).WriteSecurityEventAsync(
            Arg.Is<SecurityEventRecord>(e =>
                e.EventType == "PasswordReset" && e.TargetUserId == _local.Id
                && e.ChangedByUserId == _actor.Id && e.DetailsJson == null),
            Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "BE-12")]
    public async Task Скидання_доменному_власному_і_короткому_відхиляється_без_збереження()
    {
        var domain = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Reset().HandleAsync(_domain.Id, "Temporary-Pass-2026", CancellationToken.None));
        var self = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Reset().HandleAsync(_actor.Id, "Temporary-Pass-2026", CancellationToken.None));
        var tooShort = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Reset().HandleAsync(_local.Id, "short", CancellationToken.None));

        Assert.Equal("ECR-USR-0422", domain.ErrorCode);
        Assert.Equal("err.ECR-USR-0422.domainPasswordReset", domain.Details!["messageKey"]);
        Assert.Equal("ECR-SEC-0409", self.ErrorCode);
        Assert.Equal("err.ECR-SEC-0409.cannotTargetSelf", self.Details!["messageKey"]);
        Assert.Equal("ECR-PWD-0422", tooShort.ErrorCode);
        Assert.Equal("12", tooShort.Details!["minLength"]);
        Assert.Equal("old-hash", _local.PasswordHash);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "BE-12")]
    public async Task Блокування_безстрокове_обриває_сесії_а_розблокування_знімає_і_лічильник()
    {
        var stamp = _local.SecurityStamp;

        await Lock().HandleAsync(_local.Id, locked: true, "  звільнений  ", CancellationToken.None);

        Assert.True(_local.IsLockedOut(Now.AddYears(50)));
        Assert.NotEqual(stamp, _local.SecurityStamp);
        await _audit.Received(1).WriteSecurityEventAsync(
            Arg.Is<SecurityEventRecord>(e =>
                e.EventType == "UserLocked" && e.TargetUserId == _local.Id
                && e.DetailsJson == System.Text.Json.JsonSerializer.Serialize(new { reason = "звільнений" }, (System.Text.Json.JsonSerializerOptions?)null)),
            Arg.Any<CancellationToken>());

        _local.RegisterFailedAttempt(1, 15, Now);
        await Lock().HandleAsync(_local.Id, locked: false, "повернувся", CancellationToken.None);

        Assert.False(_local.IsLockedOut(Now));
        Assert.Equal(0, _local.FailedAttempts);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "BE-12")]
    public async Task Адміністратор_не_може_заблокувати_себе()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Lock().HandleAsync(_actor.Id, locked: true, "тест", CancellationToken.None));

        Assert.Equal("ECR-SEC-0409", error.ErrorCode);
        Assert.Equal("err.ECR-SEC-0409.cannotTargetSelf", error.Details!["messageKey"]);
        Assert.False(_actor.IsLockedOut(Now));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "BE-12")]
    public async Task Останнього_активного_адміністратора_не_блокують_і_не_скидають()
    {
        await _users.GrantRoleAsync(_local, "Admins", CancellationToken.None);

        var onLock = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Lock().HandleAsync(_local.Id, locked: true, "тест", CancellationToken.None));
        var onReset = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Reset().HandleAsync(_local.Id, "Temporary-Pass-2026", CancellationToken.None));

        Assert.Equal("err.ECR-SEC-0409.lastAdministrator", onLock.Details!["messageKey"]);
        Assert.Equal("err.ECR-SEC-0409.lastAdministrator", onReset.Details!["messageKey"]);
        Assert.False(_local.IsLockedOut(Now));

        // Заблокований другий адміністратор не рахується; активний — рахується.
        var second = _users.Seed(new User("sidorenko", "Сидоренко", AuthProvider.Local));
        await _users.GrantRoleAsync(second, "Admins", CancellationToken.None);
        second.LockByAdministrator();
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Lock().HandleAsync(_local.Id, locked: true, "тест", CancellationToken.None));

        second.Unlock();
        await Lock().HandleAsync(_local.Id, locked: true, "тест", CancellationToken.None);
        Assert.True(_local.IsLockedOut(Now));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "BE-12")]
    public async Task Без_причини_без_права_і_для_неіснуючого_запису_нічого_не_змінюється()
    {
        var noReason = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Lock().HandleAsync(_local.Id, locked: true, "   ", CancellationToken.None));
        Assert.Equal("err.ECR-USR-0422.lockReasonRequired", noReason.Details!["messageKey"]);
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Lock().HandleAsync(_local.Id, locked: true, new string('x', 401), CancellationToken.None));

        var missing = await Assert.ThrowsAsync<NotFoundException>(
            () => Lock().HandleAsync(999, locked: true, "тест", CancellationToken.None));
        Assert.Equal("err.ECR-SEC-0404.userNotFound", missing.Details!["messageKey"]);

        Allow("Document.View");
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Lock().HandleAsync(_local.Id, locked: true, "тест", CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Lock().HandleAsync(_local.Id, locked: false, "тест", CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Reset().HandleAsync(_local.Id, "Temporary-Pass-2026", CancellationToken.None));

        Assert.False(_local.IsLockedOut(Now));
        Assert.Equal("old-hash", _local.PasswordHash);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "BE-12")]
    public async Task Заблокований_доменний_запис_не_входить_і_блокування_не_знімається_входом()
    {
        _domain.LockByAdministrator();
        var login = new LoginHandler(_users, _hasher, _uow, _clock, NullLogger<LoginHandler>.Instance);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => login.HandleWindowsAsync(
            "S-1-5-21-77", "ivanov", "Іванов", [], "10.0.0.1", CancellationToken.None));

        Assert.Equal("ECR-AUTH-0423", error.ErrorCode);
        Assert.Equal("err.ECR-AUTH-0423.lockedByAdministrator", error.Details!["messageKey"]);
        Assert.True(_domain.IsLockedOut(Now));
        Assert.Null(_domain.LastSignInAt);
    }

    private void Allow(string permission)
        => _access.BuildProfileAsync(_actor.Id, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = _actor.Id }.Permission(permission).Build());

    private ResetUserPasswordHandler Reset() => new(_users, _hasher, _access, _uow, _audit, _current, _clock);

    private SetUserLockHandler Lock() => new(_users, _access, _uow, _audit, _current, _clock);
}
