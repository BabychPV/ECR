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
    private readonly INotificationSender _sender = Substitute.For<INotificationSender>();

    public SmtpSettingsHandlersTests()
    {
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
        await Save().HandleAsync(Input(host: "localhost", password: null), CancellationToken.None);

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
    public async Task Проба_шле_лист_лише_поточному_користувачу_а_не_адресату_із_запиту()
    {
        Arrange();
        _sender.IsConfigured.Returns(true);
        var handler = TestHandler(" me@corp.example ");

        var ok = await handler.HandleAsync(new SmtpTestRequest("victim@evil.example"), CancellationToken.None);
        Assert.True(ok.Ok);

        // ⛔ Мутація: взяти адресу із request.To замість користувача → цей рядок стане червоним.
        await _sender.Received(1).SendAsync(
            Arg.Is<IReadOnlyList<string>>(r => r.Single() == "me@corp.example"), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await _sender.DidNotReceive().SendAsync(
            Arg.Is<IReadOnlyList<string>>(r => r.Contains("victim@evil.example")), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
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
            () => handler.HandleAsync(new SmtpTestRequest("victim@evil.example"), CancellationToken.None));

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