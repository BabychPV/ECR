// tests/Ecr.Application.Tests/Notifications/SmtpSettingsHandlersTests.cs
using System.Text;
using System.Text.Json;
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

/// <summary>Адмін-налаштування SMTP (<c>D-263</c>): пароль write-only, валідація, журнал без пароля, кеш.</summary>
public sealed class SmtpSettingsHandlersTests
{
    private const int Actor = 7;
    private const string Password = "Sup3r-Secret-Pw";

    private readonly FakeStore _store = new();
    private readonly FakeCache _cache = new();
    private readonly List<SecurityEventRecord> _events = [];
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly TransactionProbe _tx;
    private readonly INotificationSender _sender = Substitute.For<INotificationSender>();

    public SmtpSettingsHandlersTests()
    {
        _tx = TransactionProbe.Attach(_uow, _audit);
        _clock.UtcNow.Returns(new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));
        _user.UserId.Returns(Actor);
        _audit.WriteSecurityEventAsync(Arg.Do<SecurityEventRecord>(_events.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    private static SmtpSettingsInput Input(
        string host = "smtp.corp.example", int port = 587, string from = "ecr@corp.example",
        SmtpAuthMode auth = SmtpAuthMode.Password, string? user = "mailer", string? password = Password,
        bool clear = false, bool enabled = true)
        => new(host, port, SmtpEncryptionMode.StartTls, from, "ECR", auth, user, password, clear, enabled);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public async Task Пароль_зберігається_захищеним_і_не_потрапляє_ні_у_відповідь_ні_в_журнал_ні_в_GET()
    {
        Arrange();
        var saved = await Save().HandleAsync(Input(), CancellationToken.None);

        Assert.True(saved.HasPassword);
        Assert.Equal(FakeProtector.Mark + Password, Encoding.UTF8.GetString(_store.Row!.PasswordProtected!));

        // ⛔ Мутація: додати поле «password» у SmtpSettingsView → цей рядок стане червоним.
        var serialized = JsonSerializer.Serialize(saved);
        Assert.DoesNotContain(Password, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("password\":\"", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ["Host", "Port", "EncryptionMode", "FromAddress", "FromName", "AuthMode", "UserName", "HasPassword", "IsEnabled", "Source", "Configured", "UpdatedAt"],
            typeof(SmtpSettingsView).GetProperties().Select(p => p.Name));

        var read = await Get().HandleAsync(CancellationToken.None);
        Assert.DoesNotContain(Password, JsonSerializer.Serialize(read), StringComparison.Ordinal);
        Assert.True(read.HasPassword);

        var audited = Assert.Single(_events, e => e.EventType == "SmtpSettingsCreated");
        Assert.DoesNotContain(Password, audited.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"passwordChanged\":true", audited.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public async Task Порожній_пароль_не_змінює_збережений_а_ClearPassword_і_режим_None_прибирають()
    {
        Arrange();
        await Save().HandleAsync(Input(), CancellationToken.None);
        var before = _store.Row!.PasswordProtected;

        await Save().HandleAsync(Input(password: null, from: "ecr2@corp.example"), CancellationToken.None);
        Assert.Equal(before, _store.Row!.PasswordProtected);
        Assert.Equal("ecr2@corp.example", _store.Row.FromAddress);
        Assert.Contains("\"passwordChanged\":false", _events.Last().DetailsJson, StringComparison.Ordinal);

        await Save().HandleAsync(Input(password: null, clear: true, enabled: false), CancellationToken.None);
        Assert.False(_store.Row!.HasPassword);

        await Save().HandleAsync(Input(), CancellationToken.None);
        Assert.True(_store.Row!.HasPassword);
        await Save().HandleAsync(Input(auth: SmtpAuthMode.None, user: null, password: null), CancellationToken.None);
        Assert.False(_store.Row!.HasPassword);
        Assert.Null(_store.Row.UserName);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    [InlineData("", 587, "ecr@corp.example", "host")]                          // порожній хост на ввімкнених
    [InlineData("smtp://evil", 587, "ecr@corp.example", "host")]                // схема
    [InlineData("smtp.corp.example/path", 587, "ecr@corp.example", "host")]     // шлях
    [InlineData("smtp corp", 587, "ecr@corp.example", "host")]                  // пробіл
    [InlineData("169.254.169.254", 25, "ecr@corp.example", "host")]             // метадані хмари / link-local
    [InlineData("smtp.corp.example", 0, "ecr@corp.example", "port")]
    [InlineData("smtp.corp.example", 65536, "ecr@corp.example", "port")]
    [InlineData("smtp.corp.example", 587, "not-an-address", "from")]
    [InlineData("smtp.corp.example", 587, "", "from")]
    public async Task Некоректні_host_порт_і_адреса_дають_422_і_нічого_не_пишуть(string host, int port, string from, string field)
    {
        Arrange();

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(Input(host: host, port: port, from: from), CancellationToken.None));

        Assert.Equal("ECR-REQ-0422", error.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.smtpSettingsInvalid", error.Details!["messageKey"]);
        Assert.Equal(field, error.Details["name"]);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Equal(0, _cache.Invalidations);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public async Task Приватні_й_loopback_хости_дозволені_а_пароль_без_логіна_і_без_пароля_ні()
    {
        Arrange();
        await Save().HandleAsync(Input(host: "10.1.2.3"), CancellationToken.None);
        await Save().HandleAsync(Input(host: "localhost"), CancellationToken.None);

        var noUser = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(Input(user: null), CancellationToken.None));
        Assert.Equal("auth", noUser.Details!["name"]);

        _store.Row = null;
        var noPassword = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(Input(password: null), CancellationToken.None));
        Assert.Equal("auth", noPassword.Details!["name"]);

        // Вимкнену чернетку можна зберегти неповною — вона не діє.
        var draft = await Save().HandleAsync(Input(host: "", from: "", password: null, enabled: false), CancellationToken.None);
        Assert.False(draft.Configured && draft.Source == "database");
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("user")]
    public async Task S1_зміна_адреси_порту_шифрування_чи_логіна_без_нового_пароля_дає_422_і_лишає_збережене(string changed)
    {
        Arrange();
        await Save().HandleAsync(Input(), CancellationToken.None);
        var before = _store.Row!.PasswordProtected;
        var invalidations = _cache.Invalidations;

        var input = changed switch
        {
            "host" => Input(host: "evil.example", password: null),
            "port" => Input(port: 2525, password: null),
            _ => Input(user: "other", password: null),
        };

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(input, CancellationToken.None));

        Assert.Equal("ECR-REQ-0422", error.ErrorCode);
        Assert.Equal(SaveSmtpSettingsHandler.PasswordReentryRequiredKey, error.Details!["messageKey"]);
        Assert.Equal("err.ECR-REQ-0422.smtpPasswordReentryRequired", error.Details["messageKey"]);
        Assert.Equal("smtp.corp.example", _store.Row.Host);
        Assert.Equal(before, _store.Row.PasswordProtected);
        Assert.Equal(invalidations, _cache.Invalidations);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public async Task S1_зміна_хоста_з_новим_паролем_проходить_а_без_зміни_адреси_порожній_пароль_лишає_старий()
    {
        Arrange();
        await Save().HandleAsync(Input(), CancellationToken.None);

        await Save().HandleAsync(Input(host: "SMTP.corp.example", password: null), CancellationToken.None);
        Assert.Equal(FakeProtector.Mark + Password, Encoding.UTF8.GetString(_store.Row!.PasswordProtected!));

        await Save().HandleAsync(Input(host: "relay.corp.example", password: "New-Pw-1"), CancellationToken.None);
        Assert.Equal("relay.corp.example", _store.Row!.Host);
        Assert.Equal(FakeProtector.Mark + "New-Pw-1", Encoding.UTF8.GetString(_store.Row.PasswordProtected!));

        // Явне очищення й перехід на None змінюють адресу без пароля — це не витік, а скидання секрету.
        await Save().HandleAsync(Input(host: "other.corp.example", password: null, clear: true, enabled: false), CancellationToken.None);
        Assert.False(_store.Row!.HasPassword);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public async Task Журнал_passwordChanged_true_при_новому_і_при_стиранні_пароля_false_при_незмінному_і_без_самого_пароля()
    {
        Arrange();
        await Save().HandleAsync(Input(), CancellationToken.None);
        Assert.Contains("\"passwordChanged\":true", _events.Last().DetailsJson, StringComparison.Ordinal);

        await Save().HandleAsync(Input(password: null, from: "ecr2@corp.example"), CancellationToken.None);
        Assert.Contains("\"passwordChanged\":false", _events.Last().DetailsJson, StringComparison.Ordinal);

        // ⛔ Мутація: повернути `clears && input.ClearPassword` → перехід на None без прапора дасть false.
        await Save().HandleAsync(Input(auth: SmtpAuthMode.None, user: null, password: null), CancellationToken.None);
        Assert.False(_store.Row!.HasPassword);
        Assert.Contains("\"passwordChanged\":true", _events.Last().DetailsJson, StringComparison.Ordinal);

        // Пароля вже немає — повторне збереження без автентифікації секрету не змінює.
        await Save().HandleAsync(Input(auth: SmtpAuthMode.None, user: null, password: null), CancellationToken.None);
        Assert.Contains("\"passwordChanged\":false", _events.Last().DetailsJson, StringComparison.Ordinal);

        await Save().HandleAsync(Input(), CancellationToken.None);
        await Save().HandleAsync(Input(password: null, clear: true, enabled: false), CancellationToken.None);
        Assert.Contains("\"passwordChanged\":true", _events.Last().DetailsJson, StringComparison.Ordinal);

        Assert.All(_events, e => Assert.DoesNotContain(Password, e.DetailsJson, StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public async Task S1_пароль_без_шифрування_дає_422_а_режим_без_автентифікації_без_шифрування_дозволений()
    {
        Arrange();

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(Input() with { EncryptionMode = SmtpEncryptionMode.None }, CancellationToken.None));
        Assert.Equal(SaveSmtpSettingsHandler.PasswordNeedsTlsKey, error.Details!["messageKey"]);
        Assert.Equal("encryption", error.Details["name"]);
        Assert.False(_store.Row?.HasPassword ?? false);
        Assert.Equal(0, _cache.Invalidations);

        var relay = await Save().HandleAsync(
            Input(auth: SmtpAuthMode.None, user: null, password: null) with { EncryptionMode = SmtpEncryptionMode.None },
            CancellationToken.None);
        Assert.Equal(SmtpEncryptionMode.None, relay.EncryptionMode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public async Task Збереження_скидає_кеш_транспорту_а_джерело_у_відповіді_каже_БД_конфігурація_або_нічого()
    {
        Arrange();

        _sender.IsConfigured.Returns(false);
        Assert.Equal("none", (await Get().HandleAsync(CancellationToken.None)).Source);

        _sender.IsConfigured.Returns(true);
        Assert.Equal("configuration", (await Get().HandleAsync(CancellationToken.None)).Source);

        var saved = await Save().HandleAsync(Input(), CancellationToken.None);
        Assert.Equal("database", saved.Source);
        Assert.Equal(1, _cache.Invalidations);

        // Вимкнені налаштування не діють — транспорт знову із конфігурації.
        var off = await Save().HandleAsync(Input(enabled: false), CancellationToken.None);
        Assert.Equal("configuration", off.Source);
        Assert.Equal(2, _cache.Invalidations);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public async Task Проба_шле_лист_на_введену_адресу_рівно_одну_а_порожня_адреса_веде_до_пошти_користувача()
    {
        Arrange();
        _sender.IsConfigured.Returns(true);
        var handler = TestHandler(" me@corp.example ");

        var ok = await handler.HandleAsync(new SmtpTestRequest(" ops@corp.example "), CancellationToken.None);
        Assert.True(ok.Ok);

        // ⛔ Мутація: ігнорувати request.To (брати пошту користувача) → цей рядок стане червоним.
        await _sender.Received(1).SendAsync(
            Arg.Is<IReadOnlyList<string>>(r => r.Single() == "ops@corp.example"), "ECR test notification",
            "SMTP settings test message.", Arg.Any<CancellationToken>());

        _sender.ClearReceivedCalls();
        await handler.HandleAsync(new SmtpTestRequest("  "), CancellationToken.None);
        await _sender.Received(1).SendAsync(
            Arg.Is<IReadOnlyList<string>>(r => r.Single() == "me@corp.example"), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());

        // Журнал: лише домен і ознака «введена», без повної адреси.
        var audited = _events.Last(e => e.EventType == "SmtpSettingsTested").DetailsJson;
        Assert.Contains("\"domain\":\"corp.example\"", audited, StringComparison.Ordinal);
        Assert.DoesNotContain("ops@", audited, StringComparison.Ordinal);
        Assert.DoesNotContain("me@", audited, StringComparison.Ordinal);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    [InlineData("a@corp.example, b@evil.example")]
    [InlineData("a@corp.example,b@evil.example")]
    [InlineData("a@corp.example;b@evil.example")]
    [InlineData("a@corp.example b@evil.example")]
    [InlineData("Ops <a@corp.example>")]
    [InlineData("a@corp.example\r\nBcc: b@evil.example")]
    [InlineData("a@corp.example\nb@evil.example")]
    [InlineData("not-an-address")]
    [InlineData("a@corp.example\0evil")]
    [InlineData("a@corp.example\x2028Zb@evil.example")]
    [InlineData("a@corp.example\x0085Zb@evil.example")]
    [InlineData("\"a b\"@corp.example")]
    [InlineData("\"a\"@corp.example")]
    [InlineData("a@[IPv6:::1]")]
    [InlineData("a@[127.0.0.1]")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa@corp.example")]
    [InlineData("a@xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx.example")]
    [InlineData("a@corp.example.")]
    [InlineData("a@-corp.example")]
    public async Task Проба_відхиляє_списки_роздільники_імена_відображення_і_CRLF_422_і_нічого_не_шле(string to)
    {
        Arrange();
        _sender.IsConfigured.Returns(true);
        var handler = TestHandler("me@corp.example");

        // ⛔ Мутація: прибрати IsSingleAddress (брати request.To як є) → рядки червоніють (відкритий релей).
        var bad = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync(new SmtpTestRequest(to), CancellationToken.None));

        Assert.Equal("err.ECR-REQ-0422.smtpTestRecipientInvalid", bad.Details!["messageKey"]);
        await _sender.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default!, default);
    }

    /// <summary>Сама адреса (і «Від кого» в налаштуваннях) — ті самі вектори: захист не лише в пробі.</summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-256")]
    [InlineData("a@corp.example\0evil", false)]
    [InlineData("a@corp.example\x2028", false)]
    [InlineData("a@corp.example\x85", false)]
    [InlineData("\"a b\"@corp.example", false)]
    [InlineData("\"a\"@corp.example", false)]
    [InlineData("a@[IPv6:::1]", false)]
    [InlineData("a@[127.0.0.1]", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa@corp.example", false)]
    [InlineData("a@xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx.example", false)]
    [InlineData("a@corp.example.", false)]
    [InlineData("a@-corp.example", false)]
    [InlineData("a@corp-.example", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa@xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx.example", true)]
    [InlineData("ops.team+probe@corp-mail.example", true)]
    public void Адреса_відхиляє_літерали_квотування_довгі_частини_крапку_й_дефіс_і_приймає_звичайні(
        string address, bool expected)
    {
        // ⛔ Мутація: прибрати нові перевірки в SmtpSettings.IsValidAddress → відхилювані рядки червоніють.
        Assert.Equal(expected, SmtpSettings.IsValidAddress(address));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public async Task Проба_віддає_лише_ключ_категорії_без_тексту_відмови_і_пише_журнал_без_тексту_транспорту()
    {
        Arrange();
        _sender.IsConfigured.Returns(true);
        var handler = TestHandler("me@corp.example");

        _sender.SendAsync(default!, default!, default!, default)
            .ReturnsForAnyArgs(Task.FromException(new System.Net.Mail.SmtpException(
                System.Net.Mail.SmtpStatusCode.MustIssueStartTlsFirst, "5.7.0 Must issue a STARTTLS command first")));
        var failed = await handler.HandleAsync(new SmtpTestRequest(null), CancellationToken.None);
        Assert.False(failed.Ok);
        Assert.NotNull(failed.MessageKey);
        Assert.StartsWith("notifications.test.smtp.", failed.MessageKey, StringComparison.Ordinal);
        Assert.Null(failed.Error);

        // Невідома категорія: теж без сирого тексту (клієнт покаже загальне «проба не вдалась»).
        _sender.SendAsync(default!, default!, default!, default)
            .ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("secret-internal-host.corp:25 refused")));
        var unknown = await handler.HandleAsync(new SmtpTestRequest(null), CancellationToken.None);
        Assert.False(unknown.Ok);
        Assert.Null(unknown.Error);
        Assert.DoesNotContain("secret-internal-host", JsonSerializer.Serialize(unknown), StringComparison.Ordinal);

        _sender.IsConfigured.Returns(false);
        var none = await handler.HandleAsync(new SmtpTestRequest(null), CancellationToken.None);
        Assert.Equal("notifications.test.smtpNotConfigured", none.MessageKey);
        Assert.All(_events.Where(e => e.EventType == "SmtpSettingsTested"), e => Assert.DoesNotContain("STARTTLS", e.DetailsJson, StringComparison.Ordinal));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    [InlineData(null)]
    [InlineData("nope")]
    public async Task Проба_без_валідної_пошти_у_користувача_дає_422_і_нічого_не_шле(string? email)
    {
        Arrange();
        _sender.IsConfigured.Returns(true);
        var handler = TestHandler(email);

        var bad = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync(new SmtpTestRequest(null), CancellationToken.None));

        Assert.Equal("err.ECR-REQ-0422.smtpTestRecipientInvalid", bad.Details!["messageKey"]);
        await _sender.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public async Task Без_System_ManageNotifications_усі_три_обробники_дають_403_з_назвою_права()
    {
        _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }.Permission("System.ViewHealth").Build());

        // ⛔ Мутація: прибрати PermissionCheck.RequireAsync з SaveSmtpSettingsHandler → відповідний рядок стане червоним.
        Func<Task>[] calls =
        [
            () => Get().HandleAsync(CancellationToken.None),
            () => Save().HandleAsync(Input(), CancellationToken.None),
            () => TestHandler("me@corp.example").HandleAsync(new SmtpTestRequest(null), CancellationToken.None),
        ];

        foreach (var call in calls)
        {
            var denied = await Assert.ThrowsAsync<AccessDeniedException>(call);
            Assert.Equal("System.ManageNotifications", denied.Details!["permission"]);
            Assert.Contains("System.ManageNotifications", denied.Message, StringComparison.Ordinal);
        }

        Assert.Null(_store.Row);
        await _sender.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default!, default);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    [InlineData("smtp.corp.example", 587, "not-an-address", "from")]   // вимкнена чернетка: заданий From усе одно адреса
    [InlineData("smtp://evil", 587, "", "host")]                       // вимкнена чернетка: заданий хост усе одно хост
    [InlineData("smtp.corp.example", 587, "ecr@corp.example", "fromName", 101)]
    [InlineData("smtp.corp.example", 587, "ecr@corp.example", "auth", 0, 255)]
    public async Task Вимкнена_чернетка_перевіряє_задані_поля_а_ім_я_відправника_й_логін_мають_стелю(
        string host, int port, string from, string field, int fromNameLength = 0, int userLength = 0)
    {
        Arrange();
        var input = Input(host: host, port: port, from: from, enabled: field is "from" or "host" ? false : true);
        if (fromNameLength > 0)
        {
            input = input with { FromName = new string('n', fromNameLength) };
        }

        if (userLength > 0)
        {
            input = input with { UserName = new string('u', userLength) };
        }

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ (SaveSmtpSettingsHandler.Validate): `host.Length > 0` / `from.Length > 0` → `< 0`,
        // стеля FromName 100 і логіна 254 (`>` → `>=` проходить, прибрати перевірку FromName — червоніє «fromName»).
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(input, CancellationToken.None));

        Assert.Equal(field, error.Details!["name"]);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public async Task Межові_значення_порт_1_і_65535_ім_я_100_і_логін_254_приймаються()
    {
        Arrange();

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: `Port is < 1 or > 65535` → `<= 1` чи `>= 65535`, стелі FromName/логіна `>` → `>=`
        // відхилили б рівно ці межові значення.
        foreach (var port in new[] { 1, 65535 })
        {
            var saved = await Save().HandleAsync(Input(port: port), CancellationToken.None);
            Assert.Equal(port, saved.Port);
        }

        var edge = await Save().HandleAsync(
            Input() with { FromName = new string('n', 100), UserName = new string('u', 254) }, CancellationToken.None);

        Assert.Equal(100, edge.FromName!.Length);
        Assert.Equal(254, edge.UserName!.Length);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public async Task Невідомий_режим_шифрування_чи_автентифікації_дає_422_mode()
    {
        Arrange();

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: `||` → `&&` у перевірці Enum.IsDefined пропустив би кожен із цих двох запитів.
        foreach (var input in new[]
                 {
                     Input() with { EncryptionMode = (SmtpEncryptionMode)7 },
                     Input() with { AuthMode = (SmtpAuthMode)9 },
                 })
        {
            var error = await Assert.ThrowsAsync<BusinessRuleException>(
                () => Save().HandleAsync(input, CancellationToken.None));
            Assert.Equal("mode", error.Details!["name"]);
        }

        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public async Task Без_рядка_в_базі_GET_каже_без_пароля_вимкнено_а_неповний_рядок_без_транспорту_це_none()
    {
        Arrange();

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ (GetSmtpSettingsHandler.ToView): `HasPassword: false` / `IsEnabled: false` → `true`
        // для відсутнього рядка, `source != "none"` → `==`, гілка «configuration» замість «none».
        _sender.IsConfigured.Returns(false);
        var empty = await Get().HandleAsync(CancellationToken.None);
        Assert.False(empty.HasPassword);
        Assert.False(empty.IsEnabled);
        Assert.False(empty.Configured);
        Assert.Equal(587, empty.Port);

        var draft = await Save().HandleAsync(Input(enabled: false), CancellationToken.None);
        Assert.Equal("none", draft.Source);
        Assert.False(draft.Configured);

        var on = await Save().HandleAsync(Input(password: null), CancellationToken.None);
        Assert.Equal("database", on.Source);
        Assert.True(on.Configured);

        _sender.IsConfigured.Returns(true);
        var off = await Save().HandleAsync(Input(enabled: false, password: null), CancellationToken.None);
        Assert.Equal("configuration", off.Source);
        Assert.True(off.Configured);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public async Task Перехід_на_режим_без_автентифікації_без_пароля_не_пише_passwordChanged()
    {
        Arrange();

        // Рядка з паролем немає: режим None без ClearPassword нічого не змінює в паролі.
        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: `clears && input.ClearPassword` → `clears || input.ClearPassword` — тут стало б true.
        await Save().HandleAsync(Input(auth: SmtpAuthMode.None, user: null, password: null), CancellationToken.None);

        Assert.Contains("\"passwordChanged\":false", _events.Last().DetailsJson, StringComparison.Ordinal);
        Assert.Equal("SmtpSettingsCreated", _events.Last().EventType);

        await Save().HandleAsync(Input(auth: SmtpAuthMode.None, user: null, password: null), CancellationToken.None);
        Assert.Equal("SmtpSettingsUpdated", _events.Last().EventType);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public async Task Проба_без_транспорту_не_віддає_тексту_а_журнал_розрізняє_введену_й_власну_адресу()
    {
        Arrange();
        var handler = TestHandler("me@corp.example");

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ (TestSmtpSettingsHandler): прибрати `if (!result.Ok) { Error = null }` або
        // заперечити умову — назовні пішов би англійський текст «not configured» замість лише ключа.
        _sender.IsConfigured.Returns(false);
        var none = await handler.HandleAsync(new SmtpTestRequest("ops@corp.example"), CancellationToken.None);
        Assert.False(none.Ok);
        Assert.Null(none.Error);
        Assert.Equal("notifications.test.smtpNotConfigured", none.MessageKey);

        _sender.IsConfigured.Returns(true);
        var own = await handler.HandleAsync(new SmtpTestRequest(null), CancellationToken.None);
        Assert.True(own.Ok);

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: `useEntered ? "entered" : "own"` → завжди одне з двох.
        var tested = _events.FindAll(e => e.EventType == "SmtpSettingsTested");
        Assert.Equal(2, tested.Count);
        Assert.Contains("\"recipient\":\"entered\"", tested[0].DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"domain\":\"corp.example\"", tested[0].DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"recipient\":\"own\"", tested[1].DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"ok\":true", tested[1].DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    public async Task Порожній_рядок_пароля_як_і_null_не_міняє_збереженого_а_збереження_доходить_до_бази()
    {
        Arrange();
        await Save().HandleAsync(Input(), CancellationToken.None);
        var before = _store.Row!.PasswordProtected;

        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: `IsNullOrEmpty(Password) ? null : Password` → завжди Password — порожній рядок із
        // форми (поле не чіпали) затер би пароль захищеним «нічим»; прибрати `uow.SaveChangesAsync`.
        await Save().HandleAsync(Input(password: string.Empty), CancellationToken.None);

        Assert.Equal(before, _store.Row!.PasswordProtected);
        Assert.Contains("\"passwordChanged\":false", _events.Last().DetailsJson, StringComparison.Ordinal);
        await _uow.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S8")]
    public async Task Збереження_налаштувань_SMTP_пише_аудит_у_транзакції_збереження_і_збій_її_відкочує()
    {
        // ⚠ МУТАЦІЙНИЙ ДОКАЗ: у AuditAndSaveAsync викликати Audit/Save поза ExecuteInTransactionAsync.
        Arrange();
        await Save().HandleAsync(Input(), CancellationToken.None);

        Assert.Equal([true], _tx.AuditInside);
        Assert.Equal([true], _tx.SaveInside);

        _uow.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<int>(new InvalidOperationException("db down")));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Save().HandleAsync(Input(host: "smtp2.corp.example"), CancellationToken.None));

        Assert.Equal([true, true], _tx.AuditInside);
    }

    private TestSmtpSettingsHandler TestHandler(string? email)
    {
        var users = Substitute.For<IUserStore>();
        var me = new Ecr.Domain.Entities.Security.User("me", "Me", Ecr.Domain.Enums.AuthProvider.Local);

        if (email is not null)
        {
            me.SetEmail(email);
        }

        users.FindByIdAsync(Actor, Arg.Any<CancellationToken>()).Returns(me);

        return new TestSmtpSettingsHandler(_sender, _access, _audit, _user, _clock, users);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-263")]
    [InlineData("ecr@[127.0.0.1]")]
    [InlineData("ops team@corp.example")]
    public async Task Раніше_збережений_From_що_не_проходить_суворішу_перевірку_читається_без_помилки_а_PUT_його_відхиляє(
        string legacyFrom)
    {
        Arrange();
        var row = new SmtpSettings(_clock.UtcNow, Actor);
        row.Update(
            "smtp.corp.example", 587, SmtpEncryptionMode.StartTls, legacyFrom, null, SmtpAuthMode.None, null, true,
            _clock.UtcNow, Actor);
        _store.Row = row;

        // Читання (і, отже, ефективні налаштування відправника) валідації адреси не виконує.
        // ⛔ Мутація: викликати IsValidAddress у GetSmtpSettingsHandler / IsComplete → кидає або конфігурація «не повна».
        var read = await Get().HandleAsync(CancellationToken.None);
        Assert.Equal(legacyFrom, read.FromAddress);
        Assert.True(row.IsComplete);

        // Нове значення при збереженні — перевіряється.
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(Input(from: legacyFrom), CancellationToken.None));
    }

    private void Arrange()
        => _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }.Permission("System.ManageNotifications").Build());

    private GetSmtpSettingsHandler Get() => new(_store, _sender, _access, _user);

    private SaveSmtpSettingsHandler Save()
        => new(_store, new FakeProtector(), _cache, _sender, _access, _uow, _audit, _user, _clock);

    private sealed class FakeStore : ISmtpSettingsStore
    {
        public SmtpSettings? Row { get; set; }

        public Task<SmtpSettings?> FindAsync(CancellationToken ct) => Task.FromResult(Row);

        public void Add(SmtpSettings settings) => Row = settings;
    }

    private sealed class FakeCache : ISmtpSettingsCache
    {
        public int Invalidations { get; private set; }

        public void Invalidate() => Invalidations++;
    }

    private sealed class FakeProtector : ISmtpPasswordProtector
    {
        public const string Mark = "protected:";

        public byte[] Protect(string password) => Encoding.UTF8.GetBytes(Mark + password);
    }
}