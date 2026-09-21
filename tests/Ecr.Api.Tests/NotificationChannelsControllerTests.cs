// tests/Ecr.Api.Tests/NotificationChannelsControllerTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Notifications;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Канали сповіщень крізь справжній HTTP і справжню базу (<c>BE-33</c>): право,
/// секрет write-only, SSRF-перелік із <c>appsettings.json</c>, видалення з правилами.
/// </summary>
[Collection("SqlServer")]
public sealed class NotificationChannelsControllerTests(SqlServerFixture sql)
{
    private const string Password = "Api-Notify-Probe-2026!";
    private const string Marker = "TopSecretSig-7f3a";
    private static readonly string[] Recipients = ["ops@corp.example"];
    private static readonly Uri Channels = new("/api/v1/notifications/channels", UriKind.Relative);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-33")]
    public async Task Без_права_403_на_кожному_маршруті()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "System.ViewHealth").ConfigureAwait(true);

        HttpResponseMessage[] responses =
        [
            await client.GetAsync(Channels).ConfigureAwait(true),
            await client.PostAsJsonAsync(Channels, new { kind = "TeamsWebhook", name = "x" }).ConfigureAwait(true),
            await client.PutAsJsonAsync(At("1"), new { name = "x", isEnabled = true }).ConfigureAwait(true),
            await client.DeleteAsync(At("1")).ConfigureAwait(true),
            await client.PutAsJsonAsync(At("1/secret"), new { secret = "x" }).ConfigureAwait(true),
            await client.PostAsync(At("1/test"), content: null).ConfigureAwait(true),
        ];

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-33")]
    public async Task Секрет_не_повертається_жодною_відповіддю_не_лежить_у_базі_відкрито_і_не_йде_в_журнали()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "System.ManageNotifications").ConfigureAwait(true);

        var name = $"teams-{Guid.NewGuid():N}";
        var url = $"https://prod-17.westeurope.logic.azure.com/workflows/abc?sig={Marker}";
        var seen = new StringBuilder();

        var created = await client.PostAsJsonAsync(
            Channels, new { kind = "TeamsWebhook", name, settings = new { title = "ECR" } }).ConfigureAwait(true);
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"{created.StatusCode}: {app.ErrorsText}");
        var id = (await BodyAsync(created, seen).ConfigureAwait(true)).GetProperty("id").GetInt32();

        var replaced = await client.PutAsJsonAsync(At($"{id}/secret"), new { secret = url }).ConfigureAwait(true);
        Assert.True(replaced.StatusCode == HttpStatusCode.OK, $"{replaced.StatusCode}: {app.ErrorsText}");
        Assert.True((await BodyAsync(replaced, seen).ConfigureAwait(true)).GetProperty("hasSecret").GetBoolean());

        var listed = await BodyAsync(await client.GetAsync(Channels).ConfigureAwait(true), seen).ConfigureAwait(true);
        var row = listed.EnumerateArray().Single(c => c.GetProperty("id").GetInt32() == id);
        Assert.True(row.GetProperty("hasSecret").GetBoolean());
        Assert.Equal("ECR", row.GetProperty("settings").GetProperty("title").GetString());

        // BE-34: проба йде САМИМ відправником вебхука — на адресу з секрету, яку
        // жодна відповідь не показувала. Транспорт підмінений стендом, тобто
        // мережі тут немає; перевіряється рівно те, що запит пішов туди, куди
        // вказує секрет, і що відповідь про це мовчить.
        var probe = await BodyAsync(await client.PostAsync(At($"{id}/test"), null).ConfigureAwait(true), seen).ConfigureAwait(true);
        Assert.True(probe.GetProperty("ok").GetBoolean(), $"{app.ErrorsText}");
        Assert.Equal(new Uri(url), Assert.Single(app.WebhookCalls));

        // ⛔ Головне: значення секрету немає НІДЕ, куди воно могло б просочитися.
        Assert.DoesNotContain(Marker, seen.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(app.ServerLog, line => line.Contains(Marker, StringComparison.Ordinal));

        await using var db = NewDb();
        var stored = await db.NotificationChannels.AsNoTracking().SingleAsync(c => c.Id == id).ConfigureAwait(true);
        Assert.True(stored.HasSecret);
        Assert.DoesNotContain(Marker, Encoding.UTF8.GetString(stored.SecretProtected!), StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, stored.SettingsJson, StringComparison.Ordinal);

        var events = await db.Database
            .SqlQuery<string>($"SELECT ISNULL(DetailsJson, N'') AS Value FROM aud.SecurityEvent WHERE EventType LIKE N'NotificationChannel%'")
            .ToListAsync().ConfigureAwait(true);
        Assert.Contains(events, e => e.Contains(name, StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Contains(Marker, StringComparison.Ordinal));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-33")]
    [InlineData("https://evil.example/workflows/abc")]
    [InlineData("http://prod-17.westeurope.logic.azure.com/workflows/abc")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    public async Task Вебхук_на_недозволений_хост_http_або_IP_дає_422_і_секрет_не_зберігається(string url)
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "System.ManageNotifications").ConfigureAwait(true);

        var created = await client.PostAsJsonAsync(
            Channels, new { kind = "TeamsWebhook", name = $"teams-{Guid.NewGuid():N}" }).ConfigureAwait(true);
        var id = (await BodyAsync(created, null).ConfigureAwait(true)).GetProperty("id").GetInt32();

        var refused = await client.PutAsJsonAsync(At($"{id}/secret"), new { secret = url }).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = await BodyAsync(refused, null).ConfigureAwait(true);
        Assert.Equal("ECR-REQ-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-REQ-0422.webhookUrlNotAllowed", problem.GetProperty("messageKey").GetString());

        await using var db = NewDb();
        Assert.False((await db.NotificationChannels.AsNoTracking().SingleAsync(c => c.Id == id).ConfigureAwait(true)).HasSecret);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-33")]
    public async Task Видалення_каналу_прибирає_його_правила_а_журнал_доставок_лишає()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "System.ManageNotifications").ConfigureAwait(true);

        var created = await client.PostAsJsonAsync(
            Channels,
            new
            {
                kind = "Smtp",
                name = $"mail-{Guid.NewGuid():N}",
                settings = new { recipients = Recipients },
            }).ConfigureAwait(true);
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"{created.StatusCode}: {app.ErrorsText}");
        var id = (await BodyAsync(created, null).ConfigureAwait(true)).GetProperty("id").GetInt32();

        var eventKey = $"probe-{Guid.NewGuid():N}";
        await using (var arrange = NewDb())
        {
            arrange.NotificationRules.Add(new NotificationRule(NotificationEventKind.JobFailed, id, NotificationSeverity.Error));
            arrange.NotificationRules.Add(new NotificationRule(NotificationEventKind.ExportFailed, id, NotificationSeverity.Warning));
            arrange.NotificationDeliveries.Add(new NotificationDelivery(
                DateTime.UtcNow, id, NotificationEventKind.JobFailed, eventKey, NotificationDeliveryStatus.Sent));
            await arrange.SaveChangesAsync().ConfigureAwait(true);
        }

        var removed = await client.DeleteAsync(At($"{id}")).ConfigureAwait(true);
        Assert.True(removed.StatusCode == HttpStatusCode.NoContent, $"{removed.StatusCode}: {app.ErrorsText}");

        await using var db = NewDb();
        Assert.False(await db.NotificationChannels.AnyAsync(c => c.Id == id).ConfigureAwait(true));
        Assert.False(await db.NotificationRules.AnyAsync(r => r.ChannelId == id).ConfigureAwait(true));
        Assert.True(await db.NotificationDeliveries.AnyAsync(d => d.EventKey == eventKey).ConfigureAwait(true));

        var again = await client.DeleteAsync(At($"{id}")).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    /// <summary>
    /// Транспорт SMTP крізь справжній HTTP: поля сервера не приймаються, а у
    /// видачі видно, звідки він береться.
    /// </summary>
    /// <remarks>
    /// ⚠ Тут — саме конвеєр: код, <c>messageKey</c> і те, що в <c>SettingsJson</c>
    /// бази не лишилося транспорту. Обробник це вже стереже на подвійниках;
    /// що відмова доїхала до клієнта саме <c>422</c>, а не <c>400</c> від
    /// прив'язувача моделі, видно лише звідси.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-33")]
    public async Task Сервер_SMTP_у_налаштуваннях_каналу_дає_422_а_відповідь_каже_звідки_транспорт()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "System.ManageNotifications").ConfigureAwait(true);

        var name = $"mail-{Guid.NewGuid():N}";
        var refused = await client.PostAsJsonAsync(
            Channels,
            new { kind = "Smtp", name, settings = new { host = "mail.corp.example", port = 25, recipients = Recipients } })
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = await BodyAsync(refused, null).ConfigureAwait(true);
        Assert.Equal("ECR-REQ-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal(
            "err.ECR-REQ-0422.notificationChannelTransportFromConfiguration",
            problem.GetProperty("messageKey").GetString());

        // Без транспорту — той самий канал створюється, і у відповіді видно,
        // що сервер задає застосунок: екрану не треба це вгадувати.
        var created = await client.PostAsJsonAsync(
            Channels, new { kind = "Smtp", name, settings = new { recipients = Recipients } }).ConfigureAwait(true);
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"{created.StatusCode}: {app.ErrorsText}");

        var body = await BodyAsync(created, null).ConfigureAwait(true);
        Assert.True(body.GetProperty("transportFromConfiguration").GetBoolean());
        Assert.False(body.GetProperty("settings").TryGetProperty("host", out _));

        await using var db = NewDb();
        var stored = await db.NotificationChannels.AsNoTracking()
            .SingleAsync(c => c.Id == body.GetProperty("id").GetInt32()).ConfigureAwait(true);
        Assert.DoesNotContain("mail.corp.example", stored.SettingsJson, StringComparison.Ordinal);
    }

    /// <summary>Фікстура не задає <c>Smtp:Host</c> — рівно свіже встановлення.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-33")]
    public async Task Без_Smtp_Host_поштовий_канал_не_налаштований_а_Teams_налаштований_лише_з_адресою()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "System.ManageNotifications").ConfigureAwait(true);

        var mail = await CreateAsync(client, app, "Smtp", new { recipients = Recipients }).ConfigureAwait(true);
        var teams = await CreateAsync(client, app, "TeamsWebhook", null).ConfigureAwait(true);
        Assert.False(mail.GetProperty("transportConfigured").GetBoolean());
        Assert.False(teams.GetProperty("transportConfigured").GetBoolean());

        var id = teams.GetProperty("id").GetInt32();
        var replaced = await client.PutAsJsonAsync(
            At($"{id}/secret"), new { secret = "https://prod-17.westeurope.logic.azure.com/workflows/abc" })
            .ConfigureAwait(true);
        Assert.True((await BodyAsync(replaced, null).ConfigureAwait(true)).GetProperty("transportConfigured").GetBoolean());

        var listed = await BodyAsync(await client.GetAsync(Channels).ConfigureAwait(true), null).ConfigureAwait(true);
        Assert.False(Row(listed, mail).GetProperty("transportConfigured").GetBoolean());
        Assert.True(Row(listed, teams).GetProperty("transportConfigured").GetBoolean());
    }

    /// <summary>
    /// Налаштований транспорт — справжній <c>SmtpNotificationSender</c> на
    /// власній конфігурації; у відповідь іде лише булеве, без хоста й адресанта.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-33")]
    public async Task З_Smtp_Host_поштовий_канал_налаштований_а_подробиць_транспорту_у_відповіді_немає()
    {
        const string host = "smtp-probe-7f3a.corp.example";
        const string from = "ecr-probe-7f3a@corp.example";
        var smtp = new ConfigurationBuilder()
            .AddInMemoryCollection([new("Smtp:Host", host), new("Smtp:From", from)]).Build();

        using var app = new EcrApiFactory(sql);
        using var configured = app.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<INotificationSender>(
            sp => new SmtpNotificationSender(smtp, sp.GetRequiredService<ISecretProvider>()))));
        using var client = await SystemHealthControllerTests
            .SignedInAsync(sql, configured, "System.ManageNotifications").ConfigureAwait(true);

        var created = await client.PostAsJsonAsync(
            Channels, new { kind = "Smtp", name = $"mail-{Guid.NewGuid():N}", settings = new { recipients = Recipients } })
            .ConfigureAwait(true);
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"{created.StatusCode}: {app.ErrorsText}");
        var mail = await BodyAsync(created, null).ConfigureAwait(true);
        Assert.True(mail.GetProperty("transportConfigured").GetBoolean());

        var text = await client.GetStringAsync(Channels).ConfigureAwait(true);
        Assert.True(Row(JsonDocument.Parse(text).RootElement, mail).GetProperty("transportConfigured").GetBoolean());
        Assert.DoesNotContain("probe-7f3a", text + mail.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Одна ознака — два споживачі крізь HTTP: <c>/health/facts</c> і
    /// <c>transportConfigured</c> каналу. Хост без адресанта (і навпаки) — «не
    /// налаштовано», бо відправити так не можна.
    /// </summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-33")]
    [InlineData("smtp.corp.example", "ecr@corp.example", true)]
    [InlineData("smtp.corp.example", null, false)]
    [InlineData(null, "ecr@corp.example", false)]
    public async Task Налаштовано_лише_з_Host_і_From_і_це_кажуть_факти_стану_й_канал(
        string? host, string? from, bool expected)
    {
        var smtp = new ConfigurationBuilder()
            .AddInMemoryCollection([new("Smtp:Host", host), new("Smtp:From", from)]).Build();

        using var app = new EcrApiFactory(sql);
        using var configured = app.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<INotificationSender>(
            sp => new SmtpNotificationSender(smtp, sp.GetRequiredService<ISecretProvider>()))));
        using var client = await SystemHealthControllerTests
            .SignedInAsync(sql, configured, "System.ManageNotifications", "System.ViewHealth").ConfigureAwait(true);

        var facts = await BodyAsync(
            await client.GetAsync(new Uri("/api/v1/health/facts", UriKind.Relative)).ConfigureAwait(true), null)
            .ConfigureAwait(true);
        Assert.Equal(expected, facts.GetProperty("notificationTransport").GetProperty("isConfigured").GetBoolean());

        var mail = await CreateAsync(client, app, "Smtp", new { recipients = Recipients }).ConfigureAwait(true);
        Assert.Equal(expected, mail.GetProperty("transportConfigured").GetBoolean());
    }

    private async Task<JsonElement> CreateAsync(HttpClient client, EcrApiFactory app, string kind, object? settings)
    {
        var created = await client.PostAsJsonAsync(
            Channels, new { kind, name = $"{kind}-{Guid.NewGuid():N}", settings }).ConfigureAwait(false);
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"{created.StatusCode}: {app.ErrorsText}");

        return await BodyAsync(created, null).ConfigureAwait(false);
    }

    private static JsonElement Row(JsonElement listed, JsonElement channel)
        => listed.EnumerateArray().Single(c => c.GetProperty("id").GetInt32() == channel.GetProperty("id").GetInt32());

    private static Uri At(string tail) => new($"{Channels}/{tail}", UriKind.Relative);

    private EcrDbContext NewDb()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response, StringBuilder? seen)
    {
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        seen?.Append(text);

        return JsonDocument.Parse(text).RootElement;
    }

    /// <summary>Клієнт із сеансом локального користувача з одним правом.</summary>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, string permission)
    {
        var name = $"ntf_{Guid.NewGuid():N}"[..20];

        await using (var db = NewDb())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            var role = new Role(
                Ecr.Domain.ValueObjects.EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                new Ecr.Domain.ValueObjects.LocalizedText(new Dictionary<string, string> { ["en"] = "Notify test" }));
            db.Users.Add(user);
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, permission));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }
}
