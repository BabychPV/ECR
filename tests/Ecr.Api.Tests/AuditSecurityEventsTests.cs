// tests/Ecr.Api.Tests/AuditSecurityEventsTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>GET /api/v1/audit/security</c> (ФВ-5.24, ФВ-6.11): читач журналу подій безпеки — вікно, фільтри,
/// курсор, право; відмова в доступі (403) сама потрапляє в журнал і видна аудитору.
/// </summary>
/// <remarks>
/// ⚠ Рядки вставляються прямим SQL (<c>aud.*</c> поза моделлю EF); кожен тест працює у власному
/// просторі значень (випадковий тип події й автор) — база спільна для набору.
/// </remarks>
[Collection("SqlServer")]
public sealed class AuditSecurityEventsTests(SqlServerFixture sql)
{
    private const string Password = "Api-Audit-Security-2026!";

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task Фільтри_типу_події_й_автора_звужують_видачу_кожен_окремо()
    {
        using var app = new EcrApiFactory(sql);
        var (client, _) = await LoginAsync(app, "a", ["Security.ViewAudit"]);

        var typeA = $"T{_tag}A";
        var typeB = $"T{_tag}B";
        var author = Random.Shared.Next(1_000_000, int.MaxValue - 1);

        await WriteAsync(typeA, author, "{\"route\":\"x\"}");
        await WriteAsync(typeA, author + 1);
        await WriteAsync(typeB, author);

        // ⛔ Мутація: прибрати `AND EventType = @eventType` — приїде і `typeB`.
        var byType = await ReadAsync(client, app, $"eventType={typeA}");
        Assert.Equal(2, byType.Count);
        Assert.All(byType, i => Assert.Equal(typeA, i.GetProperty("eventType").GetString()));
        Assert.Contains(byType, i => i.GetProperty("detailsJson").GetString() == "{\"route\":\"x\"}");

        // ⛔ Мутація: прибрати `AND ChangedByUserId = @changedBy` — приїдуть чужі рядки (фільтр без типу).
        var byAuthor = await ReadAsync(client, app, $"changedByUserId={author}");
        Assert.Equal(2, byAuthor.Count);

        Assert.Empty(await ReadAsync(client, app, $"eventType={typeB}&changedByUserId={author + 1}"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task Курсор_віддає_наступну_сторінку_без_повторів_і_закінчується()
    {
        using var app = new EcrApiFactory(sql);
        var (client, _) = await LoginAsync(app, "p", ["Security.ViewAudit"]);

        var type = $"T{_tag}P";
        await WriteAsync(type, 1, "first");
        await WriteAsync(type, 1, "second");

        var first = await PageAsync(client, $"eventType={type}", limit: 1);
        // Типовий порядок - найновіші першими: "second" записано пізніше.
        Assert.Equal("second", Assert.Single(Items(first)).GetProperty("detailsJson").GetString());

        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);

        // ⛔ Мутація: прибрати `AND Id > @after` — друга сторінка знову віддасть «first».
        var second = await PageAsync(client, $"eventType={type}&cursor={Uri.EscapeDataString(cursor)}", limit: 1);
        Assert.Equal("first", Assert.Single(Items(second)).GetProperty("detailsJson").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task Запит_без_вікна_або_з_вікном_понад_стелю_відхиляється()
    {
        using var app = new EcrApiFactory(sql);
        var (client, _) = await LoginAsync(app, "w", ["Security.ViewAudit"]);

        // ⛔ Мутація: прибрати з обробника `To <= From` — запит без вікна стане 200.
        var noWindow = await client.GetAsync(new Uri("/api/v1/audit/security?limit=50", UriKind.Relative));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noWindow.StatusCode);

        // ⛔ Мутація: прибрати перевірку `> MaxWindow` — рік читав би кожну партицію.
        var to = DateTime.UtcNow;
        var tooWide = await client.GetAsync(new Uri(Url(string.Empty, to.AddDays(-365), to, 50), UriKind.Relative));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooWide.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task Відмова_в_доступі_видна_аудитору_а_без_права_журнал_не_віддається()
    {
        using var app = new EcrApiFactory(sql);
        var (auditor, _) = await LoginAsync(app, "au", ["Security.ViewAudit"]);
        var (intruder, intruderId) = await LoginAsync(app, "in", ["Template.Edit"]);

        // ⛔ Мутація: прибрати з обробника перевірку права — стане 200 (комплаєнс-журнал бачив би кожен).
        var denied = await intruder.GetAsync(new Uri(Url(string.Empty), UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // Сама відмова — подія `AccessDenied` з автором-порушником (запис іде в тому ж запиті; чекаємо ≤ 5 с).
        List<JsonElement> events = [];
        for (var attempt = 0; attempt < 25 && events.Count == 0; attempt++)
        {
            events = await ReadAsync(auditor, app, $"eventType=AccessDenied&changedByUserId={intruderId}");
            if (events.Count == 0)
            {
                await Task.Delay(200);
            }
        }

        var evt = Assert.Single(events);
        Assert.Equal(intruderId, evt.GetProperty("changedByUserId").GetInt32());
        Assert.Contains("audit/security",evt.GetProperty("detailsJson").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task Типи_подій_у_вікні_для_вибору_у_фільтрі_і_той_самий_поріг_права()
    {
        // ✎ P-1124-4: «Event type» був вільним текстом із ТОЧНИМ збігом коду — людина не знає `AccessDenied`.
        using var app = new EcrApiFactory(sql);
        var (auditor, _) = await LoginAsync(app, "et", ["Security.ViewAudit"]);
        var (stranger, _) = await LoginAsync(app, "en", ["Template.Edit"]);

        var inside = $"T{_tag}E";
        await WriteAsync(inside, 1);
        await WriteAsync(inside, 2);

        var from = DateTime.UtcNow.AddDays(-1);
        var to = DateTime.UtcNow.AddMinutes(1);
        var response = await auditor.GetAsync(new Uri(TypesUrl(from, to), UriKind.Relative));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {app.ErrorsText}");

        // ⛔ Мутація: прибрати `DISTINCT` — тип прийде двічі.
        var types = (await response.Content.ReadFromJsonAsync<List<string>>())!;
        Assert.Single(types, t => t == inside);

        // ⛔ Мутація: прибрати вікно з запиту — тип із вікна в минулому теж приїде.
        var past = await auditor.GetAsync(new Uri(TypesUrl(from.AddDays(-30), from.AddDays(-29)), UriKind.Relative));
        Assert.DoesNotContain(inside, (await past.Content.ReadFromJsonAsync<List<string>>())!);

        // ⛔ Мутація: прибрати перевірку права в `EventTypesAsync` — стане 200.
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync(new Uri(TypesUrl(from, to), UriKind.Relative))).StatusCode);

        // ⛔ Мутація: прибрати `AuditWindow.Require` — рік читав би кожну партицію.
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await auditor.GetAsync(new Uri(TypesUrl(to.AddDays(-365), to), UriKind.Relative))).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task Автори_для_фільтра_за_правом_аудиту_без_ManageUsers_і_без_логіна()
    {
        // ✎ P-1124-4: «By user» брав перелік із `GET /users` (лише `Security.ManageUsers`) — в аудитора порожньо.
        using var app = new EcrApiFactory(sql);
        var (auditor, auditorId) = await LoginAsync(app, "ab", ["Security.ViewAudit"]);
        var (stranger, _) = await LoginAsync(app, "an", ["Template.Edit"]);

        var response = await auditor.GetAsync(new Uri("/api/v1/audit/authors", UriKind.Relative));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {app.ErrorsText}");

        var authors = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        var self = Assert.Single(authors, a => a.GetProperty("userId").GetInt32() == auditorId);
        Assert.Equal($"audse_ab_{_tag}", self.GetProperty("displayName").GetString());

        // ⛔ R-A2/D-86: номер, ім'я й поле логіна — для унікального імені порожнє (AC-7); SID і e-mail не віддаються.
        Assert.Equal(
            ["displayName", "login", "userId"], self.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.Null, self.GetProperty("login").ValueKind);

        // ⛔ Мутація: прибрати перевірку права в `AuthorsAsync` — стане 200 (перелік людей кожному).
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await stranger.GetAsync(new Uri("/api/v1/audit/authors", UriKind.Relative))).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task Автори_з_однаковим_ім_ям_отримують_логін_а_унікальні_ні_за_тим_самим_правом()
    {
        // ✎ AC-7 (audit-filters-contract): однакові імена (тимчасові акаунти вікон) у «By user» розрізнялись лише «#id».
        using var app = new EcrApiFactory(sql);
        var (auditor, auditorId) = await LoginAsync(app, "ac", ["Security.ViewAudit"]);
        var (manager, _) = await LoginAsync(app, "am", ["Security.ManageUsers"]);

        var twin = $"Twin {_tag}";
        var first = await AddUserAsync($"audtw1_{_tag}", twin);
        var second = await AddUserAsync($"audtw2_{_tag}", twin);
        var unique = await AddUserAsync($"audun_{_tag}", $"Unique {_tag}");

        // Ім'я, що відрізняється лише регістром, — не збіг: клієнт розрізняє такі імена й без логіна.
        var cased = await AddUserAsync($"audca_{_tag}", $"twin {_tag}");

        var response = await auditor.GetAsync(new Uri("/api/v1/audit/authors", UriKind.Relative));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {app.ErrorsText}");
        var body = await response.Content.ReadAsStringAsync();
        var authors = JsonDocument.Parse(body).RootElement.EnumerateArray()
            .ToDictionary(a => a.GetProperty("userId").GetInt32());

        // ⛔ Мутація: прибрати відсів за збігом у `WithLoginsOnCollision` (логін кожному) — унікальні отримають логін.
        Assert.Equal($"audtw1_{_tag}", authors[first].GetProperty("login").GetString());
        Assert.Equal($"audtw2_{_tag}", authors[second].GetProperty("login").GetString());
        Assert.Equal(JsonValueKind.Null, authors[unique].GetProperty("login").ValueKind);
        Assert.Equal(JsonValueKind.Null, authors[cased].GetProperty("login").ValueKind);
        Assert.Equal(JsonValueKind.Null, authors[auditorId].GetProperty("login").ValueKind);
        Assert.DoesNotContain($"audun_{_tag}", body, StringComparison.Ordinal);
        Assert.DoesNotContain($"audca_{_tag}", body, StringComparison.Ordinal);

        // Право не змінилося: без `Security.ViewAudit` — 403, і `Security.ManageUsers` його не замінює.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await manager.GetAsync(new Uri("/api/v1/audit/authors", UriKind.Relative))).StatusCode);
    }

    /// <summary>Користувач без ролей (локальний — з паролем, <c>CK_User_Provider</c>) — лише ім'я й логін у переліку авторів.</summary>
    private async Task<int> AddUserAsync(string login, string displayName)
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

        var user = new User(login, displayName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return user.Id;
    }

    private static string TypesUrl(DateTime from, DateTime to)
        => "/api/v1/audit/security/event-types"
           + $"?from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}"
           + $"&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}";

    private static string Url(string filters)
        => Url(filters, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddMinutes(1), 50);

    private static string Url(string filters, DateTime from, DateTime to, int limit)
        => "/api/v1/audit/security"
           + $"?from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}"
           + $"&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}"
           + $"&limit={limit.ToString(CultureInfo.InvariantCulture)}"
           + (filters.Length == 0 ? string.Empty : "&" + filters);

    private static async Task<List<JsonElement>> ReadAsync(HttpClient client, EcrApiFactory app, string filters)
    {
        var response = await client.GetAsync(new Uri(Url(filters), UriKind.Relative));
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()} {app.ErrorsText}");

        return Items(await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private static async Task<JsonElement> PageAsync(HttpClient client, string filters, int limit)
    {
        var url = Url(filters, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddMinutes(1), limit);
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<JsonElement> Items(JsonElement page)
        => page.GetProperty("items").EnumerateArray().ToList();

    private async Task WriteAsync(string eventType, int author, string? details = null)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO aud.SecurityEvent (ChangedAt, EventType, DetailsJson, ChangedByUserId)
            VALUES (@changedAt, @type, @details, @author);
            """;
        command.Parameters.AddWithValue("@changedAt", DateTime.UtcNow);
        command.Parameters.AddWithValue("@type", eventType);
        command.Parameters.AddWithValue("@details", (object?)details ?? DBNull.Value);
        command.Parameters.AddWithValue("@author", author);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Роль із правами → користувач → вхід; повертає клієнта й <c>UserId</c>.</summary>
    private async Task<(HttpClient Client, int UserId)> LoginAsync(EcrApiFactory app, string who, string[] permissions)
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

        var role = new Role(
            EcrCode.Create($"AUDSE_{who}_{_tag}".ToUpperInvariant()),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Role" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        foreach (var permission in permissions)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        var name = $"audse_{who}_{_tag}";
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
        await db.SaveChangesAsync();

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return (client, user.Id);
    }
}
