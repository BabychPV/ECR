// tests/Ecr.Infrastructure.Tests/Notifications/NotificationDispatcherTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Notifications;
using Ecr.Infrastructure.Notifications;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Infrastructure.Tests.Notifications;

/// <summary>
/// <c>BE-34</c>: диспетчер «подія → правила → канали» на справжній базі.
/// </summary>
/// <remarks>
/// ⚠ База в колекції <c>SqlServer</c> спільна, і знімок конфігурації бачить
/// УСІ канали, зокрема заведені сусідніми класами. Тому подія тут —
/// <see cref="NotificationEventKind.ConsistencyIssuesFound"/>, якої не вживає
/// жоден інший клас цієї збірки, ключ події несе мітку тесту, а кожне
/// твердження звіряється з ідентифікатором ВЛАСНОГО каналу. Порядок виконання
/// тестів не гарантований, і підрахунок «скільки всього пішло» ловив би чуже.
/// </remarks>
[Collection("SqlServer")]
public sealed class NotificationDispatcherTests(SqlServerFixture sql)
{
    private const NotificationEventKind Kind = NotificationEventKind.ConsistencyIssuesFound;

    private static readonly DateTime Start = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правило_веде_подію_в_канал_і_лишає_рядок_Sent()
    {
        var channelId = await AddChannelAsync("ok", NotificationChannelKind.TeamsWebhook);
        var teams = new FakeSender(NotificationChannelKind.TeamsWebhook);
        var key = $"consistency:{_tag}";

        await using var db = Context();
        var result = await Dispatcher(db, new TestClock(Start), teams)
            .DispatchAsync(Event(key), CancellationToken.None);

        Assert.Contains(channelId, teams.Channels);
        Assert.True(result.Sent >= 1);

        var delivery = Assert.Single(await DeliveriesAsync(channelId, key));
        Assert.Equal(NotificationDeliveryStatus.Sent, delivery.Status);
        Assert.Equal(Kind, delivery.EventKind);
        Assert.Null(delivery.Error);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Провал_одного_каналу_не_валить_розсилку_і_не_блокує_наступний()
    {
        // ⛔ Канал, що падає, заводиться ПЕРШИМ: диспетчер іде за
        // ідентифікатором, тож саме так перевіряється «не блокує наступний», а
        // не «випадково пощастило з порядком».
        var failingId = await AddChannelAsync("boom", NotificationChannelKind.Smtp);
        var workingId = await AddChannelAsync("fine", NotificationChannelKind.TeamsWebhook);
        var key = $"consistency:{_tag}";

        var failing = new FakeSender(NotificationChannelKind.Smtp, fails: true);
        var working = new FakeSender(NotificationChannelKind.TeamsWebhook);

        await using var db = Context();

        // Жодного винятку назовні: сповіщення не транзакційні, і недоступний
        // канал не привід урвати прогін задачі.
        await Dispatcher(db, new TestClock(Start), failing, working)
            .DispatchAsync(Event(key), CancellationToken.None);

        var failed = Assert.Single(await DeliveriesAsync(failingId, key));
        Assert.Equal(NotificationDeliveryStatus.Failed, failed.Status);
        Assert.Contains("500", failed.Error!, StringComparison.Ordinal);

        var sent = Assert.Single(await DeliveriesAsync(workingId, key));
        Assert.Equal(NotificationDeliveryStatus.Sent, sent.Status);
        Assert.Contains(workingId, working.Channels);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Та_сама_подія_в_межах_30_хвилин_дає_Suppressed_а_не_тишу()
    {
        // Вікно — вимога (§2.1 директиви №15), тому число стоїть літералом:
        // твердження проти самої константи рухалося б разом із нею.
        Assert.Equal(TimeSpan.FromMinutes(30), NotificationDispatcher.DeduplicationWindow);

        var channelId = await AddChannelAsync("dedup", NotificationChannelKind.TeamsWebhook);
        var teams = new FakeSender(NotificationChannelKind.TeamsWebhook);
        var clock = new TestClock(Start);
        var key = $"consistency:{_tag}";

        await using var db = Context();
        var dispatcher = Dispatcher(db, clock, teams);

        await dispatcher.DispatchAsync(Event(key), CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(29));
        await dispatcher.DispatchAsync(Event(key), CancellationToken.None);

        var deliveries = await DeliveriesAsync(channelId, key);

        Assert.Equal(
            [NotificationDeliveryStatus.Sent, NotificationDeliveryStatus.Suppressed],
            deliveries.Select(d => d.Status));

        // ⛔ Придушення — це РЯДОК, а не відсутність рядка: інакше «не надіслано»
        // і «надіслано» виглядали б у журналі однаково.
        Assert.NotNull(deliveries[1].Error);

        // У канал пішло рівно одне повідомлення, а не два.
        Assert.Single(teams.Channels, id => id == channelId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Після_30_хвилин_подія_йде_знову()
    {
        var channelId = await AddChannelAsync("again", NotificationChannelKind.TeamsWebhook);
        var teams = new FakeSender(NotificationChannelKind.TeamsWebhook);
        var clock = new TestClock(Start);
        var key = $"consistency:{_tag}";

        await using var db = Context();
        var dispatcher = Dispatcher(db, clock, teams);

        await dispatcher.DispatchAsync(Event(key), CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(31));
        await dispatcher.DispatchAsync(Event(key), CancellationToken.None);

        var deliveries = await DeliveriesAsync(channelId, key);

        Assert.Equal(
            [NotificationDeliveryStatus.Sent, NotificationDeliveryStatus.Sent],
            deliveries.Select(d => d.Status));
        Assert.Equal(2, teams.Channels.Count(id => id == channelId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Вимкнений_канал_не_отримує_нічого_попри_ввімкнене_правило()
    {
        // ⛔ «Вимкнути канал» має означати саме це, а не «вимкнути кожне
        // правило окремо»: забутий перемикач слав би повідомлення в канал,
        // який вважають прибраним.
        var channelId = await AddChannelAsync("off", NotificationChannelKind.TeamsWebhook, enabled: false);
        var teams = new FakeSender(NotificationChannelKind.TeamsWebhook);
        var key = $"consistency:{_tag}";

        await using var db = Context();
        await Dispatcher(db, new TestClock(Start), teams).DispatchAsync(Event(key), CancellationToken.None);

        Assert.DoesNotContain(channelId, teams.Channels);
        Assert.Empty(await DeliveriesAsync(channelId, key));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-34")]
    public async Task Канал_Smtp_доставляє_листа_адресатам_каналу_а_відмова_транспорту_лишає_Failed()
    {
        // Канал, чий адресат приймає, і канал, чий адресат відбиває: обидва
        // йдуть ОДНИМ прогоном, тобто перевіряється й те, що відмова другого
        // не з'їдає доставку першого.
        var deliveredId = await AddChannelAsync(
            "smtp-ok", NotificationChannelKind.Smtp, settingsJson: Mailbox("ops@corp.example", "ECR NCOC"))
            .ConfigureAwait(true);
        var refusedId = await AddChannelAsync(
            "smtp-bad", NotificationChannelKind.Smtp, settingsJson: Mailbox("dead@corp.example", title: null))
            .ConfigureAwait(true);

        var transport = new FakeTransport(refuses: "dead@corp.example");
        var key = $"consistency:{_tag}";

        await using var db = Context();

        // Жодного винятку назовні: відмова релея — це рядок журналу.
        await Dispatcher(db, new TestClock(Start), new SmtpChannelSender(transport))
            .DispatchAsync(Event(key), CancellationToken.None);

        var sent = Assert.Single(await DeliveriesAsync(deliveredId, key));
        Assert.Equal(NotificationDeliveryStatus.Sent, sent.Status);
        Assert.Null(sent.Error);

        var failed = Assert.Single(await DeliveriesAsync(refusedId, key));
        Assert.Equal(NotificationDeliveryStatus.Failed, failed.Status);
        Assert.Contains("550", failed.Error!, StringComparison.Ordinal);

        // ⛔ Адресат і тема беруться з КАНАЛУ, а не з конфігурації процесу:
        // саме це відрізняє відправника каналу від транспорту під ним.
        var letter = Assert.Single(transport.Delivered);
        Assert.Equal(["ops@corp.example"], letter.Recipients);
        Assert.Equal("ECR NCOC: ECR: розбіжності", letter.Subject);
        Assert.Equal("[consistency] 3 розбіжності.", letter.Body);
    }

    /// <summary>Несекретні параметри SMTP-каналу: адресат і заголовок.</summary>
    private static string Mailbox(string recipient, string? title)
        => title is null
            ? $$"""{"host":"mail.corp.example","port":25,"recipients":["{{recipient}}"]}"""
            : $$"""{"host":"mail.corp.example","port":25,"recipients":["{{recipient}}"],"title":"{{title}}"}""";

    private static NotificationEvent Event(string key)
        => new(Kind, NotificationSeverity.Error, key, "ECR: розбіжності", "[consistency] 3 розбіжності.");

    private static NotificationDispatcher Dispatcher(
        EcrDbContext db, IClock clock, params INotificationChannelSender[] senders)
        => new(new NotificationDispatchStore(db, new MemoryCache(new MemoryCacheOptions())), senders, clock);

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    /// <summary>Заводить канал із увімкненим правилом на <see cref="Kind"/>.</summary>
    /// <param name="suffix">Частина назви, унікальна в межах тесту.</param>
    /// <param name="kind">Транспорт.</param>
    /// <param name="enabled">Чи ввімкнений сам канал.</param>
    /// <param name="settingsJson">Несекретні параметри каналу.</param>
    private async Task<int> AddChannelAsync(
        string suffix, NotificationChannelKind kind, bool enabled = true, string settingsJson = "{}")
    {
        await using var db = Context();

        var channel = new NotificationChannel(kind, $"be34-{_tag}-{suffix}", settingsJson, Start, byUserId: null);

        if (!enabled)
        {
            channel.Update(channel.Name, channel.SettingsJson, isEnabled: false, Start, byUserId: null);
        }

        db.NotificationChannels.Add(channel);
        await db.SaveChangesAsync();

        db.NotificationRules.Add(new NotificationRule(Kind, channel.Id, NotificationSeverity.Warning));
        await db.SaveChangesAsync();

        return channel.Id;
    }

    private async Task<List<NotificationDelivery>> DeliveriesAsync(int channelId, string eventKey)
    {
        await using var db = Context();

        return await db.NotificationDeliveries
            .AsNoTracking()
            .Where(d => d.ChannelId == channelId && d.EventKey == eventKey)
            .OrderBy(d => d.Id)
            .ToListAsync();
    }

    /// <summary>Транспорт процесу: приймає листа або відбиває його за адресатом.</summary>
    /// <param name="refuses">Адресат, на якому релей відповідає відмовою.</param>
    private sealed class FakeTransport(string refuses) : INotificationSender
    {
        public List<(IReadOnlyList<string> Recipients, string Subject, string Body)> Delivered { get; } = [];

        public bool IsConfigured => true;

        public Task SendAsync(
            IReadOnlyList<string> recipients, string subject, string body, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(recipients);

            if (recipients.Contains(refuses, StringComparer.Ordinal))
            {
                return Task.FromException(
                    new InvalidOperationException("Relay refused the recipient: 550 5.1.1."));
            }

            Delivered.Add((recipients, subject, body));

            return Task.CompletedTask;
        }
    }

    /// <summary>Відправник, який нічого не шле, а лише запам'ятовує — або падає.</summary>
    private sealed class FakeSender(NotificationChannelKind kind, bool fails = false) : INotificationChannelSender
    {
        public List<int> Channels { get; } = [];

        public NotificationChannelKind Kind => kind;

        public Task SendAsync(NotificationChannel channel, NotificationMessage message, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(channel);

            Channels.Add(channel.Id);

            return fails
                ? Task.FromException(new InvalidOperationException("Канал «boom»: вебхук відповів 500."))
                : Task.CompletedTask;
        }
    }
}
