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
    public async Task Вимкнений_запис_у_журналі_безпеки_має_справжню_причину_і_користувача_а_відповідь_та_сама()
    {
        var disabled = new User("disabled", "Вимкнений", AuthProvider.Local);
        disabled.SetPassword(RealHash);
        disabled.DisableAsBootstrap();
        _users.Seed(disabled);

        var refusal = await Assert.ThrowsAsync<AccessDeniedException>(
            () => NewHandler().HandleAsync("disabled", "x", "198.51.100.1", CancellationToken.None));
        var unknown = await Assert.ThrowsAsync<AccessDeniedException>(
            () => NewHandler().HandleAsync("ghost", "x", "198.51.100.1", CancellationToken.None));

        // 401 і текст ті самі, що на невідоме ім'я (форма входу не розкриває існування запису).
        Assert.Equal(unknown.ErrorCode, refusal.ErrorCode);
        Assert.Equal(unknown.Message, refusal.Message);

        // M3: журнал безпеки пише справжню причину й користувача; sec.LoginAttempt лишає UnknownUser.
        var entry = _journal.Entries[0];
        Assert.Equal("Disabled", entry.Reason);
        Assert.Equal(disabled.Id, entry.UserId);
        Assert.Equal("UnknownUser", _users.Attempts.First(a => a.UserName == "disabled").FailReason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "PSS-24")]
    public async Task Службовий_і_нелокальний_записи_мають_свої_причини_в_журналі_безпеки()
    {
        var service = new User(User.IntegrationServiceUserName, "Служба", AuthProvider.Local);
        service.SetPassword(RealHash);
        _users.Seed(service);
        var windows = new User("domain.user", "Доменний", AuthProvider.Windows);
        _users.Seed(windows);

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => NewHandler().HandleAsync(User.IntegrationServiceUserName, "x", "198.51.100.1", CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => NewHandler().HandleAsync("domain.user", "x", "198.51.100.1", CancellationToken.None));

        Assert.Equal("ServiceAccount", _journal.Entries[0].Reason);
        Assert.Equal(service.Id, _journal.Entries[0].UserId);
        Assert.Equal("NotLocal", _journal.Entries[1].Reason);
        Assert.Equal(windows.Id, _journal.Entries[1].UserId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "PSS-24")]
    public async Task Правильний_пароль_у_гонці_з_блокуванням_має_окрему_причину_а_відповідь_401()
    {
        _hasher.Accept = true;
        // Блокування лягає ПІСЛЯ резервування спроби й до успішної реєстрації — під час перевірки пароля.
        _hasher.OnVerify = () => typeof(User).GetProperty(nameof(User.LockedUntil))!
            .SetValue(_user, Now.AddMinutes(15));

        var refusal = await Assert.ThrowsAsync<AccessDeniedException>(
            () => NewHandler().HandleAsync("petrenko", "right", "198.51.100.1", CancellationToken.None));

        Assert.Equal("ECR-AUTH-0401", refusal.ErrorCode);
        var entry = Assert.Single(_journal.Entries);
        Assert.Equal(LoginAuditEventTypes.Failed, entry.EventType);
        Assert.Equal("LockedOutCorrectPassword", entry.Reason);
        Assert.Equal(_user.Id, entry.UserId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "PSS-24")]
    public async Task Адреса_сокета_передається_журналу_поряд_з_адресою_клієнта()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => NewHandler().HandleAsync("petrenko", "wrong", "203.0.113.7", CancellationToken.None, socketIp: "10.0.0.5"));

        var entry = Assert.Single(_journal.Entries);
        Assert.Equal("203.0.113.7", entry.IpAddress);
        Assert.Equal("10.0.0.5", entry.SocketIp);
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

        public Action? OnVerify { get; set; }

        public bool Verify(string password, string hash)
        {
            OnVerify?.Invoke();
            return Accept && hash == RealHash;
        }

        public bool NeedsRehash(string hash) => false;
    }
}
