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
        Assert.Equal("first", Assert.Single(Items(first)).GetProperty("detailsJson").GetString());

        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);

        // ⛔ Мутація: прибрати `AND Id > @after` — друга сторінка знову віддасть «first».
        var second = await PageAsync(client, $"eventType={type}&cursor={Uri.EscapeDataString(cursor)}", limit: 1);
        Assert.Equal("second", Assert.Single(Items(second)).GetProperty("detailsJson").GetString());
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
