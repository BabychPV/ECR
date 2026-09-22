// tests/Ecr.Application.Tests/Security/LastSignInTests.cs
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

/// <summary>
/// <c>LastSignInAt</c> ставить лише УСПІШНИЙ вхід — обома провайдерами (BE-12).
/// </summary>
/// <remarks>
/// ⚠ Невдала спроба не має зсувати момент: адміністратор читає колонку як
/// «коли людина востаннє реально працювала», а не «коли хтось підбирав пароль».
/// </remarks>
public sealed class LastSignInTests
{
    private static readonly DateTime Now = new(2026, 9, 21, 10, 30, 0, DateTimeKind.Utc);

    private readonly FakeUserStore _users = new();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public LastSignInTests()
    {
        _clock.UtcNow.Returns(Now);
        _hasher.Verify("right", "hash").Returns(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "BE-12")]
    public async Task Успішний_локальний_вхід_ставить_момент_входу()
    {
        var user = LocalUser();

        await Handler().HandleAsync("petrenko", "right", "10.0.0.1", CancellationToken.None);

        Assert.Equal(Now, user.LastSignInAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "BE-12")]
    public async Task Невірний_пароль_момент_входу_не_змінює()
    {
        var user = LocalUser();

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync("petrenko", "wrong", "10.0.0.1", CancellationToken.None));

        Assert.Null(user.LastSignInAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "BE-12")]
    public async Task Доменний_вхід_теж_ставить_момент_входу()
    {
        var user = _users.Seed(FakeUserStore.DomainUser("ivanov", "S-1-5-21-500"));

        await Handler().HandleWindowsAsync(
            "S-1-5-21-500", "ivanov", "Іванов", [], "10.0.0.1", CancellationToken.None);

        Assert.Equal(Now, user.LastSignInAt);
    }

    private User LocalUser()
    {
        var user = new User("petrenko", "Петренко", AuthProvider.Local);
        user.SetPassword("hash");
        return _users.Seed(user);
    }

    private LoginHandler Handler() => new(_users, _hasher, _uow, _clock, NullLogger<LoginHandler>.Instance);
}
