// tests/Ecr.Application.Tests/Security/LoginAttemptReservationTests.cs
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
/// AN-90/L1-03: спроба входу РЕЗЕРВУЄТЬСЯ до перевірки пароля — межа «Max перевірок між блокуваннями» тримається
/// під пачкою паралельних запитів.
/// </summary>
/// <remarks>
/// ⛔ Раніше обробник читав незаблокований запис, перевіряв пароль і лише ПІСЛЯ хибної відповіді збільшував
/// лічильник; пачка з N паралельних запитів перевіряла N паролів, перш ніж лічильник доходив до порогу.
/// Тут стан «бюджет роздано спробами в польоті» задано прямо (лічильник = Max, блокування ще немає): правильний
/// пароль у такому стані не має ні потрапити в <c>Verify</c>, ні дати cookie. Мутація: прибрати
/// <c>TryReserveAttemptAsync</c> з обробника — перший тест червоний (Verify викликано, вхід пройшов).
/// </remarks>
public sealed class LoginAttemptReservationTests
{
    private const string RealHash = "real-hash";
    private const int Max = 3;
    private static readonly DateTime Now = new(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);

    private readonly FakeUserStore _users = new() { Policy = new PasswordPolicy("P", minLength: 12, maxFailedAttempts: Max) };
    private readonly SpyHasher _hasher = new();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly User _user;

    public LoginAttemptReservationTests()
    {
        _clock.UtcNow.Returns(Now);
        _user = new User("petrenko", "Петренко", AuthProvider.Local);
        _user.SetPassword(RealHash);
        _users.Seed(_user);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.4a")]
    public async Task Бюджет_роздано_спробами_в_польоті_правильний_пароль_не_перевіряється_і_не_пускає()
    {
        SetCounter(_user, Max, lockedUntil: null);

        var refusal = await Assert.ThrowsAsync<AccessDeniedException>(
            () => NewHandler().HandleAsync("petrenko", "right-password", "10.0.0.1", CancellationToken.None));

        // 401, як на хибний пароль: 423 відрізняв би запит у пачці підбору (оракул).
        Assert.Equal("ECR-AUTH-0401", refusal.ErrorCode);
        Assert.Equal(0, _hasher.VerifiesAgainst(RealHash));
        Assert.DoesNotContain(_users.Attempts, a => a.IsSuccess);

        // Запис не лишається «без блокування і без резервування»: бюджет вичерпано — отже заблоковано.
        Assert.True(_user.IsLockedOut(Now));
        Assert.Equal(Max, _user.FailedAttempts);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.4a")]
    public async Task Max_хибних_спроб_блокують_а_наступний_правильний_пароль_отримує_423_без_Verify()
    {
        for (var i = 0; i < Max; i++)
        {
            await Assert.ThrowsAsync<AccessDeniedException>(
                () => NewHandler().HandleAsync("petrenko", "wrong-password", "10.0.0.1", CancellationToken.None));
        }

        Assert.True(_user.IsLockedOut(Now));
        Assert.Equal(Max, _hasher.VerifiesAgainst(RealHash));

        var locked = await Assert.ThrowsAsync<BusinessRuleException>(
            () => NewHandler().HandleAsync("petrenko", "right-password", "10.0.0.1", CancellationToken.None));

        Assert.Equal("ECR-AUTH-0423", locked.ErrorCode);
        Assert.Equal(Max, _hasher.VerifiesAgainst(RealHash));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.4a")]
    public async Task Правильний_пароль_до_межі_пускає_і_скидає_лічильник()
    {
        SetCounter(_user, Max - 1, lockedUntil: null);
        _hasher.AcceptAgainst = RealHash;

        var result = await NewHandler().HandleAsync("petrenko", "right-password", "10.0.0.1", CancellationToken.None);

        Assert.Equal(_user.Id, result.UserId);
        Assert.Equal(0, _user.FailedAttempts);
        Assert.Null(_user.LockedUntil);
    }

    private LoginHandler NewHandler()
        => new(_users, _hasher, Substitute.For<IUnitOfWork>(), _clock, NullLogger<LoginHandler>.Instance);

    private static void SetCounter(User user, int failed, DateTime? lockedUntil)
    {
        typeof(User).GetProperty(nameof(User.FailedAttempts))!.SetValue(user, failed);
        typeof(User).GetProperty(nameof(User.LockedUntil))!.SetValue(user, lockedUntil);
    }

    /// <summary>Хешер, що рахує виклики <c>Verify</c> за хешем; приймає пароль лише для заданого хеша.</summary>
    private sealed class SpyHasher : IPasswordHasher
    {
        private readonly List<string> _verified = [];

        public string? AcceptAgainst { get; set; }

        public int VerifiesAgainst(string hash)
        {
            lock (_verified)
            {
                return _verified.Count(h => h == hash);
            }
        }

        public string Hash(string password) => "decoy-hash";

        public bool Verify(string password, string hash)
        {
            lock (_verified)
            {
                _verified.Add(hash);
            }

            return AcceptAgainst is not null && hash == AcceptAgainst;
        }

        public bool NeedsRehash(string hash) => false;
    }
}
