// tests/Ecr.Application.Tests/Notifications/NotificationRuleHandlersTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Notifications;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Notifications;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Notifications;

/// <summary>
/// Правила сповіщень і журнал доставок (<c>BE-33</c>): повна вісь подій,
/// ідемпотентна заміна матриці, право, стеля сторінки журналу.
/// </summary>
public sealed class NotificationRuleHandlersTests
{
    private const int Actor = 7;

    private static readonly NotificationChannelSettingsInput Smtp =
        new(Recipients: ["ops@corp.example"]);

    private readonly FakeNotificationStore _store = new();
    private readonly List<SecurityEventRecord> _events = [];
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public NotificationRuleHandlersTests()
    {
        _clock.UtcNow.Returns(new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc));
        _user.UserId.Returns(Actor);
        _audit.WriteSecurityEventAsync(Arg.Do<SecurityEventRecord>(_events.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        Allow("System.ManageNotifications");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-33")]
    public async Task Матриця_перелічує_всі_види_подій_навіть_коли_правила_немає_жодного()
    {
        var matrix = await Rules().HandleAsync(CancellationToken.None);

        // ⛔ Саме ВСІ п'ять, а не «ті, на які є правило»: порожня клітинка має
        // означати «правила немає», а не «такої події не буває». Число
        // літералом — інакше твердження їхало б разом із переліком.
        Assert.Equal(5, matrix.EventKinds.Count);
        Assert.Equal(Enum.GetValues<NotificationEventKind>(), matrix.EventKinds);
        Assert.Empty(matrix.Rules);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-33")]
    public async Task Та_сама_матриця_двічі_не_дублює_правил_а_прибрана_клітинка_зникає()
    {
        var channel = await NewChannelAsync();
        NotificationRuleView[] wanted =
        [
            new(NotificationEventKind.JobFailed, channel, NotificationSeverity.Warning, IsEnabled: true),
            new(NotificationEventKind.ExportFailed, channel, NotificationSeverity.Error, IsEnabled: false),
        ];

        await Replace().HandleAsync(wanted, CancellationToken.None);
        await Replace().HandleAsync(wanted, CancellationToken.None);

        // ⛔ Ідемпотентність: друге застосування МІНЯЄ ті самі клітинки, а не
        // додає другу пару «подія + канал».
        Assert.Equal(2, _store.Rules.Count);

        // Третє застосування — уже інша матриця: клітинки, якої в ній немає,
        // не лишається.
        await Replace().HandleAsync([wanted[0] with { MinSeverity = NotificationSeverity.Error }], CancellationToken.None);

        var rule = Assert.Single(_store.Rules);
        Assert.Equal(
            (NotificationEventKind.JobFailed, channel, NotificationSeverity.Error, true),
            (rule.EventKind, rule.ChannelId, rule.MinSeverity, rule.IsEnabled));

        // Порожня матриця — законна: «нікого не сповіщати».
        await Replace().HandleAsync([], CancellationToken.None);
        Assert.Empty(_store.Rules);

        // Кожна заміна — рядок журналу безпеки; третя прибрала одну клітинку.
        var replaced = _events.FindAll(e => e.EventType == "NotificationRulesReplaced");
        Assert.Equal(4, replaced.Count);
        Assert.Contains("\"removed\":1", replaced[2].DetailsJson, StringComparison.Ordinal);
        Assert.Equal(Actor, replaced[0].ChangedByUserId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-33")]
    public async Task Правило_на_неіснуючий_канал_це_404_а_дві_клітинки_на_ту_саму_пару_422()
    {
        var channel = await NewChannelAsync();

        var missing = await Assert.ThrowsAsync<NotFoundException>(
            () => Replace().HandleAsync(
                [new(NotificationEventKind.JobFailed, channel + 100, NotificationSeverity.Info, true)],
                CancellationToken.None));

        var twice = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Replace().HandleAsync(
                [
                    new(NotificationEventKind.JobFailed, channel, NotificationSeverity.Info, true),
                    new(NotificationEventKind.JobFailed, channel, NotificationSeverity.Error, false),
                ],
                CancellationToken.None));

        var unknownEvent = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Replace().HandleAsync(
                [new((NotificationEventKind)99, channel, NotificationSeverity.Info, true)], CancellationToken.None));

        Assert.Equal("err.ECR-INT-0404.notificationChannel", missing.Details!["messageKey"]);
        Assert.Equal("err.ECR-REQ-0422.notificationRuleInvalid", twice.Details!["messageKey"]);
        Assert.Equal("err.ECR-REQ-0422.notificationRuleInvalid", unknownEvent.Details!["messageKey"]);

        // ⛔ Жодна з трьох відмов не лишає по собі половини матриці.
        Assert.Empty(_store.Rules);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-33")]
    public async Task Без_System_ManageNotifications_матриця_не_читається_не_міняється_і_журнал_закритий()
    {
        var channel = await NewChannelAsync();
        await Replace().HandleAsync(
            [new(NotificationEventKind.JobFailed, channel, NotificationSeverity.Info, true)], CancellationToken.None);
        Allow("System.ViewHealth");

        await Assert.ThrowsAsync<AccessDeniedException>(() => Rules().HandleAsync(CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Replace().HandleAsync([], CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Deliveries().HandleAsync(new CursorRequest(), channelId: null, status: null, CancellationToken.None));

        // ⛔ Відмова на `PUT` мусить статися ДО запису: інакше «немає права»
        // означало б «матрицю вже стерто, але тобі про це не скажуть».
        Assert.Single(_store.Rules);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-33")]
    public async Task Сторінка_журналу_понад_двісті_рядків_відхиляється_а_не_обрізається_мовчки()
    {
        _store.Deliveries.Add(new NotificationDeliveryView(
            1, _clock.UtcNow, 1, "Mail", NotificationEventKind.JobFailed, "job:7",
            NotificationDeliveryStatus.Failed, "relay refused"));

        // ⚠ Число літералом: `MaxLimit + 1` рухалося б разом зі стелею, і
        // підміна 200 на 20000 лишила б цей набір зеленим.
        var tooBig = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Deliveries().HandleAsync(new CursorRequest(201), null, null, CancellationToken.None));
        var zero = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Deliveries().HandleAsync(new CursorRequest(0), null, null, CancellationToken.None));

        Assert.Equal("err.ECR-REQ-0422.pageSizeOutOfRange", tooBig.Details!["messageKey"]);
        Assert.Equal("200", tooBig.Details!["max"]);
        Assert.Equal("ECR-REQ-0422", zero.ErrorCode);

        var page = await Deliveries().HandleAsync(new CursorRequest(200), null, null, CancellationToken.None);
        Assert.Equal("job:7", Assert.Single(page.Items).EventKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-33")]
    public async Task Підсумок_журналу_приймається_лише_іменем_із_переліку_а_решта_це_422()
    {
        _store.Deliveries.Add(new NotificationDeliveryView(
            1, _clock.UtcNow, 1, "Mail", NotificationEventKind.JobFailed, "job:7",
            NotificationDeliveryStatus.Failed, "relay refused"));

        // Ім'я — у будь-якому регістрі: фільтр приходить із рядка запиту.
        var byName = await Deliveries().HandleAsync(new CursorRequest(50), null, "failed", CancellationToken.None);
        Assert.Equal("job:7", Assert.Single(byName.Items).EventKey);

        // ⛔ Числа переліку — НЕ значення фільтра: «2» дорівнює `Failed` лише
        // всередині .NET, а в рядку запиту це просто невідомий підсумок.
        foreach (var wrong in new[] { "Delivered", "2", "99", "-1" })
        {
            var refused = await Assert.ThrowsAsync<BusinessRuleException>(
                () => Deliveries().HandleAsync(new CursorRequest(50), null, wrong, CancellationToken.None));

            Assert.Equal("ECR-REQ-0422", refused.ErrorCode);
            Assert.Equal("err.ECR-REQ-0422.notificationDeliveryStatus", refused.Details!["messageKey"]);
            Assert.Equal(wrong, refused.Details!["status"]);
        }

        // Порожній рядок — це «усі», а не помилка: так приїжджає незаповнене
        // поле форми.
        var all = await Deliveries().HandleAsync(new CursorRequest(50), null, "  ", CancellationToken.None);
        Assert.Single(all.Items);
    }

    private void Allow(string permission)
        => _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }.Permission(permission).Build());

    private async Task<int> NewChannelAsync()
        => (await new SaveNotificationChannelHandler(
                _store, Substitute.For<INotificationSender>(), _access, _uow, _audit, _user, _clock)
            .CreateAsync(NotificationChannelKind.Smtp, "Mail", Smtp, CancellationToken.None)).Id;

    private GetNotificationRulesHandler Rules() => new(_store, _access, _user);

    private ReplaceNotificationRulesHandler Replace() => new(_store, _access, _uow, _audit, _user, _clock);

    private ListNotificationDeliveriesHandler Deliveries() => new(_store, _access, _user);
}
