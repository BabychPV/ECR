// tests/Ecr.Application.Tests/Security/WindowsSidCollisionTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// X5-01: хибний SID у <c>POST /users</c> більше не блокує Windows-вхід людини назавжди.
/// </summary>
/// <remarks>
/// ⛔ Було: адміністратор набирав SID руками з помилкою в цифрі; Windows-вхід людини шукав
/// запис за СПРАВЖНІМ SID, не знаходив, заводив новий з тим самим іменем — і падав на
/// <c>UQ_User_Name</c>: 500, спроба входу відкочувалась разом зі збоєм, а виправити запис
/// не було чим (ні зміни SID, ні видалення). Тепер: 409 з причиною і слідом у журналі
/// входів; SID перевіряється при створенні; непідтверджений SID виправляється ендпоінтом.
/// </remarks>
public sealed class WindowsSidCollisionTests
{
    private const string TypoSid = "S-1-5-21-1-2-3-9999";
    private const string RealSid = "S-1-5-21-1-2-3-1001";

    private static readonly DateTime Now = new(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);

    private readonly FakeUserStore _users = new();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _current = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly RecordingLogger<LoginHandler> _log = new();
    private readonly User _actor;

    public WindowsSidCollisionTests()
    {
        _clock.UtcNow.Returns(Now);
        _hasher.Hash(Arg.Any<string>()).Returns("new-hash");

        // Z4-01: заміна SID іде в транзакції — підставний UoW виконує її тіло, як справжній.
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
        _actor = _users.Seed(new User("admin", "Admin", AuthProvider.Local));
        _users.Roles.Add(new RoleView(1, "Admins", IsBuiltIn: false, IsActive: true, ["Security.ManageUsers"], []));
        _current.UserId.Returns(_actor.Id);
        _current.CorrelationId.Returns("test");
        _access.BuildProfileAsync(_actor.Id, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = _actor.Id }.Permission("Security.ManageUsers").Build());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "X5-01")]
    public async Task Windows_вхід_з_ім_ям_зайнятим_записом_з_іншим_SID_дає_409_і_слід_у_журналі_входів()
    {
        var typo = _users.Seed(FakeUserStore.DomainUser(@"CORP\ivan", TypoSid));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Login().HandleWindowsAsync(RealSid, @"CORP\ivan", "Іван", [], "10.0.0.1", CancellationToken.None));

        Assert.Equal("ECR-USR-0409", error.ErrorCode);
        Assert.Equal("err.ECR-USR-0409.windowsSidMismatch", error.Details!["messageKey"]);

        // ⚠ Людині є що передати адміністраторові — її власний SID.
        Assert.Equal(RealSid, error.Details["sid"]);
        Assert.Equal(@"CORP\ivan", error.Details["userName"]);

        // ⛔ Нового запису немає: раніше саме він і падав на UQ_User_Name.
        Assert.Equal(2, _users.Users.Count);
        Assert.Equal(TypoSid, typo.WindowsSid);

        var attempt = Assert.Single(_users.Attempts);
        Assert.False(attempt.IsSuccess);
        Assert.Equal("SidMismatch", attempt.FailReason);

        // Адміністратор бачить, який запис виправити і на який SID.
        var warning = Assert.Single(_log.OfLevel(LogLevel.Warning));
        Assert.Contains(RealSid, warning.Message, StringComparison.Ordinal);
        Assert.Contains($"#{typo.Id}", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "X5-01")]
    [InlineData("", "err.ECR-USR-0422.windowsSidRequired")]
    [InlineData("   ", "err.ECR-USR-0422.windowsSidRequired")]
    [InlineData(null, "err.ECR-USR-0422.windowsSidRequired")]
    [InlineData("S-1-5-21-x", "err.ECR-USR-0422.windowsSidMalformed")]
    [InlineData("CORP\\ivan", "err.ECR-USR-0422.windowsSidMalformed")]
    public async Task Створення_доменного_запису_з_порожнім_або_не_SID_дає_422_і_запису_немає(
        string? sid, string messageKey)
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => CreateDomainAsync(@"CORP\ivan", sid));

        Assert.Equal("ECR-USR-0422", error.ErrorCode);
        Assert.Equal(messageKey, error.Details!["messageKey"]);
        Assert.Null(await _users.FindByUserNameAsync(@"CORP\ivan", CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "X5-01")]
    public async Task Створення_з_SID_що_вже_має_інший_запис_дає_409_а_не_500_на_UX_User_Sid()
    {
        _users.Seed(FakeUserStore.DomainUser(@"CORP\petro", RealSid));

        // ⚠ Нижній регістр — той самий SID: канонічна форма — верхній.
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => CreateDomainAsync(@"CORP\ivan", " s-1-5-21-1-2-3-1001 "));

        Assert.Equal("ECR-USR-0409", error.ErrorCode);
        Assert.Equal("err.ECR-USR-0409.windowsSidTaken", error.Details!["messageKey"]);
        Assert.Equal(@"CORP\petro", error.Details["userName"]);
        Assert.Null(await _users.FindByUserNameAsync(@"CORP\ivan", CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "X5-01")]
    public async Task Створений_SID_зберігається_в_канонічній_формі()
    {
        var id = await CreateDomainAsync(@"CORP\ivan", " s-1-5-21-1-2-3-1001 ");

        Assert.Equal(RealSid, (await _users.FindByIdAsync(id, CancellationToken.None))!.WindowsSid);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "X5-01")]
    public async Task Виправлений_непідтверджений_SID_дає_вхід_у_той_самий_запис()
    {
        var typo = _users.Seed(FakeUserStore.DomainUser(@"CORP\ivan", TypoSid));
        var stamp = typo.SecurityStamp;

        await Correct().HandleAsync(typo.Id, "s-1-5-21-1-2-3-1001", CancellationToken.None);

        Assert.Equal(RealSid, typo.WindowsSid);
        Assert.NotEqual(stamp, typo.SecurityStamp);
        await _audit.Received(1).WriteSecurityEventAsync(
            Arg.Is<SecurityEventRecord>(e =>
                e.EventType == "WindowsSidCorrected"
                && e.TargetUserId == typo.Id
                && e.DetailsJson!.Contains(TypoSid, StringComparison.Ordinal)
                && e.DetailsJson.Contains(RealSid, StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _uow.Received().SaveChangesAsync(Arg.Any<CancellationToken>());

        var login = await Login().HandleWindowsAsync(
            RealSid, @"CORP\ivan", "Іван", [], "10.0.0.1", CancellationToken.None);

        Assert.Equal(typo.Id, login.UserId);
        Assert.Equal(2, _users.Users.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "X5-01")]
    public async Task SID_запису_що_вже_входив_не_виправляється_409()
    {
        var confirmed = _users.Seed(FakeUserStore.DomainUser(@"CORP\ivan", TypoSid));
        confirmed.RegisterSuccessfulLogin(Now.AddDays(-1));

        var error = await Assert.ThrowsAsync<DomainException>(
            () => Correct().HandleAsync(confirmed.Id, RealSid, CancellationToken.None));

        Assert.Equal("ECR-SEC-0409", error.ErrorCode);
        Assert.Equal("err.ECR-SEC-0409.windowsSidConfirmed", error.Details!["messageKey"]);
        Assert.Equal(TypoSid, confirmed.WindowsSid);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "X5-01")]
    public async Task Виправлення_SID_локального_зайнятого_чи_не_SID_відхиляється()
    {
        var local = _users.Seed(new User("petrenko", "Петренко", AuthProvider.Local));
        var typo = _users.Seed(FakeUserStore.DomainUser(@"CORP\ivan", TypoSid));
        _users.Seed(FakeUserStore.DomainUser(@"CORP\petro", RealSid));

        var notDomain = await Assert.ThrowsAsync<DomainException>(
            () => Correct().HandleAsync(local.Id, RealSid.Replace("1001", "1002", StringComparison.Ordinal), CancellationToken.None));
        var taken = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Correct().HandleAsync(typo.Id, RealSid, CancellationToken.None));
        var malformed = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Correct().HandleAsync(typo.Id, "S-1-5-21-x", CancellationToken.None));

        Assert.Equal("err.ECR-USR-0422.windowsSidNotDomain", notDomain.Details!["messageKey"]);
        Assert.Equal("err.ECR-USR-0409.windowsSidTaken", taken.Details!["messageKey"]);
        Assert.Equal("err.ECR-USR-0422.windowsSidMalformed", malformed.Details!["messageKey"]);
        Assert.Equal(TypoSid, typo.WindowsSid);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "X5-01")]
    public async Task Виправлення_SID_без_права_ManageUsers_відхиляється()
    {
        var typo = _users.Seed(FakeUserStore.DomainUser(@"CORP\ivan", TypoSid));
        _access.BuildProfileAsync(_actor.Id, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = _actor.Id }.Build());

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Correct().HandleAsync(typo.Id, RealSid, CancellationToken.None));

        Assert.Equal(TypoSid, typo.WindowsSid);
    }

    private LoginHandler Login() => new(_users, _hasher, _uow, _clock, _log);

    private CorrectWindowsSidHandler Correct() => new(_users, _access, _uow, _audit, _current, _clock);

    private Task<int> CreateDomainAsync(string userName, string? sid)
        => new CreateUserHandler(
                _users, _hasher, _access,
                new DisableBootstrapAdminHandler(_users, _uow, _audit, _current, _clock),
                _uow, _audit, _current, _clock)
            .HandleAsync(userName, "Іван", AuthProvider.Windows, sid, null, [], null, CancellationToken.None);
}
