// tests/Ecr.Api.Tests/Security/GrantableProjectsApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// <c>GET /api/v1/security/projects</c> — код і назва всіх проєктів для видачі
/// грантів (рішення людини 2026-09-29, D-207 п.2, варіант B).
/// </summary>
/// <remarks>
/// ⛔ Доти адміністратор безпеки без грантів на проєкти бачив порожній вибір
/// проєкту в гранті: <c>GET /projects</c> фільтрує за грантами і вимагає
/// <c>Document.View</c>. Перелік відкриває ЛИШЕ ідентичність проєкту — дані
/// проєкту (документи, зрізи, періоди) лишаються за грантами, і це тут
/// перевіряється тим самим клієнтом.
/// </remarks>
[Collection("SqlServer")]
public sealed class GrantableProjectsApiTests(SqlServerFixture sql)
{
    private const string Password = "Grantable-Projects-2026!";

    /// <remarks>
    /// ✎ 2026-09-29 (рішення координатора): довідник відкритий і під
    /// <c>Security.ManageUsers</c> — форма ролей користувача вибирає з нього
    /// область дії (ФВ-6.14). ⛔ МУТАЦІЯ: прибрати друге право з перевірки —
    /// випадок <c>Security.ManageUsers</c> червоніє.
    /// </remarks>
    [Theory]
    [InlineData("Security.ManageRoles")]
    [InlineData("Security.ManageUsers")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-207")]
    public async Task Адмін_безпеки_без_грантів_бачить_код_і_назву_проєкту_і_нічого_більше(string permission)
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(true);
        var (code, name) = await ProjectAsync(b.ProjectId).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var admin = await SignedInAsync(app, scope: null, permission).ConfigureAwait(true);

        using var response = await admin.GetAsync(Uri("/api/v1/security/projects")).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}\n{body}\n{app.ErrorsText}");

        var items = JsonDocument.Parse(body).RootElement.EnumerateArray().ToList();
        var row = Assert.Single(items, p => p.GetProperty("id").GetInt32() == b.ProjectId);
        Assert.Equal(code, row.GetProperty("code").GetString());
        Assert.Equal(name, row.GetProperty("nameL10n").GetProperty("values").GetProperty("en").GetString());

        // ⛔ Лише ідентичність: жодного стану, поясу чи періоду — в ЖОДНОМУ рядку.
        Assert.All(items, p => Assert.Equal(
            ["code", "id", "nameL10n"],
            p.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).ToArray()));

        // Дані проєкту — за грантами, як і доти.
        foreach (var path in new[]
                 {
                     "/api/v1/projects",
                     $"/api/v1/projects/{b.ProjectId}/periods",
                     $"/api/v1/documents?projectId={b.ProjectId}",
                     $"/api/v1/documents/{b.DocumentId}",
                     $"/api/v1/reports/snapshots?projectId={b.ProjectId}",
                 })
        {
            using var denied = await admin.GetAsync(Uri(path)).ConfigureAwait(true);
            var text = await denied.Content.ReadAsStringAsync().ConfigureAwait(true);

            Assert.True(
                denied.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
                $"{path}: {(int)denied.StatusCode}\n{text}");
            Assert.DoesNotContain(code, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-207")]
    public async Task Без_обох_прав_безпеки_перелік_закритий_навіть_із_грантом_на_проєкт()
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var viewer = await SignedInAsync(
            app, scope: null, grant: b.ProjectId, "Document.View", "Security.Simulate").ConfigureAwait(true);

        await AssertForbiddenAsync(viewer, app).ConfigureAwait(true);
    }

    /// <remarks>
    /// ⛔ <c>Security.ManageRoles</c> — глобальне право (<c>PermissionScopes.Global</c>):
    /// «адміністратор безпеки лише проєкту A» над грантами всієї системи — це
    /// адміністратор усієї системи, тож роль з областю дії цього права не дає.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Роль_з_областю_дії_не_дає_переліку()
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var scoped = await SignedInAsync(
            app, scope: [b.ProjectId], grant: b.ProjectId, "Security.ManageRoles").ConfigureAwait(true);

        await AssertForbiddenAsync(scoped, app).ConfigureAwait(true);
    }

    private static async Task AssertForbiddenAsync(HttpClient client, EcrApiFactory app)
    {
        using var response = await client.GetAsync(Uri("/api/v1/security/projects")).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{(int)response.StatusCode}\n{body}\n{app.ErrorsText}");
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-AUTH-0403", root.GetProperty("errorCode").GetString());
        Assert.Equal("Security.ManageRoles", root.GetProperty("permission").GetString());
    }

    private static Uri Uri(string path) => new(path, UriKind.Relative);

    private Task<HttpClient> SignedInAsync(EcrApiFactory app, int[]? scope, params string[] permissions)
        => SignedInAsync(app, scope, grant: null, permissions);

    private async Task<HttpClient> SignedInAsync(
        EcrApiFactory app, int[]? scope, int? grant, params string[] permissions)
    {
        var name = $"gp_{Guid.NewGuid():N}"[..18];

        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            var role = new Role(
                EcrCode.Create($"GP{Guid.NewGuid():N}"[..12]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Grantable projects" }));
            db.Users.Add(user);
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            var assignment = new RoleAssignment(role.Id, user.Id, principalSid: null);
            if (scope is not null)
            {
                assignment.SetScope(RoleAssignmentScope.Create(scope));
            }

            db.RoleAssignments.Add(assignment);

            if (grant is { } projectId)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Read));
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            Uri("/api/v1/login/local"), new { userName = name, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private async Task<(string Code, string Name)> ProjectAsync(int projectId)
    {
        await using var db = Context();
        var project = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == projectId).ConfigureAwait(false);

        return (project.Code, project.NameL10n.Values["en"]);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
