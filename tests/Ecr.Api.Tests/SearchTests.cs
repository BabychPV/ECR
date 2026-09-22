// tests/Ecr.Api.Tests/SearchTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary><c>GET /api/v1/search</c> — пошук даних для командної палітри (BE-19).</summary>
/// <remarks>
/// Наскрізно, справжнім HTTP і справжньою базою: видимість тут — гранти проєкту
/// в SQL-запиті, і модульний тест із фейковим сховищем її не бачить.
/// </remarks>
[Collection("SqlServer")]
public sealed class SearchTests(SqlServerFixture sql)
{
    private const string Password = "Api-Search-2026!";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string[] AllKinds = ["Document.View", "Template.View", "Registry.View"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Знаходить_документ_шаблон_і_довідник_за_частиною_коду_і_за_назвою_мовою_запиту()
    {
        var s = await ArrangeAsync(AllKinds).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        // Частина коду, іншим регістром.
        var byCode = await SearchAsync(client, $"srch{s.Tag}".ToLowerInvariant()).ConfigureAwait(true);
        Assert.Contains(byCode, h => h.Kind == "document" && h.Id == s.VisibleDocumentId);
        Assert.Contains(byCode, h => h.Kind == "template" && h.Code == $"SRCH{s.Tag}T");
        Assert.Contains(byCode, h => h.Kind == "registry" && h.Code == $"SRCH{s.Tag}R");

        // Частина назви кирилицею: у стовпці JSON вона лежить як \uXXXX.
        var byName = await SearchAsync(client, $"{s.Tag} Альф", "ru").ConfigureAwait(true);
        var hit = Assert.Single(byName);
        Assert.Equal(s.VisibleDocumentId, hit.Id);
        Assert.Equal($"Дозвіл {s.Tag} Альфа", hit.Title);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Документ_проєкту_без_гранту_не_зявляється_ні_за_кодом_ні_за_назвою()
    {
        var s = await ArrangeAsync(AllKinds).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        foreach (var term in new[] { $"SRCH{s.Tag}", $"Permit {s.Tag}" })
        {
            var hits = await SearchAsync(client, term).ConfigureAwait(true);

            // Контроль: запит сам по собі знаходить — інакше «немає чужого» нічого не доводить.
            Assert.Contains(hits, h => h.Id == s.VisibleDocumentId && h.Kind == "document");
            Assert.DoesNotContain(hits, h => h.Id == s.HiddenDocumentId && h.Kind == "document");
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_права_Registry_View_довідників_у_відповіді_немає()
    {
        var s = await ArrangeAsync(["Document.View", "Template.View"]).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var hits = await SearchAsync(client, $"SRCH{s.Tag}").ConfigureAwait(true);

        Assert.Contains(hits, h => h.Kind == "template");
        Assert.DoesNotContain(hits, h => h.Kind == "registry");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Запит_коротший_за_два_символи_дає_порожньо_а_не_помилку()
    {
        var s = await ArrangeAsync(AllKinds).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        // «S» збігається з кодом видимого документа — порожньо саме через довжину.
        Assert.Empty(await SearchAsync(client, " S ").ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відповідь_не_довша_за_20_хоч_би_що_попросив_клієнт_і_10_типово()
    {
        var s = await ArrangeAsync(AllKinds, extraVisibleDocuments: 25).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        Assert.Equal(20, (await SearchAsync(client, $"CAP{s.Tag}", limit: 100).ConfigureAwait(true)).Count);
        Assert.Equal(10, (await SearchAsync(client, $"CAP{s.Tag}").ConfigureAwait(true)).Count);
    }

    private static async Task<List<Hit>> SearchAsync(
        HttpClient client, string q, string? language = null, int? limit = null)
    {
        var url = $"/api/v1/search?q={Uri.EscapeDataString(q)}"
                  + (limit is { } l ? $"&limit={l.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : string.Empty);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        if (language is not null)
        {
            request.Headers.AcceptLanguage.ParseAdd(language);
        }

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<List<Hit>>(body, Json)!;
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

    /// <summary>
    /// Два проєкти з однаково названими документами; грант — лише на перший.
    /// Шаблон і довідник із тим самим тегом у коді.
    /// </summary>
    private async Task<Scenario> ArrangeAsync(string[] permissions, int extraVisibleDocuments = 0)
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var visible = await builder.BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(false);
        var hidden = await builder.BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(false);

        await using var db = builder.CreateContext();

        var docA = new Document(visible.ProjectId, $"SRCH{tag}A", 1, now);
        docA.SetName(Text(("en", $"Permit {tag} Alpha"), ("ru", $"Дозвіл {tag} Альфа")));
        var docB = new Document(hidden.ProjectId, $"SRCH{tag}B", 1, now);
        docB.SetName(Text(("en", $"Permit {tag} Beta"), ("ru", $"Дозвіл {tag} Бета")));
        db.Documents.AddRange(docA, docB);

        for (var i = 0; i < extraVisibleDocuments; i++)
        {
            db.Documents.Add(new Document(
                visible.ProjectId, $"CAP{tag}{i.ToString("00", System.Globalization.CultureInfo.InvariantCulture)}", 1, now));
        }

        db.Templates.Add(new Template(EcrCode.Create($"SRCH{tag}T"), Text(("en", "Search template")), 1, now));
        db.RegistryDefs.Add(new RegistryDef(EcrCode.Create($"SRCH{tag}R"), Text(("en", "Search registry")), isTemporal: false));

        var userName = $"search_{tag}";
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        // Роль своя на кожен прогін: база спільна на всю збірку.
        var role = new Role(EcrCode.Create($"SEARCH_{tag}"), Text(("en", "Search")));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var permission in permissions)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, visible.ProjectId, GrantLevel.Read));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(tag, userName, docA.Id, docB.Id);
    }

    private static LocalizedText Text(params (string Lang, string Value)[] values)
        => new(values.ToDictionary(v => v.Lang, v => v.Value));

    private sealed record Scenario(string Tag, string UserName, long VisibleDocumentId, long HiddenDocumentId);

    private sealed record Hit(string Kind, long Id, string Code, string Title);
}
