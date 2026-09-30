// tests/Ecr.Application.Tests/Security/ChangePasswordCounterResetTests.cs
using Ecr.Application.Common;
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
/// S9: успішна зміна пароля обнуляє лічильник невдалих спроб (ФВ-6.4a).
/// </summary>
/// <remarks>
/// ⛔ Лічильник спільний для входу й зміни пароля. Без скидання хибні спроби
/// ДО зміни лишалися висіти: людина, що нарешті ввела правильний чинний пароль
/// і змінила його, після однієї помилки з новим паролем опинялася заблокованою.
/// </remarks>
public sealed class ChangePasswordCounterResetTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.4a")]
    public async Task Успішна_зміна_пароля_обнуляє_невдалі_спроби()
    {
        var users = new FakeUserStore();
        var user = new User("petrenko", "Петренко", AuthProvider.Local);
        user.SetPassword("old-hash");
        users.Seed(user);

        for (var i = 0; i < users.Policy.MaxFailedAttempts - 1; i++)
        {
            await users.RegisterFailedAttemptAsync(
                user.Id, users.Policy.MaxFailedAttempts, users.Policy.LockoutMinutes, Now, CancellationToken.None);
        }

        Assert.Equal(users.Policy.MaxFailedAttempts - 1, user.FailedAttempts);

        var hasher = Substitute.For<IPasswordHasher>();
        hasher.Verify("old-password", "old-hash").Returns(true);
        hasher.Hash(Arg.Any<string>()).Returns("new-hash");

        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(user.Id);

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);

        var handler = new ChangePasswordHandler(
            users, hasher, Substitute.For<IUnitOfWork>(), Substitute.For<IAuditWriter>(), currentUser, clock);

        await handler.HandleAsync("old-password", "a-long-enough-new-password-2026", CancellationToken.None);

        // ⛔ Мутаційний доказ: прибрати `user.Unlock()` з `ChangePasswordHandler`
        // — тут лишається MaxFailedAttempts - 1.
        Assert.Equal(0, user.FailedAttempts);
        Assert.Null(user.LockedUntil);
        Assert.Equal("new-hash", user.PasswordHash);
    }
}
