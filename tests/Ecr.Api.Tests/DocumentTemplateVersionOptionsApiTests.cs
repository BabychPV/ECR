// tests/Ecr.Api.Tests/DocumentTemplateVersionOptionsApiTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>GET /api/v1/documents/template-versions</c> — варіанти фільтра «Version» переліку документів наскрізно через HTTP:
/// ВСІ версії видимих документів (не сторінки), природний порядок, кількість, версія за періодом, звуження проєктом і
/// відмова без права. Версія, що є лише в невидимих читачеві документах, не називається.
/// </summary>
/// <remarks>
/// Мутаційний доказ (локально, не в коміті): у <c>DocumentStore.ListTemplateVersionsAsync</c> замінити
/// <c>VisibleDocuments(...)</c> на <c>db.Documents</c> → червоніє <c>Версія_лише_невидимого_проєкту_не_називається</c>
/// (читач з грантом на A і B бачить у відповіді 3.0.0 проєкту C).
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentTemplateVersionOptionsApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Version-Options-2026!";

    // Людський порядок: 2.9.0 < 2.10.0 < 3.0.0 (текстовий дав би 2.10.0 першим).
    private const string VersionA = "2.9.0";
    private const string VersionB = "2.10.0";
    private const string VersionC = "3.0.0";
    private const string VersionBLater = "4.0.0";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Усі_версії_видимих_документів_у_природному_порядку_з_кількістю()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var viewer = await NewUserAsync(Permission, s.ProjectA, s.ProjectB, s.ProjectC).ConfigureAwait(true);
        using var client = await SignedInAsync(app, viewer).ConfigureAwait(true);

        var body = await GetAsync(app, client, $"periodKey=202601", HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Equal(
            [(VersionA, 3), (VersionB, 3), (VersionC, 3)],
            Items(body));
        Assert.Equal(0, body.GetProperty("withoutVersionCount").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Версія_береться_за_період_а_без_періоду_чинна()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var viewer = await NewUserAsync(Permission, s.ProjectA, s.ProjectB, s.ProjectC).ConfigureAwait(true);
        using var client = await SignedInAsync(app, viewer).ConfigureAwait(true);

        // Проєкт B з 202602 переходить на 4.0.0: за 202602 і без періоду B вже не 2.10.0.
        var expected = new[] { (VersionA, 3), (VersionC, 3), (VersionBLater, 3) };
        Assert.Equal(expected, Items(await GetAsync(app, client, "periodKey=202602", HttpStatusCode.OK).ConfigureAwait(true)));
        Assert.Equal(expected, Items(await GetAsync(app, client, string.Empty, HttpStatusCode.OK).ConfigureAwait(true)));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Версія_лише_невидимого_проєкту_не_називається()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);

        // Грант лише на A і B: версія 3.0.0 проєкту C читачеві закрита.
        var two = await NewUserAsync(Permission, s.ProjectA, s.ProjectB).ConfigureAwait(true);
        using var twoClient = await SignedInAsync(app, two).ConfigureAwait(true);
        var visible = await GetAsync(app, twoClient, "periodKey=202601", HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Equal([(VersionA, 3), (VersionB, 3)], Items(visible));
        Assert.DoesNotContain(visible.GetProperty("items").EnumerateArray(), e => e.GetProperty("version").GetString() == VersionC);

        // Фільтр за закритим проєктом нічого не підтверджує: порожньо, як і для проєкту без документів.
        var closed = await GetAsync(app, twoClient, $"periodKey=202601&projectId={Num(s.ProjectC)}", HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Empty(Items(closed));
        Assert.Equal(0, closed.GetProperty("withoutVersionCount").GetInt32());

        // Контроль сценарію: з грантом на C ті самі запити його бачать.
        var all = await NewUserAsync(Permission, s.ProjectA, s.ProjectB, s.ProjectC).ConfigureAwait(true);
        using var allClient = await SignedInAsync(app, all).ConfigureAwait(true);
        Assert.Contains(
            (VersionC, 3),
            Items(await GetAsync(app, allClient, "periodKey=202601", HttpStatusCode.OK).ConfigureAwait(true)));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Фільтр_projectId_звужує_варіанти_до_проєкту()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var viewer = await NewUserAsync(Permission, s.ProjectA, s.ProjectB, s.ProjectC).ConfigureAwait(true);
        using var client = await SignedInAsync(app, viewer).ConfigureAwait(true);

        Assert.Equal(
            [(VersionB, 3)],
            Items(await GetAsync(app, client, $"periodKey=202601&projectId={Num(s.ProjectB)}", HttpStatusCode.OK).ConfigureAwait(true)));
        Assert.Equal(
            [(VersionBLater, 3)],
            Items(await GetAsync(app, client, $"periodKey=202602&projectId={Num(s.ProjectB)}", HttpStatusCode.OK).ConfigureAwait(true)));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_права_Document_View_відмова()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);

        // Грант на проєкт є, а права перегляду документів немає.
        var denied = await NewUserAsync("Template.View", s.ProjectA, s.ProjectB, s.ProjectC).ConfigureAwait(true);
        using var client = await SignedInAsync(app, denied).ConfigureAwait(true);

        using var response = await client
            .GetAsync(new Uri("/api/v1/documents/template-versions?periodKey=202601", UriKind.Relative)).ConfigureAwait(true);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{response.StatusCode}: {text}\n{app.ErrorsText}");
        Assert.DoesNotContain(VersionA, text, StringComparison.Ordinal);
        Assert.DoesNotContain(VersionC, text, StringComparison.Ordinal);
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private const string Permission = "Document.View";

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Лише версії СЦЕНАРІЮ (A, B, B-пізніша, C): документи інших тестів колекції читач без їхніх грантів не бачить.</summary>
    private static List<(string Version, int Count)> Items(JsonElement body)
        => [.. body.GetProperty("items").EnumerateArray()
            .Select(e => (e.GetProperty("version").GetString() ?? string.Empty, e.GetProperty("count").GetInt32()))];

    private static async Task<JsonElement> GetAsync(EcrApiFactory app, HttpClient client, string query, HttpStatusCode status)
    {
        using var response = await client
            .GetAsync(new Uri("/api/v1/documents/template-versions" + (query.Length == 0 ? string.Empty : "?" + query), UriKind.Relative))
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    /// <summary>Користувач з окремою роллю (одне право) і грантами Read на перелічені проєкти.</summary>
    private async Task<string> NewUserAsync(string permission, params int[] projectIds)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var userName = $"tvo_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"TVO_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Version options test" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, permission));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        foreach (var projectId in projectIds)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Read));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        return userName;
    }

    /// <summary>
    /// Три проєкти з версіями 2.9.0 (A), 2.10.0 (B), 3.0.0 (C), у кожному по три документи. У B шкала: з 202602 -
    /// версія 4.0.0.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var a = await builder.BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(false);
        var b = await builder.BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(false);
        var c = await builder.BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(false);

        await using var db = builder.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cfg.TemplateVersion SET Version = {VersionA} WHERE Id = {a.TemplateVersionId}").ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cfg.TemplateVersion SET Version = {VersionB} WHERE Id = {b.TemplateVersionId}").ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cfg.TemplateVersion SET Version = {VersionC} WHERE Id = {c.TemplateVersionId}").ConfigureAwait(false);

        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        // Шкала B: до 202602 - 2.10.0, з 202602 - 4.0.0 (період 202602 теж існує в проєкті).
        var later = new TemplateVersion(b.TemplateId, VersionBLater, 1, now);
        db.TemplateVersions.Add(later);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.ProjectTemplateVersions.Add(new ProjectTemplateVersion(b.ProjectId, 200001, b.TemplateVersionId));
        db.ProjectTemplateVersions.Add(new ProjectTemplateVersion(b.ProjectId, 202602, later.Id));
        db.Periods.Add(new Period(b.ProjectId, new PeriodKey(202602), 2, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var sheets = new Dictionary<int, int> { [a.ProjectId] = a.SheetDefId, [b.ProjectId] = b.SheetDefId, [c.ProjectId] = c.SheetDefId };
        for (var round = 1; round <= 2; round++)
        {
            foreach (var projectId in new[] { a.ProjectId, b.ProjectId, c.ProjectId })
            {
                var document = new Document(projectId, $"TVO{tag}{Num(round)}{Num(projectId)}", 1, now);
                db.Documents.Add(document);
                await db.SaveChangesAsync().ConfigureAwait(false);
                db.DocumentSheets.Add(new DocumentSheet(document.Id, sheets[projectId]));
                await db.SaveChangesAsync().ConfigureAwait(false);
            }
        }

        return new Scenario(a.ProjectId, b.ProjectId, c.ProjectId);
    }

    private sealed record Scenario(int ProjectA, int ProjectB, int ProjectC);
}
