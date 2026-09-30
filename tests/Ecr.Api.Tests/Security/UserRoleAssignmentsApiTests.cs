// tests/Ecr.Api.Tests/Security/UserRoleAssignmentsApiTests.cs
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
/// ФВ-6.14: <c>GET /users/{id}/role-assignments</c> віддає особисті
/// призначення з межами й областю дії — те, на чому форма ролей будує
/// повний словник <c>scopes</c> для <c>PUT …/roles</c>.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: у <c>UserStore.ListUserRoleAssignmentsAsync</c>
/// віддавати <c>Scope: null</c> завжди — тест «область віддається» червоніє;
/// прибрати <c>PermissionCheck.RequireAsync</c> з
/// <c>ListUserRolesHandler.ListAssignmentsAsync</c> — червоніє тест «без права — 403».
/// </remarks>
[Collection("SqlServer")]
public sealed class UserRoleAssignmentsApiTests(SqlServerFixture sql)
{
    private const string Password = "Role-Assign-Get-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Область_межі_й_безобласне_призначення_віддаються()
    {
        var projectId = await AnyProjectIdAsync().ConfigureAwait(true);
        var (target, scoped, open, dated) = await TargetAsync(projectId).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var admin = await SignedInAsync(app, "Security.ManageUsers").ConfigureAwait(true);

        var response = await admin.GetAsync(
            new Uri($"/api/v1/users/{target}/role-assignments", UriKind.Relative)).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}\n{body}\n{app.ErrorsText}");

        var rows = JsonDocument.Parse(body).RootElement.EnumerateArray().ToList();
        Assert.Equal(3, rows.Count);

        var scopedRow = rows.Single(r => r.GetProperty("roleCode").GetString() == scoped);
        Assert.Equal(
            new[] { projectId },
            scopedRow.GetProperty("scope").GetProperty("projects").EnumerateArray().Select(p => p.GetInt32()));
        Assert.Equal(JsonValueKind.Null, scopedRow.GetProperty("validFrom").ValueKind);

        var openRow = rows.Single(r => r.GetProperty("roleCode").GetString() == open);
        Assert.Equal(JsonValueKind.Null, openRow.GetProperty("scope").ValueKind);

        var datedRow = rows.Single(r => r.GetProperty("roleCode").GetString() == dated);
        Assert.Equal("2026-01-01", datedRow.GetProperty("validFrom").GetString());
        Assert.Equal("2026-01-31", datedRow.GetProperty("validTo").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Без_права_керування_користувачами_403_і_без_даних()
    {
        var projectId = await AnyProjectIdAsync().ConfigureAwait(true);
        var (target, scoped, _, _) = await TargetAsync(projectId).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);

        // ⚠ Суміжне право керування РОЛЯМИ не дає читати чужі призначення.
        using var other = await SignedInAsync(app, "Security.ManageRoles").ConfigureAwait(true);

        var response = await other.GetAsync(
            new Uri($"/api/v1/users/{target}/role-assignments", UriKind.Relative)).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{(int)response.StatusCode}\n{body}");
        Assert.Equal("ECR-AUTH-0403", JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString());
        Assert.DoesNotContain(scoped, body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Неіснуючий_користувач_404()
    {
        using var app = new EcrApiFactory(sql);
        using var admin = await SignedInAsync(app, "Security.ManageUsers").ConfigureAwait(true);

        var response = await admin.GetAsync(
            new Uri($"/api/v1/users/{int.MaxValue}/role-assignments", UriKind.Relative)).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<int> AnyProjectIdAsync()
    {
        var document = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(false);

        return document.ProjectId;
    }

    /// <summary>Користувач із трьома призначеннями: з областю, без, строкове.</summary>
    private async Task<(int UserId, string Scoped, string Open, string Dated)> TargetAsync(int projectId)
    {
        await using var db = Context();

        var name = $"rag_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var roles = Enumerable.Range(0, 3)
            .Select(_ => new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("role-assignments")))
            .ToList();
        db.Roles.AddRange(roles);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var scoped = new RoleAssignment(roles[0].Id, user.Id, principalSid: null);
        scoped.SetScope(RoleAssignmentScope.Create([projectId]));
        var dated = new RoleAssignment(roles[2].Id, user.Id, principalSid: null);
        dated.SetValidity(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        db.RoleAssignments.AddRange(scoped, new RoleAssignment(roles[1].Id, user.Id, principalSid: null), dated);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (user.Id, roles[0].Code, roles[1].Code, roles[2].Code);
    }

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, string permission)
    {
        var name = $"ragA_{Guid.NewGuid():N}"[..20];

        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("role-assignments admin"));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, permission));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
