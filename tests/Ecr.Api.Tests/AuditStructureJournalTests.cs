// tests/Ecr.Api.Tests/AuditStructureJournalTests.cs
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
/// `BE-16`: загальний журнал структурних змін — вікно, фільтри, курсор, право.
/// </summary>
/// <remarks>
/// ⛔ Рядки вставляються ПРЯМИМ SQL з тієї самої причини, що в
/// <see cref="AuditCellFiltersTests"/>: предмет — ЧИТАННЯ з фільтром, а
/// <c>aud.*</c> живуть поза моделлю EF.
///
/// ⚠ База спільна для всього набору, тому кожен тест працює у ВЛАСНОМУ
/// просторі значень: тип сутності з випадковим тегом і випадковий автор. Інакше
/// «рівно два рядки» залежало б від порядку прогону сусідів.
/// </remarks>
[Collection("SqlServer")]
public sealed partial class AuditStructureJournalTests(SqlServerFixture sql)
{
    private const string Password = "Api-Audit-Structure-2026!";

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-16")]
    public async Task Фільтри_типу_сутності_й_автора_звужують_видачу_кожен_окремо()
    {
        using var app = new EcrApiFactory(sql);
        var client = await LoginAsync(app, ["Security.ViewAudit"]);

        var typeA = $"t.{_tag}.A";
        var typeB = $"t.{_tag}.B";
        var author = Random.Shared.Next(1_000_000, int.MaxValue);

        await WriteAsync(typeA, author);
        await WriteAsync(typeA, author + 1);
        await WriteAsync(typeB, author);

        // ⛔ Мутація: прибрати `AND EntityType = @entityType` — приїде весь
        // журнал вікна, зокрема рядок `typeB`.
        var byType = await ReadAsync(client, app, $"entityType={typeA}");
        Assert.Equal(2, byType.Count);
        Assert.All(byType, i => Assert.Equal(typeA, i.GetProperty("entityType").GetString()));

        // ⛔ Мутація: прибрати `AND ChangedByUserId = @changedBy` — приїдуть і
        // чужі рядки. Фільтр стоїть САМ, без типу: інакше тип приховав би мутацію.
        var byAuthor = await ReadAsync(client, app, $"changedByUserId={author}");
        Assert.Equal(2, byAuthor.Count);
        Assert.All(byAuthor, i => Assert.Equal(author, i.GetProperty("changedByUserId").GetInt32()));

        // ⚠ Разом — кон'юнкція, а не об'єднання.
        var both = await ReadAsync(client, app, $"entityType={typeB}&changedByUserId={author + 1}");
        Assert.Empty(both);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-16")]
    public async Task Курсор_віддає_наступну_сторінку_без_повторів_і_закінчується()
    {
        using var app = new EcrApiFactory(sql);
        var client = await LoginAsync(app, ["Security.ViewAudit"]);

        var type = $"t.{_tag}.P";
        await WriteAsync(type, 1, operation: "First");
        await WriteAsync(type, 1, operation: "Second");

        var first = await PageAsync(client, $"entityType={type}", limit: 1);
        Assert.Equal("First", Assert.Single(Items(first)).GetProperty("operation").GetString());

        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);

        // ⛔ Мутація: прибрати `AND Id > @after` — друга сторінка знову віддасть
        // «First», і «показати ще» гортало б по колу.
        var second = await PageAsync(
            client, $"entityType={type}&cursor={Uri.EscapeDataString(cursor)}", limit: 1);
        Assert.Equal("Second", Assert.Single(Items(second)).GetProperty("operation").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-16")]
    public async Task Запит_без_вікна_або_з_вікном_понад_стелю_відхиляється()
    {
        using var app = new EcrApiFactory(sql);
        var client = await LoginAsync(app, ["Security.ViewAudit"]);

        // ⛔ Мутація: прибрати з обробника `To <= From` — запит без вікна стане
        // 200 з порожньою видачею, тобто «змін не було» замість «ви не спитали».
        var noWindow = await client
            .GetAsync(new Uri("/api/v1/audit/structure?limit=50", UriKind.Relative))
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noWindow.StatusCode);

        // ⛔ Мутація: прибрати перевірку `> MaxWindow` — рік стане 200 і читатиме
        // кожну партицію `ps_AuditByMonth`.
        var to = DateTime.UtcNow;
        var tooWide = await client
            .GetAsync(new Uri(Url(string.Empty, to.AddDays(-365), to, 50), UriKind.Relative))
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooWide.StatusCode);

        var problem = JsonDocument
            .Parse(await tooWide.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal("ECR-REQ-0422", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-16")]
    public async Task Без_Security_ViewAudit_журнал_не_віддається()
    {
        using var app = new EcrApiFactory(sql);

        // ⚠ `Template.Edit` — право людини, яка структуру ЗМІНЮЄ. Воно не дає
        // читати, хто ще її змінював: журнал — комплаєнс-право (Q-177).
        var client = await LoginAsync(app, ["Template.Edit"]);

        var response = await client
            .GetAsync(new Uri(Url(string.Empty), UriKind.Relative))
            .ConfigureAwait(true);

        // ⛔ Мутація: прибрати з обробника `!profile.Has(Permission)` — стане 200.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static string Url(string filters)
        => Url(filters, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddMinutes(1), 50);

    private static string Url(string filters, DateTime from, DateTime to, int limit)
        => "/api/v1/audit/structure"
           + $"?from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}"
           + $"&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}"
           + $"&limit={limit.ToString(CultureInfo.InvariantCulture)}"
           + (filters.Length == 0 ? string.Empty : "&" + filters);

    private static async Task<List<JsonElement>> ReadAsync(HttpClient client, EcrApiFactory app, string filters)
    {
        var response = await client.GetAsync(new Uri(Url(filters), UriKind.Relative)).ConfigureAwait(true);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync().ConfigureAwait(true)} {app.ErrorsText}");

        return Items(await response.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(true));
    }

    private static async Task<JsonElement> PageAsync(HttpClient client, string filters, int limit)
    {
        var url = Url(filters, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddMinutes(1), limit);
        var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(true);
    }

    private static List<JsonElement> Items(JsonElement page)
        => page.GetProperty("items").EnumerateArray().ToList();

    /// <summary>Один рядок журналу — прямим ADO, бо <c>aud.*</c> поза моделлю EF.</summary>
    private async Task WriteAsync(string entityType, int author, string operation = "Test", string? reason = null)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO aud.StructureChange
                (ChangedAt, TemplateVersionId, EntityType, EntityId, ChangeClass, Operation, ChangeReason, ChangedByUserId)
            VALUES (@changedAt, 0, @entityType, 1, 0, @operation, @reason, @author);
            """;

        command.Parameters.AddWithValue("@changedAt", DateTime.UtcNow);
        command.Parameters.AddWithValue("@entityType", entityType);
        command.Parameters.AddWithValue("@operation", operation);
        command.Parameters.AddWithValue("@reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("@author", author);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>Роль із правами → користувач → вхід.</summary>
    private async Task<HttpClient> LoginAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, string[] permissions)
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

        var role = new Role(
            EcrCode.Create($"AUDS_{_tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Role" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var permission in permissions)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        var name = $"auds_{_tag}";
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var client = app.CreateClient();
        var login = await client
            .PostAsJsonAsync(
                new Uri("/api/v1/login/local", UriKind.Relative),
                new { userName = name, password = Password })
            .ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {(app as EcrApiFactory)?.ErrorsText}");

        return client;
    }
}
