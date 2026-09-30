// tests/Ecr.Application.Tests/Security/PasswordPolicyCheckTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// S15: політика пароля — не лише довжина, і однакова на всіх трьох шляхах
/// (зміна власного, скидання адміністратором, разовий пароль при створенні).
/// </summary>
/// <remarks>
/// ⛔ До S15 кожен шлях перевіряв тільки <c>MinLength</c>. Тести відмов нижче
/// червоні на коді до фіксу: пароль приймався і зберігався. Цифра не
/// вимагається (V-16/P-1).
/// </remarks>
public sealed class PasswordPolicyCheckTests
{
    private const string OwnerName = "petrenko";
    private const string NewUserName = "kovalenko";
    private const string CurrentPassword = "Old-Password-2026";

    private static readonly DateTime Now = new(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);

    private readonly FakeUserStore _users = new();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _current = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly User _actor;
    private readonly User _owner;

    public PasswordPolicyCheckTests()
    {
        _clock.UtcNow.Returns(Now);
        _hasher.Hash(Arg.Any<string>()).Returns("new-hash");
        _hasher.Verify(CurrentPassword, "old-hash").Returns(true);

        _actor = _users.Seed(new User("admin", "Admin", AuthProvider.Local));
        _owner = _users.Seed(new User(OwnerName, "Петренко", AuthProvider.Local));
        _owner.SetPassword("old-hash");
        _users.Roles.Add(new RoleView(1, "Admins", IsBuiltIn: false, IsActive: true, ["Security.ManageUsers"], []));

        _current.CorrelationId.Returns("test");
        _access.BuildProfileAsync(_actor.Id, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = _actor.Id }.Permission("Security.ManageUsers").Build());
    }

    /// <summary>Три шляхи, де задається пароль.</summary>
    public enum PasswordPath
    {
        /// <summary>Зміна власного пароля.</summary>
        Change,

        /// <summary>Скидання адміністратором.</summary>
        Reset,

        /// <summary>Разовий пароль при створенні.</summary>
        Create,
    }

    [Theory]
    [InlineData(PasswordPath.Change)]
    [InlineData(PasswordPath.Reset)]
    [InlineData(PasswordPath.Create)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "S15")]
    public async Task Пароль_без_цифри_приймається_навіть_із_прапорцем_RequireDigit(PasswordPath path)
    {
        // ⛔ V-16/P-1: сервер цифру не вимагає. Прапорець ставиться саме в `1`,
        // бо таке умовчання стовпця в розгорнутій базі (`DF_PwdP_Dig`): читання
        // прапорця зробило б цифру обов'язковою скрізь без рішення людини.
        typeof(PasswordPolicy).GetProperty(nameof(PasswordPolicy.RequireDigit))!.SetValue(_users.Policy, true);

        await SetAsync(path, "No-Digits-Here-At-All");

        var stored = path == PasswordPath.Create
            ? _users.Users.Single(u => u.UserName == NewUserName)
            : _owner;
        Assert.Equal("new-hash", stored.PasswordHash);
    }

    [Theory]
    [InlineData(PasswordPath.Change)]
    [InlineData(PasswordPath.Reset)]
    [InlineData(PasswordPath.Create)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "S15")]
    public async Task Пароль_з_іменем_користувача_без_урахування_регістру_відхиляється(PasswordPath path)
    {
        // Ім'я — того, ЧИЙ це пароль: власника, а при створенні — нового запису.
        var password = path == PasswordPath.Create ? "My-KOVALENKO-2026-pass" : "My-PETRENKO-2026-pass";

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => SetAsync(path, password));

        await AssertRefusedAsync(error, "err.ECR-PWD-0422.containsUserName");
    }

    [Theory]
    [InlineData(PasswordPath.Change)]
    [InlineData(PasswordPath.Reset)]
    [InlineData(PasswordPath.Create)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "S15")]
    public async Task Найпоширеніший_пароль_відхиляється(PasswordPath path)
    {
        // Довжина 12 — `MinLength` пропускає; зупиняє лише блок-лист.
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => SetAsync(path, "Password1234"));

        await AssertRefusedAsync(error, "err.ECR-PWD-0422.tooCommon");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "S15")]
    public async Task Зміна_на_чинний_пароль_відхиляється()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => SetAsync(PasswordPath.Change, CurrentPassword));

        await AssertRefusedAsync(error, "err.ECR-PWD-0422.sameAsCurrent");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "S15")]
    public async Task Закороткий_пароль_як_і_раніше_відмовляє_ключем_tooShort()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => SetAsync(PasswordPath.Change, "short1"));

        await AssertRefusedAsync(error, "err.ECR-PWD-0422.tooShort");
        Assert.Equal("12", error.Details!["minLength"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "S15")]
    public void Коротке_ім_я_не_шукається_в_паролі()
    {
        // Дволітерне ім'я заборонило б половину паролів без виграшу в стійкості.
        PasswordPolicyCheck.Ensure(_users.Policy, "Kashagan-Ab-2026", "ab", "Новий пароль");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "S15")]
    public void Блок_лист_невеликий_і_без_урахування_регістру()
    {
        Assert.True(PasswordPolicyCheck.IsCommon("QWERTYUIOP123"));
        Assert.False(PasswordPolicyCheck.IsCommon("Kashagan-Winter-2026!"));
    }

    private async Task SetAsync(PasswordPath path, string password)
    {
        switch (path)
        {
            case PasswordPath.Change:
                _current.UserId.Returns(_owner.Id);
                await new ChangePasswordHandler(_users, _hasher, _uow, _audit, _current, _clock)
                    .HandleAsync(CurrentPassword, password, CancellationToken.None);
                break;

            case PasswordPath.Reset:
                _current.UserId.Returns(_actor.Id);
                await new ResetUserPasswordHandler(_users, _hasher, _access, _uow, _audit, _current, _clock)
                    .HandleAsync(_owner.Id, password, CancellationToken.None);
                break;

            default:
                _current.UserId.Returns(_actor.Id);
                await new CreateUserHandler(
                        _users,
                        _hasher,
                        _access,
                        new DisableBootstrapAdminHandler(_users, _uow, _audit, _current, _clock),
                        _uow,
                        _audit,
                        _current,
                        _clock)
                    .HandleAsync(
                        userName: NewUserName,
                        displayName: "Новий",
                        provider: AuthProvider.Local,
                        windowsSid: null,
                        initialPassword: password,
                        roleCodes: [],
                        email: null,
                        CancellationToken.None);
                break;
        }
    }

    private async Task AssertRefusedAsync(BusinessRuleException error, string messageKey)
    {
        Assert.Equal("ECR-PWD-0422", error.ErrorCode);
        Assert.Equal(messageKey, error.Details!["messageKey"]);

        // ⛔ Пароль не змінено, запис не заведено, нічого не збережено.
        Assert.Equal("old-hash", _owner.PasswordHash);
        Assert.DoesNotContain(_users.Users, u => u.UserName == NewUserName);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().ExecuteInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
    }
}
