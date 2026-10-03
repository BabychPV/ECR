// tests/Ecr.Application.Tests/Security/CreateUserNameValidationTests.cs
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
/// T1-03: ім'я входу не валідувалося — створювалися облікові записи з
/// «невидимим» логіном (пробіл, табуляція, 200 символів, <c>&lt;b&gt;</c>).
/// </summary>
public sealed class CreateUserNameValidationTests
{
    private readonly FakeUserStore _users = new();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _current = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly User _actor;

    public CreateUserNameValidationTests()
    {
        _clock.UtcNow.Returns(new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc));
        _hasher.Hash(Arg.Any<string>()).Returns("new-hash");
        _actor = _users.Seed(new User("admin", "Admin", AuthProvider.Local));
        _users.Roles.Add(new RoleView(1, "Admins", IsBuiltIn: false, IsActive: true, ["Security.ManageUsers"], []));
        _current.CorrelationId.Returns("test");
        _current.UserId.Returns(_actor.Id);
        _access.BuildProfileAsync(_actor.Id, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = _actor.Id }.Permission("Security.ManageUsers").Build());
    }

    private Task<int> CreateAsync(string userName)
        => new CreateUserHandler(
                _users, _hasher, _access,
                new DisableBootstrapAdminHandler(_users, _uow, _audit, _current, _clock),
                _uow, _audit, _current, _clock)
            .HandleAsync(userName, "Новий", AuthProvider.Local, null, "Long-Enough-Pass-2026", [], null, CancellationToken.None);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("a b")]
    [InlineData("<b>x</b>")]
    public async Task Некоректне_ім_я_входу_відхиляється_422_і_запис_не_створюється(string userName)
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => CreateAsync(userName));

        Assert.Equal("ECR-USR-0422", error.ErrorCode);
        Assert.Equal("err.ECR-USR-0422.userNameInvalid", error.Details!["messageKey"]);
        Assert.Single(_users.Users);
    }

    [Fact]
    public async Task Задовге_ім_я_входу_відхиляється()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => CreateAsync(new string('x', 200)));

        Assert.Equal("err.ECR-USR-0422.userNameInvalid", error.Details!["messageKey"]);
    }

    [Theory]
    [InlineData("kovalenko.o")]
    [InlineData("a.b-c_d")]
    [InlineData("DOMAIN\\user")]
    [InlineData("user@domain.tld")]
    [InlineData("Коваленко_О")]
    public async Task Реальні_імена_входу_приймаються(string userName)
    {
        await CreateAsync(userName);

        Assert.Contains(_users.Users, u => u.UserName == userName);
    }

    [Theory]
    [InlineData("a​b")]
    [InlineData("a\u0007b")]
    [InlineData("a b")]
    public async Task Невидимі_та_керуючі_символи_в_імені_входу_відхиляються(string userName)
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => CreateAsync(userName));

        Assert.Equal("err.ECR-USR-0422.userNameInvalid", error.Details!["messageKey"]);
    }

    [Fact]
    public async Task Межа_довжини_імені_входу_100_приймається_101_відхиляється()
    {
        await CreateAsync(new string('x', 100));
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => CreateAsync(new string('y', 101)));

        Assert.Equal("err.ECR-USR-0422.userNameInvalid", error.Details!["messageKey"]);
    }

    [Fact]
    public async Task Дублікат_з_крайнім_пробілом_не_створює_другого_запису()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => CreateAsync("admin "));

        Assert.NotEqual("err.ECR-USR-0422.userNameInvalid", error.Details?["messageKey"]);
        Assert.Single(_users.Users);
    }

    [Fact]
    public async Task Ім_я_входу_обрізається_від_крайніх_пробілів()
    {
        await CreateAsync("  kovalenko.o  ");

        Assert.Contains(_users.Users, u => u.UserName == "kovalenko.o");
    }
}
