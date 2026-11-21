using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecr.Api.Security;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// PSS-24: форма запису події входу, автор, маска невідомого імені й обмежувач флуду невдалих спроб (без бази).
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНІ ДОКАЗИ: прибрати обмежувач (<c>TryAdmit</c> → допускати все) — червоніють тести флуду; не вертати місце
/// у вікні після збою запису (<c>Release</c>) — червоніє тест збою; почати обмежувати <c>AccountLocked</c> чи
/// <c>LoginSucceeded</c> — червоніє тест «рідкісні події не обмежуються»; писати сире ім'я замість маски — червоніють
/// тести форми й приватності; автором відмови поставити користувача — червоніє тест автора; не виносити лічильник
/// пропущених у підсумок — червоніє тест підмітання; не писати подію переповнення — червоніє тест переповнення.
/// </remarks>
public sealed class LoginAuditJournalTests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "PSS-24")]
    public async Task Флуд_однакових_невдалих_спроб_обмежується_за_вікно_із_лічильником_пропущених()
    {
        var h = new Harness();

        for (var i = 0; i < 100; i++)
        {
            await h.RecordAsync(Failed("victim", "BadPassword", "203.0.113.5", userId: 7));
        }

        Assert.Equal(LoginAuditJournal.MaxPerKeyPerWindow, h.Written.Count);

        // Наступне вікно: пропущене не губиться, а рахується в першому записі нового вікна.
        h.Now = T0 + LoginAuditJournal.DefaultWindow;
        await h.RecordAsync(Failed("victim", "BadPassword", "203.0.113.5", userId: 7));

        Assert.Equal(LoginAuditJournal.MaxPerKeyPerWindow + 1, h.Written.Count);
        var last = JsonDocument.Parse(h.Written.Last().DetailsJson!).RootElement;
        Assert.Equal(100 - LoginAuditJournal.MaxPerKeyPerWindow, last.GetProperty("suppressed").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "PSS-24")]
    public async Task Лічильник_пропущених_ключа_що_не_повернувся_виноситься_підсумковою_подією_а_не_губиться()
    {
        var h = new Harness(maxPerWindow: 1);

        for (var i = 0; i < 4; i++)
        {
            await h.RecordAsync(Failed("victim", "BadPassword", "203.0.113.5", userId: 7));
        }

        Assert.Single(h.Written); // 3 пропущено

        // Ключ «victim» більше не повертається; через два вікна приходить інша спроба — підмітання виносить лічильник.
        h.Now = T0 + (2 * LoginAuditJournal.DefaultWindow);
        await h.RecordAsync(Failed("other", "BadPassword", "203.0.113.6", userId: 8));

        var summary = Assert.Single(h.Written, r => r.EventType == LoginAuditEventTypes.SuppressedSummary);
        var details = JsonDocument.Parse(summary.DetailsJson!).RootElement;
        Assert.Equal(3, details.GetProperty("suppressed").GetInt32());
        Assert.Equal(1, details.GetProperty("keys").GetInt32());
        Assert.Equal(LoginAuditEventTypes.AnonymousActorId, summary.ChangedByUserId);
        Assert.Equal(3, h.Written.Count); // перша victim, підсумок, other
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "PSS-24")]
    public async Task Переповнення_таблиці_ключів_пише_подію_раз_на_вікно_з_кількістю_відкинутих()
    {
        var h = new Harness(maxKeys: 2);

        await h.RecordAsync(Failed("a", "BadPassword", "203.0.113.1", userId: 1));
        await h.RecordAsync(Failed("b", "BadPassword", "203.0.113.2", userId: 2));
        await h.RecordAsync(Failed("c", "BadPassword", "203.0.113.3", userId: 3)); // відкинуто: таблиця повна
        await h.RecordAsync(Failed("d", "BadPassword", "203.0.113.4", userId: 4)); // відкинуто, події вдруге немає

        var overflow = Assert.Single(h.Written, r => r.EventType == LoginAuditEventTypes.AuditOverflow);
        var details = JsonDocument.Parse(overflow.DetailsJson!).RootElement;
        Assert.Equal(2, details.GetProperty("limit").GetInt32());
        Assert.Equal(1, details.GetProperty("dropped").GetInt32());
        Assert.Equal(LoginAuditEventTypes.AnonymousActorId, overflow.ChangedByUserId);

        // Наступне вікно: ключі застаріли, місце є, нові спроби пишуться знову.
        h.Now = T0 + (2 * LoginAuditJournal.DefaultWindow);
        await h.RecordAsync(Failed("e", "BadPassword", "203.0.113.5", userId: 5));

        Assert.Equal(3, h.Written.Count(r => r.EventType == LoginAuditEventTypes.Failed));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "PSS-24")]
    public async Task Іншим_іменем_причиною_чи_адресою_ключ_інший_а_регістр_імені_не_плодить_ключів()
    {
        var h = new Harness(maxPerWindow: 1);

        await h.RecordAsync(Failed("victim", "BadPassword", "203.0.113.5", userId: 7));
        await h.RecordAsync(Failed("VICTIM", "BadPassword", "203.0.113.5", userId: 7)); // той самий ключ — пропущено
        await h.RecordAsync(Failed("victim", "LockedOut", "203.0.113.5", userId: 7));
        await h.RecordAsync(Failed("victim", "BadPassword", "203.0.113.6", userId: 7));
        await h.RecordAsync(Failed("other", "BadPassword", "203.0.113.5", userId: 8));

        Assert.Equal(4, h.Written.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "PSS-24")]
    public async Task Успіх_і_блокування_не_обмежуються_флудом()
    {
        var h = new Harness(maxPerWindow: 1);

        for (var i = 0; i < 5; i++)
        {
            await h.RecordAsync(new LoginAuditEntry(
                LoginAuditEventTypes.Succeeded, "u", AuthProvider.Local, null, "203.0.113.5", 7, T0));
            await h.RecordAsync(new LoginAuditEntry(
                LoginAuditEventTypes.AccountLocked, "u", AuthProvider.Local, "LockedOut", "203.0.113.5", 7, T0));
        }

        Assert.Equal(10, h.Written.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "PSS-24")]
    public async Task Невідоме_ім_я_йде_в_журнал_лише_маскою_без_сирого_імені()
    {
        var h = new Harness();
        const string typed = "MyS3cretPassw0rd!";

        await h.RecordAsync(Failed(typed, "UnknownUser", "203.0.113.5", userId: null));

        var record = Assert.Single(h.Written);
        Assert.Equal(LoginAuditEventTypes.AnonymousActorId, record.ChangedByUserId);
        Assert.Null(record.TargetUserId);
        Assert.Equal("corr-1", record.CorrelationId);
        Assert.Equal(T0, record.ChangedAt);

        // ⛔ Ні сирого імені, ні його фрагмента довшого за два символи.
        Assert.DoesNotContain(typed, record.DetailsJson!, StringComparison.Ordinal);
        Assert.DoesNotContain(typed[..3], record.DetailsJson!, StringComparison.Ordinal);

        var details = JsonDocument.Parse(record.DetailsJson!).RootElement;
        Assert.Equal(
            ["ip", "outcome", "provider", "reason", "userNameHash", "userNameLength", "userNamePrefix"],
            details.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("My", details.GetProperty("userNamePrefix").GetString());
        Assert.Equal(typed.Length, details.GetProperty("userNameLength").GetInt32());
        Assert.Equal(Harness.HashOf(typed), details.GetProperty("userNameHash").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "PSS-24")]
    public async Task Автор_відмови_і_блокування_анонімний_актор_а_користувач_у_TargetUserId_автор_успіху_сам_користувач()
    {
        var h = new Harness();

        await h.RecordAsync(Failed("alice", "BadPassword", null, userId: 42));
        await h.RecordAsync(new LoginAuditEntry(
            LoginAuditEventTypes.AccountLocked, "alice", AuthProvider.Local, "LockedOut", null, 42, T0));
        await h.RecordAsync(new LoginAuditEntry(
            LoginAuditEventTypes.Succeeded, "alice", AuthProvider.Windows, null, null, 42, T0));

        var records = h.Written.ToList();
        foreach (var failure in records.Take(2))
        {
            Assert.Equal(LoginAuditEventTypes.AnonymousActorId, failure.ChangedByUserId);
            Assert.Equal(42, failure.TargetUserId);
            var details = JsonDocument.Parse(failure.DetailsJson!).RootElement;
            Assert.Equal("alice", details.GetProperty("userName").GetString()); // наявний обліковий запис — ім'я не маскується
            Assert.False(details.TryGetProperty("ip", out _));
        }

        Assert.Equal(42, records[2].ChangedByUserId);
        Assert.Equal(42, records[2].TargetUserId);
        Assert.Equal("Windows", JsonDocument.Parse(records[2].DetailsJson!).RootElement.GetProperty("provider").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "PSS-24")]
    public async Task Адреса_сокета_пишеться_лише_коли_відрізняється_від_адреси_клієнта()
    {
        var h = new Harness();

        await h.RecordAsync(Failed("a", "BadPassword", "203.0.113.5", userId: 1, socketIp: "10.0.0.5"));
        await h.RecordAsync(Failed("b", "BadPassword", "203.0.113.5", userId: 2, socketIp: "203.0.113.5"));
        await h.RecordAsync(Failed("c", "BadPassword", "203.0.113.5", userId: 3));

        var details = h.Written.Select(r => JsonDocument.Parse(r.DetailsJson!).RootElement.Clone()).ToList();
        Assert.Equal("10.0.0.5", details[0].GetProperty("socketIp").GetString());
        Assert.False(details[1].TryGetProperty("socketIp", out _));
        Assert.False(details[2].TryGetProperty("socketIp", out _));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "PSS-24")]
    public async Task Невдалий_запис_не_кидає_і_не_витрачає_місця_у_вікні()
    {
        var h = new Harness(maxPerWindow: 1) { FailWrites = true };

        await h.RecordAsync(Failed("victim", "BadPassword", "203.0.113.5", userId: 7));
        Assert.Empty(h.Written);

        // База ожила: та сама спроба тепер проходить, бо місце не було спожито.
        h.FailWrites = false;
        await h.RecordAsync(Failed("victim", "BadPassword", "203.0.113.5", userId: 7));
        Assert.Single(h.Written);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "PSS-24")]
    public async Task Запис_іде_незалежним_методом_а_не_приєднаним_до_транзакції()
    {
        var h = new Harness();

        await h.RecordAsync(Failed("victim", "BadPassword", "203.0.113.5", userId: 7));

        Assert.Equal(1, h.IndependentCalls);
        Assert.Equal(0, h.TransactionalCalls);
    }

    private static LoginAuditEntry Failed(string name, string reason, string? ip, int? userId, string? socketIp = null)
        => new(LoginAuditEventTypes.Failed, name, AuthProvider.Local, reason, ip, userId, T0, socketIp);

    private sealed class Harness
    {
        private static readonly byte[] Key = Encoding.UTF8.GetBytes("test-key-test-key-test-key-12345");

        private readonly ConcurrentQueue<SecurityEventRecord> _written = new();
        private readonly LoginAuditJournal _journal;
        private int _independent;
        private int _transactional;

        public Harness(int maxPerWindow = LoginAuditJournal.MaxPerKeyPerWindow, int maxKeys = LoginAuditJournal.MaxTrackedKeys)
        {
            var clock = Substitute.For<IClock>();
            clock.UtcNow.Returns(_ => Now);

            var correlation = Substitute.For<ICorrelationIdAccessor>();
            correlation.CorrelationId.Returns("corr-1");

            var masker = Substitute.For<ILoginNameMasker>();
            masker.MaskAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var name = call.Arg<string>();
                    return Task.FromResult(new LoginNameMask(name.Length <= 2 ? name : name[..2], name.Length, HashOf(name)));
                });

            var writer = Substitute.For<IAuditWriter>();
            writer.WriteIndependentSecurityEventAsync(Arg.Any<SecurityEventRecord>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Interlocked.Increment(ref _independent);
                    if (FailWrites)
                    {
                        return Task.FromException(new InvalidOperationException("db down"));
                    }

                    _written.Enqueue(call.Arg<SecurityEventRecord>());
                    return Task.CompletedTask;
                });
            writer.WriteSecurityEventAsync(Arg.Any<SecurityEventRecord>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    Interlocked.Increment(ref _transactional);
                    return Task.CompletedTask;
                });

            var services = new ServiceCollection();
            services.AddScoped(_ => writer);
            var provider = services.BuildServiceProvider();

            _journal = new LoginAuditJournal(
                provider.GetRequiredService<IServiceScopeFactory>(), correlation, masker, clock,
                NullLogger<LoginAuditJournal>.Instance, LoginAuditJournal.DefaultWindow, maxPerWindow, maxKeys);
        }

        public DateTime Now { get; set; } = T0;

        public bool FailWrites { get; set; }

        public ConcurrentQueue<SecurityEventRecord> Written => _written;

        public int IndependentCalls => _independent;

        public int TransactionalCalls => _transactional;

        public static string HashOf(string name)
            => Convert.ToHexString(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(name.ToUpperInvariant())))
                .ToLowerInvariant()[..32];

        public Task RecordAsync(LoginAuditEntry entry) => _journal.RecordAsync(entry, CancellationToken.None);
    }
}
