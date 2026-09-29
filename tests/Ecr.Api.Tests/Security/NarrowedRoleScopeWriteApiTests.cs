// tests/Ecr.Api.Tests/Security/NarrowedRoleScopeWriteApiTests.cs
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
/// D-214 (ФВ-6.14): аркуші й періоди в області призначення через API —
/// особисте (<c>PUT /users/{id}/roles</c>) і групове
/// (<c>POST /security/group-assignments</c>), читання назад
/// (<c>GET /users/{id}/role-assignments</c>, <c>GET /security/group-assignments</c>).
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: без <c>JsonUnmappedMemberHandling.Disallow</c> на
/// <c>RoleScopeDto</c> рядок «невідомий ключ» — 200 і область без звуження, тобто
/// ширша, ніж просили; без перевірки коду аркуша — 200 з кодом, якого немає.
/// </remarks>
[Collection("SqlServer")]
public sealed class NarrowedRoleScopeWriteApiTests(SqlServerFixture sql)
{
    private const string Password = "Narrowed-Write-D214!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Аркуші_й_періоди_зберігаються_в_регістрі_шаблону_й_читаються_назад()
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var admin = await AdminAsync(app, b.ProjectId).ConfigureAwait(true);
        var (target, role) = await TargetAsync().ConfigureAwait(true);

        var put = await PutRolesAsync(admin, target, role, new
        {
            projects = new[] { b.ProjectId },
            sheets = new[] { b.SheetCode.ToLowerInvariant() },
            periods = new { from = 202601, to = (int?)null },
        }).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.OK, put).ConfigureAwait(true);

        Assert.Equal(
            $"{{\"projects\":[{b.ProjectId}],\"sheets\":[\"{b.SheetCode}\"],\"periods\":{{\"from\":202601,\"to\":null}}}}",
            await ScopeJsonAsync(target).ConfigureAwait(true));

        var listed = JsonDocument.Parse(await admin.GetStringAsync(
            new Uri($"/api/v1/users/{target}/role-assignments", UriKind.Relative)).ConfigureAwait(true)).RootElement;
        var scope = listed.EnumerateArray().Single().GetProperty("scope");
        Assert.Equal(b.SheetCode, scope.GetProperty("sheets").EnumerateArray().Single().GetString());
        Assert.Equal(202601, scope.GetProperty("periods").GetProperty("from").GetInt32());
        Assert.Equal(JsonValueKind.Null, scope.GetProperty("periods").GetProperty("to").ValueKind);
    }

    /// <summary>Будь-яке некоректне звуження — 422 наявним кодом, нічого не записано.</summary>
    [Theory]
    [InlineData("unknownKey")]
    [InlineData("unknownPeriodKey")]
    [InlineData("unknownSheet")]
    [InlineData("badPeriod")]
    [InlineData("reversed")]
    [InlineData("duplicateSheet")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Некоректне_звуження_422_і_нічого_не_записано(string kind)
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var admin = await AdminAsync(app, b.ProjectId).ConfigureAwait(true);
        var (target, role) = await TargetAsync().ConfigureAwait(true);

        object scope = kind switch
        {
            "unknownKey" => new { projects = new[] { b.ProjectId }, regions = new[] { 1 } },
            "unknownPeriodKey" => new { projects = new[] { b.ProjectId }, periods = new { since = 202601 } },
            "unknownSheet" => new { projects = new[] { b.ProjectId }, sheets = new[] { "NOSUCHSHEET" } },
            "badPeriod" => new { projects = new[] { b.ProjectId }, periods = new { from = 202600 } },
            "reversed" => new { projects = new[] { b.ProjectId }, periods = new { from = 202606, to = 202601 } },
            _ => new { projects = new[] { b.ProjectId }, sheets = new[] { b.SheetCode, b.SheetCode.ToLowerInvariant() } },
        };

        using var put = await PutRolesAsync(admin, target, role, scope).ConfigureAwait(true);
        var body = await put.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(put.StatusCode == HttpStatusCode.UnprocessableEntity, $"{kind}: {(int)put.StatusCode}\n{body}");
        Assert.Equal("ECR-REQ-0422", JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString());

        await using var db = Context();
        Assert.False(await db.RoleAssignments.AnyAsync(a => a.UserId == target).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Групове_призначення_несе_аркуші_й_періоди()
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var admin = await AdminAsync(app, b.ProjectId).ConfigureAwait(true);
        var (_, role) = await TargetAsync().ConfigureAwait(true);
        var roleId = await RoleIdAsync(role).ConfigureAwait(true);
        var sid = $"S-1-5-21-214-{BitConverter.ToUInt32(Guid.NewGuid().ToByteArray(), 0)}-1105";

        var post = await admin.PostAsJsonAsync(
                new Uri("/api/v1/security/group-assignments", UriKind.Relative),
                new
                {
                    roleId,
                    principal = sid,
                    scope = new { projects = new[] { b.ProjectId }, sheets = new[] { b.SheetCode }, periods = new { to = 202612 } },
                })
            .ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.Created, post).ConfigureAwait(true);

        var list = JsonDocument.Parse(await admin.GetStringAsync(
            new Uri("/api/v1/security/group-assignments", UriKind.Relative)).ConfigureAwait(true)).RootElement;
        var scope = list.EnumerateArray().Single(r => r.GetProperty("principalSid").GetString() == sid).GetProperty("scope");

        Assert.Equal(b.SheetCode, scope.GetProperty("sheets").EnumerateArray().Single().GetString());
        Assert.Equal(JsonValueKind.Null, scope.GetProperty("periods").GetProperty("from").ValueKind);
        Assert.Equal(202612, scope.GetProperty("periods").GetProperty("to").GetInt32());
    }

    private static async Task ExpectAsync(EcrApiFactory app, HttpStatusCode expected, HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.True(response.StatusCode == expected, $"{response.StatusCode} замість {expected}\n{body}\n{app.ErrorsText}");
        }
    }

    private static Task<HttpResponseMessage> PutRolesAsync(HttpClient client, int userId, string role, object scope)
        => client.PutAsJsonAsync(
            new Uri($"/api/v1/users/{userId}/roles", UriKind.Relative),
            new { roleCodes = new[] { role }, scopes = new Dictionary<string, object> { [role] = scope } });

    /// <summary>Адміністратор: <c>Security.ManageUsers</c> і <c>Manage</c> на проєкт.</summary>
    private async Task<HttpClient> AdminAsync(EcrApiFactory app, int projectId)
    {
        var name = $"adm214_{Guid.NewGuid():N}"[..20];

        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("D-214 admin"));
            db.Users.Add(user);
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Security.ManageUsers"));
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Manage));
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

    /// <summary>Користувач без ролей і роль, яку йому призначатимуть.</summary>
    private async Task<(int UserId, string RoleCode)> TargetAsync()
    {
        await using var db = Context();

        var name = $"t214_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("D-214 target role"));
        db.Users.Add(user);
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (user.Id, role.Code);
    }

    private async Task<string?> ScopeJsonAsync(int userId)
    {
        await using var db = Context();

        return await db.RoleAssignments
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .Select(a => a.ScopeJson)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    private async Task<int> RoleIdAsync(string code)
    {
        await using var db = Context();

        return await db.Roles.Where(r => r.Code == code).Select(r => r.Id).SingleAsync().ConfigureAwait(false);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
