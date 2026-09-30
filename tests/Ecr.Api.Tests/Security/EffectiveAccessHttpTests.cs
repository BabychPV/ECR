// tests/Ecr.Api.Tests/Security/EffectiveAccessHttpTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// ФВ-6.16 (D-220): розріз «ресурс → підсумковий рівень → який грант якої ролі його дав» на справжньому
/// шляху: HTTP, справжній <c>AccessDecisionService</c> і справжні рядки <c>sec.*</c>.
/// </summary>
/// <remarks>
/// ⛔ Головна перевірка в кожному тесті — підсумковий рівень розрізу дорівнює тому, що дає профіль доступу
/// (<c>AccessDecisionService.BuildProfileAsync</c>) тому самому користувачеві, а для довідника ще й
/// тому, що він реально отримує на маршруті читання (200/404). Розріз, що розійшовся з рішенням,
/// гірший за відсутність розрізу: він показував би доступ, якого немає.
/// </remarks>
[Collection("SqlServer")]
public sealed class EffectiveAccessHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Effective-Access-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Кілька_ролей__внески_атрибутовані_роллю_і_призначенню__рівень_збігається_з_профілем()
    {
        using var app = new EcrApiFactory(sql);
        var stand = await SeedAsync();

        var reader = await AddRoleAsync(stand.Tag, "Rdr", [], [(ResourceKind.Registry, stand.RegistryId, GrantLevel.Read, false)]);
        var writer = await AddRoleAsync(stand.Tag, "Wrt", [], [(ResourceKind.Registry, stand.RegistryId, GrantLevel.Write, false)]);
        var unrelated = await AddRoleAsync(stand.Tag, "Oth", [], [(ResourceKind.Registry, stand.RegistryId + 9000, GrantLevel.Manage, false)]);
        var subject = await AddUserAsync([reader, writer, unrelated]);
        using var admin = await SignedInAdminAsync(app);

        var view = await GetAsync(admin, subject.Id, $"Registry:{stand.RegistryId}");

        Assert.Equal("Write", view.GetProperty("level").GetString());
        Assert.False(view.GetProperty("isDenied").GetBoolean());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("denyReason").ValueKind);

        var rows = Contributions(view);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.Counted && r.Source == "Grant" && r.Scope == "Unscoped" && r.Via is null));
        Assert.Equal(("Rdr", "Read"), (Suffix(rows[0].Role), rows[0].Level));
        Assert.Equal(("Wrt", "Write"), (Suffix(rows[1].Role), rows[1].Level));

        Assert.Equal(GrantLevel.Write, await ProfileLevelAsync(app, subject.Id, ResourceKind.Registry, stand.RegistryId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Глобальне_право_і_Deny__заборона_виграє__розріз_і_маршрут_кажуть_те_саме_що_S18()
    {
        using var app = new EcrApiFactory(sql);
        var stand = await SeedAsync();

        var global = await AddRoleAsync(stand.Tag, "Glb", ["Registry.View", "Registry.EditData"], []);
        var denier = await AddRoleAsync(stand.Tag, "Dny", [], [(ResourceKind.Registry, stand.RegistryId, GrantLevel.Read, true)]);
        var open = await AddUserAsync([global]);
        var denied = await AddUserAsync([global, denier]);
        using var admin = await SignedInAdminAsync(app);

        // Без заборони: глобальне право дає Write, внески — два права, обидва враховані.
        var openView = await GetAsync(admin, open.Id, $"Registry:{stand.RegistryId}");
        Assert.Equal("Write", openView.GetProperty("level").GetString());
        var openRows = Contributions(openView);
        Assert.Equal(2, openRows.Count);
        Assert.All(openRows, r => Assert.True(r.Counted && r.Source == "Permission"));
        Assert.Equal(["Registry.EditData", "Registry.View"], openRows.Select(r => r.Permission!).Order(StringComparer.Ordinal));

        // З забороною: підсумок None, причина — явна заборона, заборона теж у внесках.
        var view = await GetAsync(admin, denied.Id, $"Registry:{stand.RegistryId}");
        Assert.Equal("None", view.GetProperty("level").GetString());
        Assert.True(view.GetProperty("isDenied").GetBoolean());
        Assert.Equal("ExplicitDeny", view.GetProperty("denyReason").GetString());
        Assert.Contains(Contributions(view), r => r.Source == "Grant" && r.Deny && r.Counted);
        Assert.Equal(2, Contributions(view).Count(r => r.Source == "Permission"));

        Assert.Equal(GrantLevel.None, await ProfileLevelAsync(app, denied.Id, ResourceKind.Registry, stand.RegistryId));

        // Те саме, що людина отримує насправді: відкритий користувач читає, заборонений — 404.
        using var openClient = await SignedInAsync(app, open.UserName);
        using var deniedClient = await SignedInAsync(app, denied.UserName);
        var path = new Uri($"/api/v1/registries/{stand.RegistryCode}/entries?asOf=2026-09-30", UriKind.Relative);
        Assert.Equal(HttpStatusCode.OK, (await openClient.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await deniedClient.GetAsync(path)).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Роль_з_областю__внесок_у_проєкті_області_враховано__у_чужому_проєкті_і_на_довіднику_ні()
    {
        using var app = new EcrApiFactory(sql);
        var stand = await SeedAsync();

        var scoped = await AddRoleAsync(
            stand.Tag, "Scp", [],
            [
                (ResourceKind.Project, stand.ProjectA, GrantLevel.Manage, false),
                (ResourceKind.Project, stand.ProjectB, GrantLevel.Read, false),
                (ResourceKind.Registry, stand.RegistryId, GrantLevel.Write, false),
            ]);
        var subject = await AddUserAsync([scoped], scopeProjects: [stand.ProjectA]);
        using var admin = await SignedInAdminAsync(app);

        var inScope = await GetAsync(admin, subject.Id, $"Project:{stand.ProjectA}");
        Assert.Equal("Manage", inScope.GetProperty("level").GetString());
        var inRow = Assert.Single(Contributions(inScope));
        Assert.True(inRow.Counted);
        Assert.Equal("InScope", inRow.Scope);

        var outside = await GetAsync(admin, subject.Id, $"Project:{stand.ProjectB}");
        Assert.Equal("None", outside.GetProperty("level").GetString());
        Assert.Equal("NoGrant", outside.GetProperty("denyReason").GetString());
        var outRow = Assert.Single(Contributions(outside));
        Assert.False(outRow.Counted);
        Assert.Equal("OutOfScope", outRow.Scope);

        // Роль з областю довідників не дає ніде — так само, як профіль.
        var registry = await GetAsync(admin, subject.Id, $"Registry:{stand.RegistryId}");
        Assert.Equal("None", registry.GetProperty("level").GetString());
        Assert.False(Assert.Single(Contributions(registry)).Counted);

        Assert.Equal(GrantLevel.Manage, await ProfileLevelAsync(app, subject.Id, ResourceKind.Project, stand.ProjectA));
        Assert.Equal(GrantLevel.None, await ProfileLevelAsync(app, subject.Id, ResourceKind.Project, stand.ProjectB));
        Assert.Equal(GrantLevel.None, await ProfileLevelAsync(app, subject.Id, ResourceKind.Registry, stand.RegistryId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Групове_призначення__у_внеску_SID_групи__групи_чужого_запису_не_підставляються()
    {
        using var app = new EcrApiFactory(sql);
        var stand = await SeedAsync();
        var sid = $"S-1-5-21-{Random.Shared.NextInt64(1_000_000, 9_000_000)}-1001";

        var role = await AddRoleAsync(stand.Tag, "Grp", [], [(ResourceKind.Registry, stand.RegistryId, GrantLevel.Read, false)]);
        var subject = await AddUserAsync([]);
        await using (var db = new EcrDbContext(Options()))
        {
            db.RoleAssignments.Add(new RoleAssignment(role, userId: null, principalSid: sid));
            await db.SaveChangesAsync();
        }

        using var admin = await SignedInAdminAsync(app);
        var view = await GetAsync(admin, subject.Id, $"Registry:{stand.RegistryId}");

        // Групи чужої сесії невідомі (`P-02`): групове призначення не приписується людині.
        Assert.False(view.GetProperty("groupsFromTicket").GetBoolean());
        Assert.Empty(Contributions(view));
        Assert.Equal("None", view.GetProperty("level").GetString());
        Assert.Equal("NoGrant", view.GetProperty("denyReason").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Відсутні_ресурс_і_користувач__404__хибна_форма_ресурсу__422__без_права__403()
    {
        using var app = new EcrApiFactory(sql);
        var stand = await SeedAsync();
        var subject = await AddUserAsync([]);
        using var admin = await SignedInAdminAsync(app);

        Assert.Equal(HttpStatusCode.NotFound, (await Raw(admin, subject.Id, $"Registry:{stand.RegistryId + 50_000}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Raw(admin, subject.Id, $"Project:{stand.ProjectA + 50_000}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Raw(admin, subject.Id + 500_000, $"Registry:{stand.RegistryId}")).StatusCode);

        foreach (var bad in new[] { "", "Registry", "Registry:abc", "Registry:0", "Sheet:1", "Bogus:1" })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Raw(admin, subject.Id, bad)).StatusCode);
        }

        using var plain = await SignedInAsync(app, subject.UserName);
        Assert.Equal(HttpStatusCode.Forbidden, (await Raw(plain, subject.Id, $"Registry:{stand.RegistryId}")).StatusCode);
    }

    private static async Task<HttpResponseMessage> Raw(HttpClient client, int userId, string resource)
        => await client.GetAsync(new Uri(
            $"/api/v1/security/users/{userId}/effective-access?resource={Uri.EscapeDataString(resource)}", UriKind.Relative));

    private static async Task<JsonElement> GetAsync(HttpClient client, int userId, string resource)
    {
        var response = await Raw(client, userId, resource);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static List<Row> Contributions(JsonElement view)
        => [.. view.GetProperty("contributions").EnumerateArray().Select(c => new Row(
            c.GetProperty("source").GetString()!,
            c.GetProperty("roleCode").GetString()!,
            c.GetProperty("principalSid").ValueKind == JsonValueKind.Null ? null : c.GetProperty("principalSid").GetString(),
            c.GetProperty("permissionCode").ValueKind == JsonValueKind.Null ? null : c.GetProperty("permissionCode").GetString(),
            c.GetProperty("level").GetString()!,
            c.GetProperty("isDeny").GetBoolean(),
            c.GetProperty("scope").GetString()!,
            c.GetProperty("counted").GetBoolean()))];

    private static string Suffix(string roleCode) => roleCode.Split('_')[1];

    private static async Task<GrantLevel> ProfileLevelAsync(EcrApiFactory app, int userId, ResourceKind kind, int resourceId)
    {
        using var scope = app.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<Ecr.Application.Security.IAccessDecisionService>();
        var profile = await access.BuildProfileAsync(userId, CancellationToken.None);
        return profile.LevelFor(kind, resourceId);
    }

    private sealed record Row(
        string Source, string Role, string? Via, string? Permission, string Level, bool Deny, string Scope, bool Counted);

    private sealed record Stand(string Tag, int RegistryId, string RegistryCode, int ProjectA, int ProjectB);

    private sealed record Account(int Id, string UserName);

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private async Task<Stand> SeedAsync()
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = new EcrDbContext(Options());

        var registry = new RegistryDef(EcrCode.Create($"EFA{tag}"), Name($"Eff {tag}"), isTemporal: false);
        db.RegistryDefs.Add(registry);

        var template = new Template(EcrCode.Create($"EFT_{tag}"), Name("Template"), 1, DateTime.UtcNow);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, DateTime.UtcNow);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        // periodPolicyId 1 — сіяна політика «ECR-Standard» (той самий факт, на який спираються інші тести).
        var a = new Project(
            EcrCode.Create($"EFPA_{tag}"), Name("A"), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            version.Id, PeriodKind.Monthly, periodPolicyId: 1, "Asia/Atyrau");
        var b = new Project(
            EcrCode.Create($"EFPB_{tag}"), Name("B"), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            version.Id, PeriodKind.Monthly, periodPolicyId: 1, "Asia/Atyrau");
        db.Projects.AddRange(a, b);
        await db.SaveChangesAsync();

        return new Stand(tag, registry.Id, registry.Code, a.Id, b.Id);
    }

    /// <summary>Роль із правами й грантами; код <c>EF{tag}_{suffix}</c> — суфікс читає <see cref="Suffix"/>.</summary>
    private async Task<int> AddRoleAsync(
        string tag, string suffix, string[] permissions,
        (ResourceKind Kind, int Id, GrantLevel Level, bool Deny)[] grants)
    {
        await using var db = new EcrDbContext(Options());
        var role = new Role(EcrCode.Create($"EF{tag}_{suffix}"), Name($"Role {suffix}"));
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        foreach (var permission in permissions)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        foreach (var grant in grants)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, grant.Kind, grant.Id, grant.Level, grant.Deny));
        }

        await db.SaveChangesAsync();
        return role.Id;
    }

    private async Task<Account> AddUserAsync(int[] roleIds, int[]? scopeProjects = null)
    {
        var name = $"effa_{Guid.NewGuid():N}"[..20];
        await using var db = new EcrDbContext(Options());
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        await db.SaveChangesAsync();

        foreach (var roleId in roleIds)
        {
            var assignment = new RoleAssignment(roleId, user.Id, principalSid: null);
            if (scopeProjects is not null)
            {
                assignment.SetScope(RoleAssignmentScope.Create(scopeProjects));
            }

            db.RoleAssignments.Add(assignment);
        }

        await db.SaveChangesAsync();
        return new Account(user.Id, name);
    }

    private async Task<HttpClient> SignedInAdminAsync(EcrApiFactory app)
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        var role = await AddRoleAsync(tag, "Adm", ["Security.ManageUsers"], []);
        var admin = await AddUserAsync([role]);
        return await SignedInAsync(app, admin.UserName);
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }
}
