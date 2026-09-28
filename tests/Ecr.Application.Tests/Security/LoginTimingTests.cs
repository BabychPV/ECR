// tests/Ecr.Application.Tests/Security/LoginTimingTests.cs
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
/// S8(а): невідоме ім'я і хибний пароль коштують ОДНАКОВО — рівно один
/// <c>Verify</c> і нуль <c>Hash</c> на запит (ФВ-6.11).
/// </summary>
/// <remarks>
/// ⛔ Детерміновано, без заміру часу: вартість запиту — це кількість
/// викликів PBKDF2, і саме її рахує лічильник. <c>LoginHandler</c> — Scoped,
/// тобто НОВИЙ екземпляр на кожен запит; тому тут новий обробник на кожен
/// виклик. Поки хеш-приманка жила в полі екземпляра, невідоме ім'я коштувало
/// Hash + Verify (2× PBKDF2), а наявне з хибним паролем — лише Verify (1×):
/// різниця ~100 мс, видима на графіку затримок.
/// </remarks>
public sealed class LoginTimingTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc);

    private readonly FakeUserStore _users = new();
    private readonly CountingHasher _hasher = new();
    private readonly IClock _clock = Substitute.For<IClock>();

    public LoginTimingTests()
    {
        _clock.UtcNow.Returns(Now);

        var user = new User("petrenko", "Петренко", AuthProvider.Local);
        user.SetPassword("real-hash");
        _users.Seed(user);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.11")]
    public async Task Невідоме_ім_я_і_хибний_пароль_коштують_один_Verify_і_нуль_Hash()
    {
        // Прогрів: приманка рахується раз на процес — перший запит процесу
        // може її порахувати, і це не вартість конкретного запиту.
        await FailAsync("немає-такого");
        _hasher.Reset();

        await FailAsync("немає-такого");
        var unknown = (_hasher.Hashes, _hasher.Verifies);
        _hasher.Reset();

        await FailAsync("ще-одне-невідоме");
        var unknownAgain = (_hasher.Hashes, _hasher.Verifies);
        _hasher.Reset();

        await FailAsync("petrenko");
        var wrongPassword = (_hasher.Hashes, _hasher.Verifies);

        // ⛔ Мутаційний доказ: повернути приманку в поле екземпляра
        // (`_decoyHash ??= hasher.Hash(...)`) — невідоме ім'я дає (1, 1).
        Assert.Equal((0, 1), unknown);
        Assert.Equal((0, 1), unknownAgain);
        Assert.Equal((0, 1), wrongPassword);
    }

    private async Task FailAsync(string userName)
    {
        // Новий обробник на кожен запит — як Scoped-реєстрація в контейнері.
        var handler = new LoginHandler(
            _users, _hasher, Substitute.For<IUnitOfWork>(), _clock, NullLogger<LoginHandler>.Instance);

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => handler.HandleAsync(userName, "wrong-password", "10.0.0.1", CancellationToken.None));
    }

    /// <summary>Хешер-лічильник: «PBKDF2» тут — лише інкремент.</summary>
    /// <remarks>
    /// ⚠ Власний тип, а не підстановка NSubstitute: приманка кешується на
    /// ТИП хешера, і спільний проксі-тип зв'язав би цей тест з іншими.
    /// </remarks>
    private sealed class CountingHasher : IPasswordHasher
    {
        private int _hashes;
        private int _verifies;

        public int Hashes => Volatile.Read(ref _hashes);

        public int Verifies => Volatile.Read(ref _verifies);

        public void Reset()
        {
            Interlocked.Exchange(ref _hashes, 0);
            Interlocked.Exchange(ref _verifies, 0);
        }

        public string Hash(string password)
        {
            Interlocked.Increment(ref _hashes);
            return "decoy-hash";
        }

        public bool Verify(string password, string hash)
        {
            Interlocked.Increment(ref _verifies);
            return false;
        }

        public bool NeedsRehash(string hash) => false;
    }
}
