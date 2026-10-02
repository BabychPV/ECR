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

    private static readonly NotificationChannelSettingsInput Smtp =
        new(Recipients: ["ops@corp.example"]);

    private readonly FakeNotificationStore _store = new();
    private readonly List<SecurityEventRecord> _events = [];
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly TransactionProbe _tx;
    private readonly INotificationSender _sender = Substitute.For<INotificationSender>();

    public NotificationChannelHandlersTests()
    {
        _tx = TransactionProbe.Attach(_uow, _audit);
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
        Assert.Equal(["ops@corp.example"], mail.Settings.Recipients);

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

    /// <summary>
    /// Транспорт SMTP задає застосунок: канал його не приймає, але й не
    /// ламається об те, що вже збережено.
    /// </summary>
    /// <remarks>
    /// ⛔ Обидва боки в одному випадку навмисно. Сама відмова <c>422</c> нічого
    /// не доводить, поки не показано, що канали, записані ДО зміни, лишилися
    /// читабельними: «прибрали поле з контракту» і «зламали наявні рядки» —
    /// різні наслідки одного коміту.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-33")]
    public async Task Поля_транспорту_дають_422_а_канал_зі_старим_SettingsJson_читається_і_працює()
    {
        const string legacy = """
            {"host":"mail.corp.example","port":25,"useTls":true,"from":"ecr@corp.example","recipients":["ops@corp.example"],"title":"ECR"}
            """;
        _store.AddChannel(new NotificationChannel(
            NotificationChannelKind.Smtp, "Legacy", legacy, _clock.UtcNow, Actor));

        var listed = Assert.Single(
            await List().HandleAsync(CancellationToken.None));

        // Читається: адресати й підпис на місці, транспорту у видачі немає, а
        // екран має чим пояснити порожнє місце там, де колись було поле.
        Assert.Equal(["ops@corp.example"], listed.Settings.Recipients);
        Assert.Equal("ECR", listed.Settings.Title);
        Assert.True(listed.TransportFromConfiguration);
        Assert.DoesNotContain(
            "mail.corp.example", System.Text.Json.JsonSerializer.Serialize(listed), StringComparison.Ordinal);

        // Повторне збереження БЕЗ транспорту проходить і прибирає старі поля.
        var saved = await Save().UpdateAsync(listed.Id, "Legacy", isEnabled: true, Smtp, CancellationToken.None);
        Assert.Equal(["ops@corp.example"], saved.Settings.Recipients);
        Assert.DoesNotContain("mail.corp.example", _store.Channels.Single().SettingsJson, StringComparison.Ordinal);

        // А з транспортом — 422 названим ключем, і збережене не змінилося.
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().UpdateAsync(
                listed.Id, "Legacy", true, Smtp with { Host = "mail.corp.example", Port = 25 },
                CancellationToken.None));

        Assert.Equal("ECR-REQ-0422", refused.ErrorCode);
        Assert.Equal(
            "err.ECR-REQ-0422.notificationChannelTransportFromConfiguration", refused.Details!["messageKey"]);
        Assert.DoesNotContain("mail.corp.example", _store.Channels.Single().SettingsJson, StringComparison.Ordinal);

        // Кожне поле транспорту — окремо: спільна умова, яка ловить лише `host`,
        // лишила б три інші дороги до того самого нездійсненного налаштування.
        NotificationChannelSettingsInput[] transports =
        [
            Smtp with { Host = "mail.corp.example" }, Smtp with { Port = 25 },
            Smtp with { UseTls = true }, Smtp with { From = "ecr@corp.example" },
        ];

        foreach (var one in transports)
        {
            var each = await Assert.ThrowsAsync<BusinessRuleException>(
                () => Save().CreateAsync(NotificationChannelKind.Smtp, "Other", one, CancellationToken.None));
            Assert.Equal(
                "err.ECR-REQ-0422.notificationChannelTransportFromConfiguration", each.Details!["messageKey"]);
        }
    }

    /// <summary>
    /// Свіже встановлення без <c>Smtp:Host</c>: поштовий канал є, а доставляти
    /// його нічим — і кожна відповідь каналу мусить це визнати.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-33")]
    public async Task Поштовий_канал_без_транспорту_процесу_не_названо_налаштованим()
    {
        var created = await Save().CreateAsync(NotificationChannelKind.Smtp, "Mail", Smtp, CancellationToken.None);
        var updated = await Save().UpdateAsync(created.Id, "Mail", true, Smtp, CancellationToken.None);
        var listed = Assert.Single(await List().HandleAsync(CancellationToken.None));

        Assert.Equal([false, false, false], new[] { created, updated, listed }.Select(v => v.TransportConfigured));

        // `transportFromConfiguration` лишився тим, чим був: «транспорт із налаштувань».
        Assert.True(listed.TransportFromConfiguration);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-33")]
    public async Task Поштовий_канал_налаштований_коли_налаштований_транспорт_процесу()
    {
        _sender.IsConfigured.Returns(true);

        var created = await Save().CreateAsync(NotificationChannelKind.Smtp, "Mail", Smtp, CancellationToken.None);
        var listed = Assert.Single(await List().HandleAsync(CancellationToken.None));

        Assert.True(created.TransportConfigured);
        Assert.True(listed.TransportConfigured);
    }

    /// <summary>Для Teams «налаштовано» — це «задано адресу вебхука», і транспорт пошти тут ні до чого.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-34")]
    public async Task Канал_Teams_налаштований_рівно_тоді_коли_задано_адресу_вебхука()
    {
        _sender.IsConfigured.Returns(true);
        var teams = await Save().CreateAsync(NotificationChannelKind.TeamsWebhook, "Teams", null, CancellationToken.None);
        Assert.False(teams.TransportConfigured);

        _sender.IsConfigured.Returns(false);
        var withUrl = await Secret().HandleAsync(teams.Id, Webhook, CancellationToken.None);
        Assert.True(withUrl.TransportConfigured);
        Assert.True(Assert.Single(await List().HandleAsync(CancellationToken.None)).TransportConfigured);

        var cleared = await Secret().HandleAsync(teams.Id, " ", CancellationToken.None);
        Assert.False(cleared.TransportConfigured);
    }

    /// <summary>Адресати каналу — саме адреси, а не будь-який непорожній рядок.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-33")]
    public async Task Порожній_перелік_адресатів_і_рядок_що_не_є_адресою_дають_різні_422()
    {
        var empty = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().CreateAsync(
                NotificationChannelKind.Smtp, "Mail", Smtp with { Recipients = [] }, CancellationToken.None));
        var garbage = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().CreateAsync(
                NotificationChannelKind.Smtp, "Mail", Smtp with { Recipients = ["ops(at)corp.example"] },
                CancellationToken.None));
        var blank = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().CreateAsync(
                NotificationChannelKind.Smtp, "Mail", Smtp with { Recipients = ["ops@corp.example", "   "] },
                CancellationToken.None));

        // ⚠ Ключі РІЗНІ: «адресатів немає» і «ось цей рядок не адреса» ведуть до
        // різних дій користувача, і один ключ на двох сказав би не те.
        Assert.Equal("err.ECR-REQ-0422.notificationChannelInvalid", empty.Details!["messageKey"]);
        Assert.Equal("err.ECR-REQ-0422.notificationChannelRecipientInvalid", garbage.Details!["messageKey"]);
        Assert.Equal("err.ECR-REQ-0422.notificationChannelRecipientInvalid", blank.Details!["messageKey"]);
        Assert.Empty(_store.Channels);

        // Адреса з підписом — теж адреса: звуження до «щось@щось» відхиляло б
        // чинні значення, і перевірка почала б заважати замість допомагати.
        var ok = await Save().CreateAsync(
            NotificationChannelKind.Smtp, "Mail", Smtp with { Recipients = ["Ops <ops@corp.example>"] },
            CancellationToken.None);
        Assert.Equal(["Ops <ops@corp.example>"], ok.Settings.Recipients);
    }

    /// <summary>Рев'ю ent6 S3: межа явних адрес, суворіша перевірка адреси, довжина заголовка — лише на збереженні.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ent6-S3")]
    public async Task Збереження_каналу_обмежує_кількість_явних_адрес_відкидає_доменні_літерали_і_довгий_заголовок()
    {
        string[] Many(int n) => [.. Enumerable.Range(0, n).Select(i => $"u{i}@corp.example")];

        // Мутація: прибрати перевірку `MaxExplicitRecipients` → 51 адреса проходить, рядок червоніє.
        var tooMany = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().CreateAsync(
                NotificationChannelKind.Smtp, "Mail", Smtp with { Recipients = Many(NotificationChannel.MaxExplicitRecipients + 1) },
                CancellationToken.None));
        Assert.Equal("ECR-REQ-0422", tooMany.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.channelTooManyRecipients", tooMany.Details!["messageKey"]);

        // Мутація: повернути `MailAddress.TryCreate` замість `SmtpSettings.IsValidAddress` → літерал проходить.
        var literal = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().CreateAsync(
                NotificationChannelKind.Smtp, "Mail", Smtp with { Recipients = ["a@[10.0.0.1]"] }, CancellationToken.None));
        Assert.Equal("err.ECR-REQ-0422.notificationChannelRecipientInvalid", literal.Details!["messageKey"]);

        var longTitle = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().CreateAsync(
                NotificationChannelKind.Smtp, "Mail",
                Smtp with { Title = new string('x', NotificationChannel.TitleMaxLength + 1) }, CancellationToken.None));
        Assert.Equal("err.ECR-REQ-0422.notificationChannelInvalid", longTitle.Details!["messageKey"]);
        Assert.Empty(_store.Channels);

        // Рівно межа — приймається.
        var ok = await Save().CreateAsync(
            NotificationChannelKind.Smtp, "Mail", Smtp with { Recipients = Many(NotificationChannel.MaxExplicitRecipients) },
            CancellationToken.None);
        Assert.Equal(NotificationChannel.MaxExplicitRecipients, ok.Settings.Recipients!.Count);
    }

    /// <summary>Рев'ю ent6 S3: legacy-канал із понад межею адрес читається, а проба шле не більше ліміту проби.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ent6-S3")]
    public async Task Проба_legacy_каналу_з_понад_межею_адрес_обрізає_їх_до_ліміту_проби()
    {
        var mail = await Save().CreateAsync(NotificationChannelKind.Smtp, "Mail", Smtp, CancellationToken.None);
        var legacy = Enumerable.Range(0, 80).Select(i => $"\"u{i}@corp.example\"");
        _store.Channels.Single().Update(
            "Mail", $"{{\"recipients\":[{string.Join(',', legacy)}]}}", true, DateTime.UtcNow, null);
        _sender.IsConfigured.Returns(true);

        // Мутація: прибрати `.Take(ProbeRecipientLimit)` у `ProbeAsync` → піде 80 адрес.
        Assert.True((await Test().HandleAsync(mail.Id, CancellationToken.None)).Ok);
        await _sender.Received(1).SendAsync(
            Arg.Is<IReadOnlyList<string>>(r => r.Count == TestNotificationChannelHandler.ProbeRecipientLimit),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        // ⛔ S4: нерозпізнана відмова пошти — та сама єдина категорія недосяжності, що й connect/timeout.
        Assert.Equal((false, null, SmtpFailureClassifier.ProbeFailed), (refused.Ok, refused.Error, refused.MessageKey));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public async Task Проба_SMTP_іде_відправником_каналу_що_розгортає_ролі_з_межею_а_порожній_розклад_має_ключ()
    {
        var mail = await Save().CreateAsync(NotificationChannelKind.Smtp, "Mail", Smtp, CancellationToken.None);
        _sender.IsConfigured.Returns(true);

        var smtp = new SpyChannelSender(NotificationChannelKind.Smtp);
        Assert.True((await Test(smtp).HandleAsync(mail.Id, CancellationToken.None)).Ok);

        // ⛔ Мутація: повернути шлях лише з явними адресами (`sender.SendAsync`) → Calls порожній, рядок червоний.
        var call = Assert.Single(smtp.Calls);
        Assert.Equal(mail.Id, call.Channel.Id);
        Assert.Equal(TestNotificationChannelHandler.ProbeRecipientLimit, call.Message.RecipientLimit);
        await _sender.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default!, default);

        // Ролі не розкрилися → названий ключ, а не загальна відмова.
        smtp.Fails = new NotificationNoRecipientsException("no active role users");
        var none = await Test(smtp).HandleAsync(mail.Id, CancellationToken.None);
        Assert.Equal((false, "notifications.test.smtpNoRecipients"), (none.Ok, none.MessageKey));
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
        // ⛔ Текст винятку транспорту (хост/URL цілі) назовні не йде — лише ключ категорії.
        // Мутація: повернути `e.Message` у `TryAsync` для не-пошти → Error не null, тест червоніє.
        webhook.Fails = new HttpRequestException("Connection refused (hooks.example.test:443) /secret-token-123");
        var failed = await Test(webhook).HandleAsync(teams.Id, CancellationToken.None);
        Assert.Equal((false, null, "notifications.testFailed"), (failed.Ok, failed.Error, failed.MessageKey));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-33")]
    public async Task Проба_каналу_лишає_запис_у_журналі_безпеки_без_секрету_й_без_тіла_повідомлення()
    {
        var teams = await Save().CreateAsync(NotificationChannelKind.TeamsWebhook, "Teams", null, CancellationToken.None);
        await Secret().HandleAsync(teams.Id, Webhook, CancellationToken.None);
        _events.Clear();

        var webhook = new SpyChannelSender(NotificationChannelKind.TeamsWebhook);
        Assert.True((await Test(webhook).HandleAsync(teams.Id, CancellationToken.None)).Ok);

        // ⛔ Проба шле повідомлення НАЗОВНІ від імені системи — решта дій над
        // каналом у журналі є, і мовчазна проба лишала б у ньому дірку.
        var probe = Assert.Single(_events, e => e.EventType == "NotificationChannelTested");
        Assert.Equal(Actor, probe.ChangedByUserId);
        Assert.Contains("\"ok\":true", probe.DetailsJson, StringComparison.Ordinal);

        // ⛔ Ні секрету каналу, ні тіла пробного повідомлення в журналі немає.
        Assert.DoesNotContain("TopSecretSig", probe.DetailsJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("Test message for channel", probe.DetailsJson!, StringComparison.Ordinal);

        // Невдала проба — така сама подія: журнал, який пише лише успіхи,
        // не відповідає на питання «хто смикав канал».
        webhook.Fails = new InvalidOperationException("вебхук відповів 500");
        await Test(webhook).HandleAsync(teams.Id, CancellationToken.None);

        Assert.Equal(
            [true, false],
            _events.Where(e => e.EventType == "NotificationChannelTested")
                .Select(e => e.DetailsJson!.Contains("\"ok\":true", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-33")]
    public async Task Без_System_ManageNotifications_жодна_дія_не_виконується()
    {
        var mail = await Save().CreateAsync(NotificationChannelKind.Smtp, "Mail", Smtp, CancellationToken.None);
        Allow("System.ViewHealth");

        await Assert.ThrowsAsync<AccessDeniedException>(() => List().HandleAsync(CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Save().CreateAsync(NotificationChannelKind.Smtp, "X", Smtp, CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Save().UpdateAsync(mail.Id, "X", true, Smtp, CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Delete().HandleAsync(mail.Id, CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Secret().HandleAsync(mail.Id, "p", CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Test().HandleAsync(mail.Id, CancellationToken.None));

        Assert.Equal("Mail", _store.Channels.Single().Name);
        Assert.False(_store.Channels.Single().HasSecret);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public async Task Канал_може_мати_адресатів_лише_за_ролями_невідома_роль_і_Teams_із_ролями_дають_422_а_PUT_без_ролей_їх_знімає()
    {
        // Лише ролі, без явних адрес — це вже адресат.
        var created = await Save().CreateAsync(
            NotificationChannelKind.Smtp, "By role", new NotificationChannelSettingsInput(RecipientRoleIds: [1, 2, 2]), CancellationToken.None);
        Assert.Equal([1, 2], created.Settings.RecipientRoleIds);
        Assert.Equal([1, 2], (await List().HandleAsync(CancellationToken.None)).Single().Settings.RecipientRoleIds);

        // ⛔ Ролі не потрапляють у SettingsJson: вони живуть у sys_ecr.NotificationChannelRole.
        Assert.DoesNotContain("recipientRoleIds", _store.Channels.Single().SettingsJson, StringComparison.OrdinalIgnoreCase);

        var unknown = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().CreateAsync(NotificationChannelKind.Smtp, "Bad", new NotificationChannelSettingsInput(RecipientRoleIds: [99]), CancellationToken.None));
        var teams = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().CreateAsync(NotificationChannelKind.TeamsWebhook, "T", new NotificationChannelSettingsInput(RecipientRoleIds: [1]), CancellationToken.None));
        Assert.Equal("err.ECR-REQ-0422.notificationChannelRoleInvalid", unknown.Details!["messageKey"]);
        Assert.Equal("err.ECR-REQ-0422.notificationChannelRoleInvalid", teams.Details!["messageKey"]);

        // Без ролей і без адрес — як і раніше 422.
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().UpdateAsync(created.Id, "By role", true, new NotificationChannelSettingsInput(Recipients: []), CancellationToken.None));

        var updated = await Save().UpdateAsync(
            created.Id, "By role", true, new NotificationChannelSettingsInput(Recipients: ["a@b.example"]), CancellationToken.None);
        Assert.Empty(updated.Settings.RecipientRoleIds!);
    }

    private void Allow(string permission)
        => _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }.Permission(permission).Build());

    private ListNotificationChannelsHandler List() => new(_store, _sender, _access, _user);

    /// <summary>
    /// Проба пошти називає категорію відмови ключем каталогу (адміністратор бачить, ЩО лагодити),
    /// а нерозпізнану — загальним ключем <c>notifications.testFailed</c>; текст транспорту назовні не йде.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-12.4a")]
    public async Task Проба_SMTP_віддає_категорію_відмови_ключем_а_невідому_загальним_ключем_без_тексту()
    {
        _sender.IsConfigured.Returns(true);
        var channel = await Save().CreateAsync(NotificationChannelKind.Smtp, "Mail", Smtp, CancellationToken.None);

        _sender
            .SendAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new System.Net.Mail.SmtpException(System.Net.Mail.SmtpStatusCode.ClientNotPermitted, "535 5.7.8 rejected"));

        var auth = await Test().HandleAsync(channel.Id, CancellationToken.None);

        Assert.False(auth.Ok);
        Assert.Equal(SmtpFailureClassifier.Auth, auth.MessageKey);

        _sender
            .SendAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("something odd"));

        var unknown = await Test().HandleAsync(channel.Id, CancellationToken.None);

        Assert.False(unknown.Ok);
        // ⛔ Мутація: повернути `e.Message` у `TryAsync` для пошти → тут і у відповіді про `auth` червоніє (текст транспорту — назовні).
        Assert.Equal(SmtpFailureClassifier.ProbeFailed, unknown.MessageKey);
        Assert.Null(unknown.Error);
        Assert.Null(auth.Error);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public async Task Проба_через_відправника_каналу_класифікує_відмову_SMTP_а_Teams_дає_загальний_ключ_без_поштової_категорії()
    {
        var mail = await Save().CreateAsync(NotificationChannelKind.Smtp, "Mail", Smtp, CancellationToken.None);
        var teams = await Save().CreateAsync(NotificationChannelKind.TeamsWebhook, "Teams", null, CancellationToken.None);
        _sender.IsConfigured.Returns(true);

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: `classify: true` → `false` у гілці відправника каналу (ProbeAsync) — назовні
        // пішов би банер поштового сервера, а ключа категорії не було б.
        var smtp = new SpyChannelSender(NotificationChannelKind.Smtp)
        {
            Fails = new System.Net.Mail.SmtpException(
                System.Net.Mail.SmtpStatusCode.ClientNotPermitted, "535 5.7.8 relay.corp.internal rejected"),
        };
        var refused = await Test(smtp).HandleAsync(mail.Id, CancellationToken.None);
        Assert.Equal((false, null, SmtpFailureClassifier.Auth), (refused.Ok, refused.Error, refused.MessageKey));

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: `classify ? MessageKeyOf(e) : null` → завжди MessageKeyOf — Teams отримав би
        // поштовий ключ «notifications.test.smtp.unknown» замість загального (тексту немає — ent5 P3-3).
        var webhook = new SpyChannelSender(NotificationChannelKind.TeamsWebhook)
        {
            Fails = new InvalidOperationException("Канал «Teams»: вебхук відповів 500."),
        };
        var failed = await Test(webhook).HandleAsync(teams.Id, CancellationToken.None);
        Assert.Equal((false, (string?)null, "notifications.testFailed"), (failed.Ok, failed.Error, failed.MessageKey));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-33")]
    public async Task Назва_рівно_100_символів_приймається_заголовок_обрізається_а_канал_лише_з_ролями_не_пише_порожнього_списку_адрес()
    {
        // ⚠ МУТАЦІЙНИЙ ДОКАЗ (SaveNotificationChannelHandler): стеля назви `>` → `>=` відхилила б рівно 100;
        // `Title` → завжди null загубив би заголовок; `recipients.Count == 0 ? null : recipients` → завжди
        // `recipients` записав би «recipients: []» каналу, що адресує лише ролями.
        var name = new string('N', 100);
        var created = await Save().CreateAsync(
            NotificationChannelKind.Smtp, name,
            new NotificationChannelSettingsInput(Title: "  Ops alerts  ", RecipientRoleIds: [1]), CancellationToken.None);

        Assert.Equal(name, created.Name);
        Assert.Equal("Ops alerts", created.Settings.Title);
        Assert.DoesNotContain("[]", _store.Channels.Single().SettingsJson, StringComparison.Ordinal);

        var blank = await Save().UpdateAsync(
            created.Id, name, true, new NotificationChannelSettingsInput(Recipients: ["a@b.example"], Title: "   "), CancellationToken.None);
        Assert.Null(blank.Settings.Title);
        Assert.Equal(["a@b.example"], blank.Settings.Recipients!);

        var tooLong = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().CreateAsync(NotificationChannelKind.Smtp, name + "N", Smtp, CancellationToken.None));
        Assert.Equal("err.ECR-REQ-0422.notificationChannelInvalid", tooLong.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public async Task Кожна_зміна_каналу_зберігається_а_ролі_правки_доходять_до_сховища()
    {
        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: прибрати будь-який `uow.SaveChangesAsync` у створенні (два: канал і ролі),
        // правці, секреті чи видаленні, або `store.ReplaceChannelRolesAsync` у правці — відповідь лишалася б
        // правильною, а база ні.
        var mail = await Save().CreateAsync(NotificationChannelKind.Smtp, "Mail", Smtp, CancellationToken.None);
        Assert.Equal(2, Saves());

        await Save().UpdateAsync(
            mail.Id, "Mail", true, new NotificationChannelSettingsInput(RecipientRoleIds: [3]), CancellationToken.None);
        Assert.Equal(3, Saves());
        Assert.Equal([3], _store.ChannelRoles[mail.Id]);
        Assert.Equal([3], (await List().HandleAsync(CancellationToken.None)).Single().Settings.RecipientRoleIds);

        var teams = await Save().CreateAsync(NotificationChannelKind.TeamsWebhook, "Teams", null, CancellationToken.None);
        Assert.Equal(5, Saves());
        await Secret().HandleAsync(teams.Id, Webhook, CancellationToken.None);
        Assert.Equal(6, Saves());

        // Канал без ролей: у списку — порожній перелік, а не null (`roleIds ?? []`).
        Assert.Empty((await List().HandleAsync(CancellationToken.None)).Single(c => c.Id == teams.Id).Settings.RecipientRoleIds!);

        await Delete().HandleAsync(teams.Id, CancellationToken.None);
        Assert.Equal(7, Saves());
    }

    private int Saves() => _uow.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IUnitOfWork.SaveChangesAsync));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public async Task Проба_Teams_з_DNS_відмовою_не_отримує_поштової_категорії_а_старий_шлях_пошти_шле_порожній_перелік_а_не_null()
    {
        var teams = await Save().CreateAsync(NotificationChannelKind.TeamsWebhook, "Teams", null, CancellationToken.None);

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: `classify: false` → `true` для не-пошти — Teams отримав би
        // «notifications.test.smtp.dns», хоча пошти тут немає.
        var webhook = new SpyChannelSender(NotificationChannelKind.TeamsWebhook)
        {
            Fails = new HttpRequestException(
                "No such host is known. (hooks.corp.example:443)",
                new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound)),
        };
        var dns = await Test(webhook).HandleAsync(teams.Id, CancellationToken.None);
        Assert.Equal((false, (string?)null, "notifications.testFailed"), (dns.Ok, dns.Error, dns.MessageKey));

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: `Settings.Recipients ?? []` у старому шляху пошти (без відправника каналу) —
        // канал лише з ролями передав би транспорту null замість порожнього переліку.
        var byRole = await Save().CreateAsync(
            NotificationChannelKind.Smtp, "By role", new NotificationChannelSettingsInput(RecipientRoleIds: [1]), CancellationToken.None);
        _sender.IsConfigured.Returns(true);
        await Test().HandleAsync(byRole.Id, CancellationToken.None);
        await _sender.Received(1).SendAsync(
            Arg.Is<IReadOnlyList<string>>(r => r != null && r.Count == 0),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S8")]
    public async Task Створення_зміна_секрет_і_видалення_каналу_пишуть_аудит_у_транзакції_збереження()
    {
        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: у AuditAndSaveAsync викликати Audit/Save поза ExecuteInTransactionAsync.
        var teams = await Save().CreateAsync(NotificationChannelKind.TeamsWebhook, "Teams", null, CancellationToken.None);
        await Save().UpdateAsync(teams.Id, "Teams 2", true, null, CancellationToken.None);
        await Secret().HandleAsync(teams.Id, "https://prod-17.westeurope.logic.azure.com/workflows/x", CancellationToken.None);
        await Delete().HandleAsync(teams.Id, CancellationToken.None);

        Assert.Equal(4, _tx.AuditInside.Count);
        Assert.All(_tx.AuditInside, inside => Assert.True(inside));
        Assert.All(_tx.SaveInside.Take(1), inside => Assert.True(inside));
        Assert.True(_tx.SaveInside.Count >= 4);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S8")]
    public async Task Збій_збереження_зміни_каналу_не_лишає_події_поза_транзакцією()
    {
        var teams = await Save().CreateAsync(NotificationChannelKind.TeamsWebhook, "Teams", null, CancellationToken.None);
        _tx.AuditInside.Clear();
        _uow.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<int>(new InvalidOperationException("db down")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Save().UpdateAsync(teams.Id, "Teams 2", true, null, CancellationToken.None));

        Assert.Equal([true], _tx.AuditInside);
    }

    private SaveNotificationChannelHandler Save() => new(_store, _sender, _access, _uow, _audit, _user, _clock);

    private DeleteNotificationChannelHandler Delete() => new(_store, _access, _uow, _audit, _user, _clock);

    private TestNotificationChannelHandler Test(params INotificationChannelSender[] channelSenders)
        => new(_store, _sender, channelSenders, _access, _audit, _user, _clock);

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
        => new(_store, new FakeProtector(), new WebhookUrlPolicy(suffixes.Split(';')), _sender, _access, _uow, _audit, _user, _clock);

    private sealed class FakeProtector : INotificationSecretProtector
    {
        public const string Mark = "protected:";

        public byte[] Protect(string secret) => Encoding.UTF8.GetBytes(Mark + secret);
    }
}
