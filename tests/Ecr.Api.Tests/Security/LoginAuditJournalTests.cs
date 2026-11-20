using System.Collections.Concurrent;
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
/// PSS-24: форма запису події входу й обмежувач флуду невдалих спроб (без бази).
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНІ ДОКАЗИ: прибрати обмежувач (<c>TryAdmit</c> → <c>true</c>) — червоніють тести флуду; не вертати місце у
/// вікні після збою запису (<c>Release</c>) — червоніє тест збою; почати обмежувати <c>AccountLocked</c> чи
/// <c>LoginSucceeded</c> — червоніє тест «рідкісні події не обмежуються».
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
            await h.RecordAsync(Failed("victim", "BadPassword", "203.0.113.5"));
        }

        Assert.Equal(LoginAuditJournal.MaxPerKeyPerWindow, h.Written.Count);

        // Наступне вікно: пропущене не губиться, а рахується в першому записі нового вікна.
        h.Now = T0 + LoginAuditJournal.DefaultWindow;
        await h.RecordAsync(Failed("victim", "BadPassword", "203.0.113.5"));

        Assert.Equal(LoginAuditJournal.MaxPerKeyPerWindow + 1, h.Written.Count);
        var last = JsonDocument.Parse(h.Written.Last().DetailsJson!).RootElement;
        Assert.Equal(100 - LoginAuditJournal.MaxPerKeyPerWindow, last.GetProperty("suppressed").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "PSS-24")]
    public async Task Іншим_іменем_причиною_чи_адресою_ключ_інший_а_регістр_імені_не_плодить_ключів()
    {
        var h = new Harness(maxPerWindow: 1);

        await h.RecordAsync(Failed("victim", "BadPassword", "203.0.113.5"));
        await h.RecordAsync(Failed("VICTIM", "BadPassword", "203.0.113.5")); // той самий ключ — пропущено
        await h.RecordAsync(Failed("victim", "LockedOut", "203.0.113.5"));
        await h.RecordAsync(Failed("victim", "BadPassword", "203.0.113.6"));
        await h.RecordAsync(Failed("other", "BadPassword", "203.0.113.5"));

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
    public async Task Форма_запису_лише_ім_я_результат_причина_адреса_і_анонімний_автор_для_невідомого()
    {
        var h = new Harness();

        await h.RecordAsync(Failed("ghost", "UnknownUser", "203.0.113.5"));
        await h.RecordAsync(new LoginAuditEntry(
            LoginAuditEventTypes.Failed, "alice", AuthProvider.Windows, "Disabled", null, 42, T0));

        var anonymous = h.Written.First();
        Assert.Equal(LoginAuditEventTypes.AnonymousActorId, anonymous.ChangedByUserId);
        Assert.Null(anonymous.TargetUserId);
        Assert.Equal("corr-1", anonymous.CorrelationId);
        Assert.Equal(T0, anonymous.ChangedAt);
        Assert.Equal(
            ["ip", "outcome", "provider", "reason", "userName"],
            JsonDocument.Parse(anonymous.DetailsJson!).RootElement.EnumerateObject()
                .Select(p => p.Name).Order(StringComparer.Ordinal));

        var known = h.Written.Last();
        Assert.Equal(42, known.ChangedByUserId);
        Assert.Equal(42, known.TargetUserId);
        var details = JsonDocument.Parse(known.DetailsJson!).RootElement;
        Assert.Equal("Windows", details.GetProperty("provider").GetString());
        Assert.False(details.TryGetProperty("ip", out _));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "PSS-24")]
    public async Task Невдалий_запис_не_кидає_і_не_витрачає_місця_у_вікні()
    {
        var h = new Harness(maxPerWindow: 1) { FailWrites = true };

        await h.RecordAsync(Failed("victim", "BadPassword", "203.0.113.5"));
        Assert.Empty(h.Written);

        // База ожила: та сама спроба тепер проходить, бо місце не було спожито.
        h.FailWrites = false;
        await h.RecordAsync(Failed("victim", "BadPassword", "203.0.113.5"));
        Assert.Single(h.Written);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "PSS-24")]
    public async Task Запис_іде_незалежним_методом_а_не_приєднаним_до_транзакції()
    {
        var h = new Harness();

        await h.RecordAsync(Failed("victim", "BadPassword", "203.0.113.5"));

        Assert.Equal(1, h.IndependentCalls);
        Assert.Equal(0, h.TransactionalCalls);
    }

    private static LoginAuditEntry Failed(string name, string reason, string ip)
        => new(LoginAuditEventTypes.Failed, name, AuthProvider.Local, reason, ip, null, T0);

    private sealed class Harness
    {
        private readonly ConcurrentQueue<SecurityEventRecord> _written = new();
        private readonly LoginAuditJournal _journal;
        private int _independent;
        private int _transactional;

        public Harness(int maxPerWindow = LoginAuditJournal.MaxPerKeyPerWindow)
        {
            var clock = Substitute.For<IClock>();
            clock.UtcNow.Returns(_ => Now);

            var correlation = Substitute.For<ICorrelationIdAccessor>();
            correlation.CorrelationId.Returns("corr-1");

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
                provider.GetRequiredService<IServiceScopeFactory>(), correlation, clock,
                NullLogger<LoginAuditJournal>.Instance, LoginAuditJournal.DefaultWindow, maxPerWindow);
        }

        public DateTime Now { get; set; } = T0;

        public bool FailWrites { get; set; }

        public ConcurrentQueue<SecurityEventRecord> Written => _written;

        public int IndependentCalls => _independent;

        public int TransactionalCalls => _transactional;

        public Task RecordAsync(LoginAuditEntry entry) => _journal.RecordAsync(entry, CancellationToken.None);
    }
}
