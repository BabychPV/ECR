// tests/Ecr.Application.Tests/Notifications/NotificationChannelHandlersTests.cs
using System.Text;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Notifications;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Notifications;
using Ecr.TestKit;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Ecr.Application.Tests.Notifications;

/// <summary>Канали сповіщень (<c>BE-33</c>): SSRF-перелік, секрет write-only, журнал безпеки, проба.</summary>
public sealed class NotificationChannelHandlersTests
{
    private const int Actor = 7;
    private const string Webhook = "https://prod-17.westeurope.logic.azure.com/workflows/abc?sig=TopSecretSig";

    private static readonly NotificationChannelSettings Smtp =
        new(Host: "mail.corp.example", Port: 25, Recipients: ["ops@corp.example"]);

    private readonly FakeNotificationStore _store = new();
    private readonly List<SecurityEventRecord> _events = [];
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly INotificationSender _sender = Substitute.For<INotificationSender>();

    public NotificationChannelHandlersTests()
    {
        _clock.UtcNow.Returns(new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc));
        _user.UserId.Returns(Actor);
        _audit.WriteSecurityEventAsync(Arg.Do<SecurityEventRecord>(_events.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        Allow("System.ManageNotifications");
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-33")]
    [InlineData("http://prod-17.westeurope.logic.azure.com/workflows/abc")] // не https
    [InlineData("https://evil.example/workflows/abc")] // хост поза переліком
    [InlineData("https://evillogic.azure.com/x")] // суфікс без межі домену
    [InlineData("https://logic.azure.com.evil.example/x")] // суфікс не в кінці
    [InlineData("https://10.0.0.5/hook")] // IP-літерал
    [InlineData("https://[::1]/hook")]
    [InlineData("https://localhost/hook")]
    [InlineData("not a url")]
    public async Task Адреса_вебхука_поза_переліком_хостів_відхиляється_422_і_секрет_не_зберігається(string url)
    {
        var teams = await Save().CreateAsync(NotificationChannelKind.TeamsWebhook, "Teams", null, CancellationToken.None);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Secret().HandleAsync(teams.Id, url, CancellationToken.None));

        Assert.Equal("ECR-REQ-0422", error.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.webhookUrlNotAllowed", error.Details!["messageKey"]);
        Assert.False(_store.Channels.Single().HasSecret);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-33")]
    public async Task Порожній_перелік_суфіксів_не_приймає_жодного_вебхука_а_пароль_SMTP_перелік_не_обмежує()
    {
        var teams = await Save().CreateAsync(NotificationChannelKind.TeamsWebhook, "Teams", null, CancellationToken.None);
        var smtp = await Save().CreateAsync(NotificationChannelKind.Smtp, "Mail", Smtp, CancellationToken.None);

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Secret(suffixes: "").HandleAsync(teams.Id, Webhook, CancellationToken.None));

        var view = await Secret(suffixes: "").HandleAsync(smtp.Id, "p@ssw0rd", CancellationToken.None);
        Assert.True(view.HasSecret);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-33")]
    public async Task Секрет_зберігається_захищеним_і_не_потрапляє_ні_у_відповідь_ні_в_журнал_безпеки()
    {
        var teams = await Save().CreateAsync(NotificationChannelKind.TeamsWebhook, "Teams", null, CancellationToken.None);

        var view = await Secret().HandleAsync(teams.Id, Webhook, CancellationToken.None);

        Assert.True(view.HasSecret);
        Assert.Equal(FakeProtector.Mark + Webhook, Encoding.UTF8.GetString(_store.Channels.Single().SecretProtected!));

        var replaced = Assert.Single(_events, e => e.EventType == "NotificationChannelSecretReplaced");
        Assert.Equal(Actor, replaced.ChangedByUserId);
        Assert.All(_events, e => Assert.DoesNotContain("TopSecretSig", e.DetailsJson, StringComparison.Ordinal));
        Assert.DoesNotContain("TopSecretSig", System.Text.Json.JsonSerializer.Serialize(view), StringComparison.Ordinal);

        var cleared = await Secret().HandleAsync(teams.Id, " ", CancellationToken.None);
        Assert.False(cleared.HasSecret);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-33")]
    public async Task Зміни_каналу_йдуть_у_журнал_безпеки_а_зайнята_назва_й_неповний_SMTP_дають_422()
    {
        var mail = await Save().CreateAsync(NotificationChannelKind.Smtp, " Mail ", Smtp, CancellationToken.None);
        Assert.Equal("Mail", mail.Name);
        Assert.Equal("mail.corp.example", mail.Settings.Host);

        var taken = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().CreateAsync(NotificationChannelKind.Smtp, "Mail", Smtp, CancellationToken.None));
        var incomplete = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().CreateAsync(NotificationChannelKind.Smtp, "Other", Smtp with { Recipients = [] }, CancellationToken.None));

        Assert.Equal("err.ECR-REQ-0422.notificationChannelNameTaken", taken.Details!["messageKey"]);
        Assert.Equal("err.ECR-REQ-0422.notificationChannelInvalid", incomplete.Details!["messageKey"]);

        // Власна назва при зміні — не «зайнята».
        var updated = await Save().UpdateAsync(mail.Id, "Mail", isEnabled: false, Smtp, CancellationToken.None);
        Assert.False(updated.IsEnabled);

        _store.RuleCount = 3;
        await Delete().HandleAsync(mail.Id, CancellationToken.None);
        Assert.Empty(_store.Channels);

        Assert.Equal(
            ["NotificationChannelCreated", "NotificationChannelUpdated", "NotificationChannelDeleted"],
            _events.Select(e => e.EventType));
        Assert.Contains("\"removedRules\":3", _events[^1].DetailsJson, StringComparison.Ordinal);

        var missing = await Assert.ThrowsAsync<NotFoundException>(() => Delete().HandleAsync(mail.Id, CancellationToken.None));
        Assert.Equal("err.ECR-INT-0404.notificationChannel", missing.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-33")]
    public async Task Проба_SMTP_іде_адресатам_каналу_а_відмова_транспорту_це_відповідь_а_не_помилка_запиту()
    {
        var mail = await Save().CreateAsync(NotificationChannelKind.Smtp, "Mail", Smtp, CancellationToken.None);

        var unconfigured = await Test().HandleAsync(mail.Id, CancellationToken.None);
        Assert.Equal((false, "notifications.test.smtpNotConfigured"), (unconfigured.Ok, unconfigured.MessageKey));

        _sender.IsConfigured.Returns(true);
        Assert.True((await Test().HandleAsync(mail.Id, CancellationToken.None)).Ok);
        await _sender.Received(1).SendAsync(
            Arg.Is<IReadOnlyList<string>>(r => r.Single() == "ops@corp.example"),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        _sender.SendAsync(default!, default!, default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("relay refused"));
        var refused = await Test().HandleAsync(mail.Id, CancellationToken.None);
        Assert.Equal((false, "relay refused"), (refused.Ok, refused.Error));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-34")]
    public async Task Проба_Teams_іде_відправником_каналу_а_без_відправника_каже_це_ключем()
    {
        var teams = await Save().CreateAsync(NotificationChannelKind.TeamsWebhook, "Teams", null, CancellationToken.None);

        // ⛔ Відправника в переліку немає — те саме, що рядок `Failed` у журналі
        // доставок: канал увімкнений, а доставити його нічим.
        var orphan = await Test().HandleAsync(teams.Id, CancellationToken.None);
        Assert.Equal((false, "notifications.test.senderNotRegistered"), (orphan.Ok, orphan.MessageKey));

        var webhook = new SpyChannelSender(NotificationChannelKind.TeamsWebhook);
        var probe = await Test(webhook).HandleAsync(teams.Id, CancellationToken.None);

        Assert.True(probe.Ok);
        Assert.Null(probe.Error);
        var (sent, message) = Assert.Single(webhook.Calls);
        Assert.Equal(teams.Id, sent.Id);
        Assert.Contains("Teams", message.Body, StringComparison.Ordinal);

        // ⛔ Транспорт процесу до Teams не має стосунку: проба, яка тихо пішла
        // поштою, зеленіла б там, де вебхук не працює.
        await _sender.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default!, default);

        // Відмова транспорту — це ВІДПОВІДЬ проби, а не помилка запиту.
        webhook.Fails = new InvalidOperationException("Канал «Teams»: вебхук відповів 500.");
        var failed = await Test(webhook).HandleAsync(teams.Id, CancellationToken.None);
        Assert.Equal((false, "Канал «Teams»: вебхук відповів 500."), (failed.Ok, failed.Error));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-33")]
    public async Task Без_System_ManageNotifications_жодна_дія_не_виконується()
    {
        var mail = await Save().CreateAsync(NotificationChannelKind.Smtp, "Mail", Smtp, CancellationToken.None);
        Allow("System.ViewHealth");

        await Assert.ThrowsAsync<AccessDeniedException>(() => new ListNotificationChannelsHandler(_store, _access, _user).HandleAsync(CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Save().CreateAsync(NotificationChannelKind.Smtp, "X", Smtp, CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Save().UpdateAsync(mail.Id, "X", true, Smtp, CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Delete().HandleAsync(mail.Id, CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Secret().HandleAsync(mail.Id, "p", CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Test().HandleAsync(mail.Id, CancellationToken.None));

        Assert.Equal("Mail", _store.Channels.Single().Name);
        Assert.False(_store.Channels.Single().HasSecret);
    }

    private void Allow(string permission)
        => _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }.Permission(permission).Build());

    private SaveNotificationChannelHandler Save() => new(_store, _access, _uow, _audit, _user, _clock);

    private DeleteNotificationChannelHandler Delete() => new(_store, _access, _uow, _audit, _user, _clock);

    private TestNotificationChannelHandler Test(params INotificationChannelSender[] channelSenders)
        => new(_store, _sender, channelSenders, _access, _user);

    /// <summary>Відправник каналу, який нічого не шле — лише запам'ятовує або падає.</summary>
    private sealed class SpyChannelSender(NotificationChannelKind kind) : INotificationChannelSender
    {
        public List<(NotificationChannel Channel, NotificationMessage Message)> Calls { get; } = [];

        public Exception? Fails { get; set; }

        public NotificationChannelKind Kind => kind;

        public Task SendAsync(NotificationChannel channel, NotificationMessage message, CancellationToken ct)
        {
            Calls.Add((channel, message));

            return Fails is null ? Task.CompletedTask : Task.FromException(Fails);
        }
    }

    // ⚠ Суфікси — літералом, а не з appsettings: тест тримає ПРАВИЛО, не дефолт.
    private ReplaceNotificationChannelSecretHandler Secret(string suffixes = ".logic.azure.com;webhook.office.com")
        => new(_store, new FakeProtector(), new WebhookUrlPolicy(suffixes.Split(';')), _access, _uow, _audit, _user, _clock);

    private sealed class FakeProtector : INotificationSecretProtector
    {
        public const string Mark = "protected:";

        public byte[] Protect(string secret) => Encoding.UTF8.GetBytes(Mark + secret);
    }
}
