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
/// PSS-24: що саме <see cref="LoginHandler"/> передає журналу входів — успіх, відмова з кодом причини, блокування.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНІ ДОКАЗИ: прибрати <c>JournalAsync</c> з <c>FailAsync</c> — червоніють тести відмов; прибрати його після
/// <c>RecordAttempt(… true …)</c> — червоніє тест успіху; передати <c>userId</c> у гілку «немає такого» — червоніє тест
/// «невідоме ім'я не розкриває, що запис існує».
/// </remarks>
public sealed class LoginAuditJournalingTests
{
    private const string RealHash = "real-hash";
    private const int Max = 3;
    private static readonly DateTime Now = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);

    private readonly FakeUserStore _users = new() { Policy = new PasswordPolicy("P", minLength: 12, maxFailedAttempts: Max) };
    private readonly Hasher _hasher = new();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly RecordingJournal _journal = new();
    private readonly User _user;

    public LoginAuditJournalingTests()
    {
        _clock.UtcNow.Returns(Now);
        _user = new User("petrenko", "Петренко", AuthProvider.Local);
        _user.SetPassword(RealHash);
        _users.Seed(_user);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "PSS-24")]
    public async Task Успішний_вхід_пишеться_LoginSucceeded_з_користувачем_адресою_і_без_причини()
    {
        _hasher.Accept = true;

        await NewHandler().HandleAsync("petrenko", "right", "198.51.100.1", CancellationToken.None);

        var entry = Assert.Single(_journal.Entries);
        Assert.Equal(LoginAuditEventTypes.Succeeded, entry.EventType);
        Assert.Equal("petrenko", entry.UserName);
        Assert.Equal(_user.Id, entry.UserId);
        Assert.Equal("198.51.100.1", entry.IpAddress);
        Assert.Null(entry.Reason);
        Assert.Equal(Now, entry.At);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "PSS-24")]
    public async Task Хибний_пароль_пишеться_LoginFailed_BadPassword_і_пароль_у_журнал_не_йде()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => NewHandler().HandleAsync("petrenko", "TOP-SECRET-guess", "198.51.100.1", CancellationToken.None));

        var entry = Assert.Single(_journal.Entries);
        Assert.Equal(LoginAuditEventTypes.Failed, entry.EventType);
        Assert.Equal("BadPassword", entry.Reason);
        Assert.Equal(_user.Id, entry.UserId);
        Assert.DoesNotContain("TOP-SECRET", entry.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "PSS-24")]
    public async Task Невідоме_ім_я_пишеться_без_ідентифікатора_користувача()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => NewHandler().HandleAsync("ghost", "x", "198.51.100.1", CancellationToken.None));

        var entry = Assert.Single(_journal.Entries);
        Assert.Equal(LoginAuditEventTypes.Failed, entry.EventType);
        Assert.Equal("UnknownUser", entry.Reason);
        Assert.Null(entry.UserId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "PSS-24")]
    public async Task Вимкнений_запис_для_журналу_входів_теж_невідомий_без_ідентифікатора()
    {
        var disabled = new User("disabled", "Вимкнений", AuthProvider.Local);
        disabled.SetPassword(RealHash);
        disabled.DisableAsBootstrap();
        _users.Seed(disabled);

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => NewHandler().HandleAsync("disabled", "x", "198.51.100.1", CancellationToken.None));

        var entry = Assert.Single(_journal.Entries);
        Assert.Equal("UnknownUser", entry.Reason);
        Assert.Null(entry.UserId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "PSS-24")]
    public async Task Остання_спроба_бюджету_пише_LoginFailed_і_один_AccountLocked_а_наступна_на_заблокованому_лише_LoginFailed()
    {
        for (var i = 0; i < Max; i++)
        {
            await Assert.ThrowsAsync<AccessDeniedException>(
                () => NewHandler().HandleAsync("petrenko", "wrong", "198.51.100.1", CancellationToken.None));
        }

        Assert.Equal(Max, _journal.Entries.Count(e => e.EventType == LoginAuditEventTypes.Failed));
        var locked = Assert.Single(_journal.Entries, e => e.EventType == LoginAuditEventTypes.AccountLocked);
        Assert.Equal(_user.Id, locked.UserId);

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => NewHandler().HandleAsync("petrenko", "right", "198.51.100.1", CancellationToken.None));

        Assert.Equal(Max + 1, _journal.Entries.Count(e => e.EventType == LoginAuditEventTypes.Failed));
        Assert.Single(_journal.Entries, e => e.EventType == LoginAuditEventTypes.AccountLocked);
        Assert.Equal("LockedOut", _journal.Entries.Last().Reason);
    }

    private LoginHandler NewHandler()
        => new(
            _users, _hasher, Substitute.For<IUnitOfWork>(), _clock, NullLogger<LoginHandler>.Instance,
            disableBootstrap: null, journal: _journal);

    private sealed class RecordingJournal : ILoginAuditJournal
    {
        public List<LoginAuditEntry> Entries { get; } = [];

        public Task RecordAsync(LoginAuditEntry entry, CancellationToken ct)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class Hasher : IPasswordHasher
    {
        public bool Accept { get; set; }

        public string Hash(string password) => "decoy-hash";

        public bool Verify(string password, string hash) => Accept && hash == RealHash;

        public bool NeedsRehash(string hash) => false;
    }
}
