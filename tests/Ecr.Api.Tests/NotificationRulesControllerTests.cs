// tests/Ecr.Api.Tests/NotificationRulesControllerTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Notifications;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Правила сповіщень і журнал доставок крізь справжній HTTP і справжню базу
/// (<c>BE-33</c>): право, ідемпотентність <c>PUT</c>, стеля сторінки журналу,
/// відсутність секрету в журналі.
/// </summary>
[Collection("SqlServer")]
public sealed class NotificationRulesControllerTests(SqlServerFixture sql)
{
    private const string Password = "Api-Rules-Probe-2026!";
    private const string Marker = "DeliverySig-93b1";
    private static readonly string[] Recipients = ["ops@corp.example"];
    private static readonly Uri Rules = new("/api/v1/notifications/rules", UriKind.Relative);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-33")]
    public async Task Без_права_403_на_матриці_і_на_журналі()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "System.ViewHealth").ConfigureAwait(true);

        HttpResponseMessage[] responses =
        [
            await client.GetAsync(Rules).ConfigureAwait(true),
            await client.PutAsJsonAsync(Rules, new { rules = Array.Empty<object>() }).ConfigureAwait(true),
            await client.GetAsync(Deliveries(50)).ConfigureAwait(true),
        ];

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-33")]
    public async Task Матриця_віддає_всі_види_подій_а_та_сама_заміна_двічі_не_дублює_правил()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "System.ManageNotifications").ConfigureAwait(true);
        var id = await NewChannelAsync(app, client).ConfigureAwait(true);

        var matrix = await BodyAsync(await client.GetAsync(Rules).ConfigureAwait(true)).ConfigureAwait(true);

        // ⛔ Вісь подій повна навіть тоді, коли правил немає: порожня клітинка
        // означає «правила немає», а не «події не буває».
        Assert.Equal(5, matrix.GetProperty("eventKinds").GetArrayLength());

        var body = new
        {
            rules = new[]
            {
                new { eventKind = "JobFailed", channelId = id, minSeverity = "Warning", isEnabled = true },
                new { eventKind = "ExportFailed", channelId = id, minSeverity = "Error", isEnabled = false },
            },
        };

        var first = await client.PutAsJsonAsync(Rules, body).ConfigureAwait(true);
        Assert.True(first.StatusCode == HttpStatusCode.OK, $"{first.StatusCode}: {app.ErrorsText}");
        var second = await client.PutAsJsonAsync(Rules, body).ConfigureAwait(true);
        Assert.True(second.StatusCode == HttpStatusCode.OK, $"{second.StatusCode}: {app.ErrorsText}");

        // ⛔ Головне твердження: той самий вхід двічі — той самий стан.
        // Дубль тут не «зайвий рядок», а друга доставка на кожну подію.
        await using (var db = NewDb())
        {
            Assert.Equal(2, await db.NotificationRules.CountAsync(r => r.ChannelId == id).ConfigureAwait(true));
        }

        var after = await BodyAsync(await client.GetAsync(Rules).ConfigureAwait(true)).ConfigureAwait(true);
        var mine = after.GetProperty("rules").EnumerateArray()
            .Where(r => r.GetProperty("channelId").GetInt32() == id).ToList();
        Assert.Equal(2, mine.Count);
        Assert.Equal(
            "Warning",
            mine.Single(r => r.GetProperty("eventKind").GetString() == "JobFailed")
                .GetProperty("minSeverity").GetString());

        // Клітинка, якої в новій матриці немає, зникає — це заміна, не доповнення.
        await client.PutAsJsonAsync(Rules, new { rules = body.rules[..1] }).ConfigureAwait(true);

        await using var final = NewDb();
        var rule = await final.NotificationRules.AsNoTracking()
            .SingleAsync(r => r.ChannelId == id).ConfigureAwait(true);
        Assert.Equal(NotificationEventKind.JobFailed, rule.EventKind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-33")]
    public async Task Правило_на_неіснуючий_канал_дає_404_а_дві_клітинки_на_пару_422()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "System.ManageNotifications").ConfigureAwait(true);
        var id = await NewChannelAsync(app, client).ConfigureAwait(true);

        var missing = await client.PutAsJsonAsync(
            Rules,
            new { rules = new[] { new { eventKind = "JobFailed", channelId = id + 100_000, minSeverity = "Info", isEnabled = true } } })
            .ConfigureAwait(true);

        var twice = await client.PutAsJsonAsync(
            Rules,
            new
            {
                rules = new[]
                {
                    new { eventKind = "JobFailed", channelId = id, minSeverity = "Info", isEnabled = true },
                    new { eventKind = "JobFailed", channelId = id, minSeverity = "Error", isEnabled = false },
                },
            }).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(
            "err.ECR-INT-0404.notificationChannel",
            (await BodyAsync(missing).ConfigureAwait(true)).GetProperty("messageKey").GetString());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, twice.StatusCode);
        Assert.Equal(
            "err.ECR-REQ-0422.notificationRuleInvalid",
            (await BodyAsync(twice).ConfigureAwait(true)).GetProperty("messageKey").GetString());

        // Жодна з відмов не лишила по собі половини матриці.
        await using var db = NewDb();
        Assert.False(await db.NotificationRules.AnyAsync(r => r.ChannelId == id).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-33")]
    public async Task Журнал_доставок_відхиляє_сторінку_понад_двісті_і_не_показує_ні_секрету_ні_тіла()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "System.ManageNotifications").ConfigureAwait(true);
        var id = await NewChannelAsync(app, client).ConfigureAwait(true);

        var secret = await client
            .PutAsJsonAsync(
                new Uri($"/api/v1/notifications/channels/{id}/secret", UriKind.Relative),
                new { secret = $"https://prod.logic.azure.com/workflows/x?sig={Marker}" })
            .ConfigureAwait(true);
        Assert.True(secret.StatusCode == HttpStatusCode.OK, $"{secret.StatusCode}: {app.ErrorsText}");

        var eventKey = $"delivery-{Guid.NewGuid():N}";
        await using (var arrange = NewDb())
        {
            arrange.NotificationDeliveries.Add(new NotificationDelivery(
                DateTime.UtcNow, id, NotificationEventKind.CollectionFailed, eventKey,
                NotificationDeliveryStatus.Failed, "relay refused"));
            await arrange.SaveChangesAsync().ConfigureAwait(true);
        }

        // ⚠ 201 літералом, не `MaxLimit + 1`: стеля 200 — це вимога, і
        // твердження проти константи поїхало б разом із нею.
        var tooBig = await client.GetAsync(Deliveries(201)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooBig.StatusCode);
        Assert.Equal(
            "err.ECR-REQ-0422.pageSizeOutOfRange",
            (await BodyAsync(tooBig).ConfigureAwait(true)).GetProperty("messageKey").GetString());

        var response = await client.GetAsync(Deliveries(200)).ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {app.ErrorsText}");

        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        var row = JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray()
            .Single(d => d.GetProperty("eventKey").GetString() == eventKey);

        Assert.Equal("Failed", row.GetProperty("status").GetString());
        Assert.Equal("relay refused", row.GetProperty("error").GetString());
        Assert.False(string.IsNullOrEmpty(row.GetProperty("channelName").GetString()));

        // ⛔ Ні значення секрету, ні поля під тіло повідомлення в журналі бути
        // не може: цей перелік бачить кожен, хто керує сповіщеннями.
        Assert.DoesNotContain(Marker, text, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "message", "body", "payload", "secret", "settings" })
        {
            Assert.False(row.TryGetProperty(forbidden, out _), $"Журнал віддає «{forbidden}».");
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-33")]
    public async Task Журнал_доставок_звужується_каналом_і_підсумком_а_невідомий_підсумок_дає_422()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "System.ManageNotifications").ConfigureAwait(true);
        var mine = await NewChannelAsync(app, client).ConfigureAwait(true);
        var other = await NewChannelAsync(app, client).ConfigureAwait(true);

        var tag = Guid.NewGuid().ToString("N");
        await using (var arrange = NewDb())
        {
            arrange.NotificationDeliveries.AddRange(
                new NotificationDelivery(
                    DateTime.UtcNow, mine, NotificationEventKind.JobFailed, $"sent-{tag}",
                    NotificationDeliveryStatus.Sent),
                new NotificationDelivery(
                    DateTime.UtcNow, mine, NotificationEventKind.JobFailed, $"failed-{tag}",
                    NotificationDeliveryStatus.Failed, "relay refused"),
                new NotificationDelivery(
                    DateTime.UtcNow, other, NotificationEventKind.ExportFailed, $"other-{tag}",
                    NotificationDeliveryStatus.Failed, "relay refused"));
            await arrange.SaveChangesAsync().ConfigureAwait(true);
        }

        var byChannel = await KeysAsync(app, client, Deliveries(200, $"&channelId={mine}")).ConfigureAwait(true);

        // ⛔ Головне твердження: чужого рядка у видачі НЕМАЄ. Саме цим фільтр
        // відрізняється від підказки — журнал спільний на всі канали, і
        // «зайвий рядок» тут означає чужу доставку в шухляді не того каналу.
        Assert.Contains($"sent-{tag}", byChannel);
        Assert.Contains($"failed-{tag}", byChannel);
        Assert.DoesNotContain($"other-{tag}", byChannel);

        var byBoth = await KeysAsync(app, client, Deliveries(200, $"&channelId={mine}&status=Failed"))
            .ConfigureAwait(true);
        Assert.Equal([$"failed-{tag}"], byBoth.Where(k => k.EndsWith(tag, StringComparison.Ordinal)));

        var byStatus = await KeysAsync(app, client, Deliveries(200, "&status=Sent")).ConfigureAwait(true);
        Assert.Contains($"sent-{tag}", byStatus);
        Assert.DoesNotContain($"failed-{tag}", byStatus);

        // Невідомий підсумок — 422 наявним кодом, а не мовчазне «усі».
        var unknown = await client.GetAsync(Deliveries(50, "&status=Delivered")).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Equal(
            "err.ECR-REQ-0422.notificationDeliveryStatus",
            (await BodyAsync(unknown).ConfigureAwait(true)).GetProperty("messageKey").GetString());
    }

    /// <summary>Ключі подій зі сторінки журналу; відмова — це падіння тесту.</summary>
    private static async Task<List<string>> KeysAsync(EcrApiFactory app, HttpClient client, Uri path)
    {
        var response = await client.GetAsync(path).ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {app.ErrorsText}");

        return [.. (await BodyAsync(response).ConfigureAwait(false)).GetProperty("items").EnumerateArray()
            .Select(d => d.GetProperty("eventKey").GetString() ?? string.Empty)];
    }

    private static Uri Deliveries(int limit, string query = "")
        => new($"/api/v1/notifications/deliveries?limit={limit}{query}", UriKind.Relative);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;

    private EcrDbContext NewDb()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    /// <summary>Заводить канал штатним маршрутом і повертає його ідентифікатор.</summary>
    private static async Task<int> NewChannelAsync(EcrApiFactory app, HttpClient client)
    {
        var created = await client.PostAsJsonAsync(
            new Uri("/api/v1/notifications/channels", UriKind.Relative),
            new
            {
                kind = "Smtp",
                name = $"rules-{Guid.NewGuid():N}",
                settings = new { host = "mail.corp.example", port = 25, recipients = Recipients },
            }).ConfigureAwait(false);

        Assert.True(created.StatusCode == HttpStatusCode.Created, $"{created.StatusCode}: {app.ErrorsText}");

        return (await BodyAsync(created).ConfigureAwait(false)).GetProperty("id").GetInt32();
    }

    /// <summary>Клієнт із сеансом локального користувача з одним правом.</summary>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, string permission)
    {
        var name = $"nrl_{Guid.NewGuid():N}"[..20];

        await using (var db = NewDb())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            var role = new Role(
                Ecr.Domain.ValueObjects.EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                new Ecr.Domain.ValueObjects.LocalizedText(new Dictionary<string, string> { ["en"] = "Rules test" }));
            db.Users.Add(user);
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, permission));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password })
            .ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }
}
