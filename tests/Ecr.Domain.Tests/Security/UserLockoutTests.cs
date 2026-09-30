// tests/Ecr.Domain.Tests/Security/UserLockoutTests.cs
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Security;

/// <summary>
/// S8(б): блокування після невдалих спроб минає РАЗОМ із лічильником (ФВ-6.4a).
/// </summary>
/// <remarks>
/// ⛔ Поки лічильник не скидався після закінчення блокування, перша ж хибна
/// спроба після нього знову давала <c>FailedAttempts ≥ межі</c> і блокувала
/// ще на 15 хв. Одна спроба на чверть години — і законний власник не входить
/// ніколи: відмова в обслуговуванні за ціною одного запиту.
/// </remarks>
public sealed class UserLockoutTests
{
    private const int Max = 5;
    private const int Minutes = 15;
    private static readonly DateTime T0 = new(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.4a")]
    public void Після_закінчення_блокування_одна_хибна_спроба_не_блокує_знову()
    {
        var user = LockedAt(T0);
        var afterLock = T0.AddMinutes(Minutes + 1);

        Assert.False(user.IsLockedOut(afterLock));

        var locked = user.RegisterFailedAttempt(Max, Minutes, afterLock);

        // ⛔ Мутаційний доказ: прибрати скидання лічильника в
        // `RegisterFailedAttempt` — тут true і FailedAttempts == Max + 1.
        Assert.False(locked);
        Assert.False(user.IsLockedOut(afterLock));
        Assert.Equal(1, user.FailedAttempts);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.4a")]
    public void Після_закінчення_блокування_знову_потрібно_Max_спроб()
    {
        var user = LockedAt(T0);
        var later = T0.AddMinutes(Minutes + 1);

        for (var i = 1; i < Max; i++)
        {
            Assert.False(user.RegisterFailedAttempt(Max, Minutes, later));
        }

        Assert.True(user.RegisterFailedAttempt(Max, Minutes, later));
        Assert.Equal(later.AddMinutes(Minutes), user.LockedUntil);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.4a")]
    public void Під_час_блокування_лічильник_не_скидається()
    {
        var user = LockedAt(T0);

        // Ще триває: «минуле» блокування — лише те, що вже скінчилося.
        user.RegisterFailedAttempt(Max, Minutes, T0.AddMinutes(1));

        Assert.Equal(Max + 1, user.FailedAttempts);
        Assert.True(user.IsLockedOut(T0.AddMinutes(2)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "BE-12")]
    public void Адміністративне_блокування_хибна_спроба_не_знімає_і_не_скорочує()
    {
        var user = new User("admlock", "admlock", AuthProvider.Local);
        user.LockByAdministrator();

        for (var i = 0; i < Max + 2; i++)
        {
            user.RegisterFailedAttempt(Max, Minutes, T0);
        }

        Assert.True(user.IsLockedByAdministrator);
    }

    private static User LockedAt(DateTime at)
    {
        var user = new User("lockout", "lockout", AuthProvider.Local);

        for (var i = 0; i < Max; i++)
        {
            user.RegisterFailedAttempt(Max, Minutes, at);
        }

        Assert.True(user.IsLockedOut(at));
        Assert.Equal(Max, user.FailedAttempts);
        return user;
    }
}
