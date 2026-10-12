// tests/Ecr.Api.Tests/ApprovalRouteHttpTests.cs
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

namespace Ecr.Api.Tests;

/// <summary>
/// HTTP-сценарії <c>PUT /projects/{id}/approval-route</c> (ФВ-5.17): успіх, відмова без права й
/// гранта, відмови вмісту. Раніше була лише обробникова перевірка (<c>ApprovalRouteHandlerTests</c>),
/// а невидимий/неіснуючий проєкт — у <c>ProjectExistenceOracleApiTests</c>.
/// </summary>
/// <remarks>
/// ⛔ Порядок перевірок серверний і тут зафіксований: право (<c>403 ECR-AUTH-0403</c>) → видимість
/// проєкту (<c>404 ECR-PRJ-0404</c>) → грант Manage на ЦЕЙ проєкт (<c>403 noProjectManageGrant</c>) →
/// вміст (<c>422 ECR-DOC-0422</c>, <c>404 ECR-SEC-0404</c>). Після будь-якої відмови маршрут лишається
/// тим, що був: заміна атомарна, а не «частково застосована».
///
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати перевірку <c>profile.LevelFor(…) &lt; GrantLevel.Manage</c> з
/// <c>ReplaceApprovalRouteHandler</c> — червоніє «лише грант Read → 403»; прибрати перевірку
/// <c>MaxSteps</c> — червоніє «21 крок → 422».
/// </remarks>
[Collection("SqlServer")]
public sealed class ApprovalRouteHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Approval-Route-2026!";

    private const int Missing = 999_999_999;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.17")]
    public async Task Менеджер_проєкту_заміняє_маршрут_читає_його_й_прибирає_порожнім_набором()
    {
        var stand = await ArrangeAsync(2).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, ["Project.Manage"], stand.ProjectId, GrantLevel.Manage)
            .ConfigureAwait(true);

        using var put = await manager.PutAsJsonAsync(RouteOf(stand.ProjectId), new { roleIds = stand.RoleIds })
            .ConfigureAwait(true);
        var putBody = await put.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(put.StatusCode == HttpStatusCode.OK, $"{(int)put.StatusCode}: {putBody}\n{app.ErrorsText}");
        using (var json = JsonDocument.Parse(putBody))
        {
            Assert.Equal(2, json.RootElement.GetProperty("steps").GetInt32());
        }

        var route = await ReadRouteAsync(app, manager, stand.ProjectId).ConfigureAwait(true);
        Assert.True(route.HasRoute);
        Assert.Equal(stand.RoleIds, route.RoleIds);

        // Порожній набір прибирає маршрут: затвердження повертається до одноетапного.
        using var clear = await manager.PutAsJsonAsync(RouteOf(stand.ProjectId), new { roleIds = Array.Empty<int>() })
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);

        var cleared = await ReadRouteAsync(app, manager, stand.ProjectId).ConfigureAwait(true);
        Assert.False(cleared.HasRoute);
        Assert.Empty(cleared.RoleIds);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Без_права_Project_Manage_403_однаково_для_наявного_й_неіснуючого_проєкту()
    {
        var stand = await ArrangeAsync(1).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);

        // Грант Manage є, права Project.Manage — ні: грант права не підміняє.
        using var client = await SignedInAsync(app, ["Document.View"], stand.ProjectId, GrantLevel.Manage)
            .ConfigureAwait(true);

        using var existing = await client.PutAsJsonAsync(RouteOf(stand.ProjectId), new { roleIds = stand.RoleIds })
            .ConfigureAwait(true);
        using var missing = await client.PutAsJsonAsync(RouteOf(Missing), new { roleIds = stand.RoleIds })
            .ConfigureAwait(true);

        var a = await RefusalAsync(app, existing, HttpStatusCode.Forbidden, "ECR-AUTH-0403").ConfigureAwait(true);
        var b = await RefusalAsync(app, missing, HttpStatusCode.Forbidden, "ECR-AUTH-0403").ConfigureAwait(true);

        Assert.Contains("Project.Manage", a.Body, StringComparison.Ordinal);
        Assert.Equal(a.MessageKey, b.MessageKey);

        var route = await ReadRouteAsAdminAsync(stand.ProjectId).ConfigureAwait(true);
        Assert.False(route);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Право_є_а_гранта_лише_Read_403_noProjectManageGrant_і_маршрут_не_змінюється()
    {
        var stand = await ArrangeAsync(1).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var reader = await SignedInAsync(app, ["Project.Manage"], stand.ProjectId, GrantLevel.Read)
            .ConfigureAwait(true);

        using var put = await reader.PutAsJsonAsync(RouteOf(stand.ProjectId), new { roleIds = stand.RoleIds })
            .ConfigureAwait(true);

        var refusal = await RefusalAsync(app, put, HttpStatusCode.Forbidden, "ECR-AUTH-0403").ConfigureAwait(true);
        Assert.Equal("err.ECR-AUTH-0403.noProjectManageGrant", refusal.MessageKey);

        Assert.False(await ReadRouteAsAdminAsync(stand.ProjectId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.17")]
    public async Task Роль_кроку_якої_немає_404_ECR_SEC_0404_і_маршрут_не_змінюється()
    {
        var stand = await ArrangeAsync(1).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, ["Project.Manage"], stand.ProjectId, GrantLevel.Manage)
            .ConfigureAwait(true);

        using var put = await manager.PutAsJsonAsync(RouteOf(stand.ProjectId), new { roleIds = new[] { stand.RoleIds[0], Missing } })
            .ConfigureAwait(true);

        var refusal = await RefusalAsync(app, put, HttpStatusCode.NotFound, "ECR-SEC-0404").ConfigureAwait(true);
        Assert.Equal("err.ECR-SEC-0404.roleNotFound", refusal.MessageKey);

        Assert.False(await ReadRouteAsAdminAsync(stand.ProjectId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.17")]
    public async Task Дві_однакові_ролі_поспіль_422_а_наявний_маршрут_лишається()
    {
        var stand = await ArrangeAsync(2).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, ["Project.Manage"], stand.ProjectId, GrantLevel.Manage)
            .ConfigureAwait(true);

        // Є наявний маршрут з двох кроків — після відмови він має лишитися саме таким.
        using var first = await manager.PutAsJsonAsync(RouteOf(stand.ProjectId), new { roleIds = stand.RoleIds })
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var roleId = stand.RoleIds[0];
        using var twice = await manager.PutAsJsonAsync(RouteOf(stand.ProjectId), new { roleIds = new[] { roleId, roleId } })
            .ConfigureAwait(true);

        var refusal = await RefusalAsync(app, twice, HttpStatusCode.UnprocessableEntity, "ECR-DOC-0422").ConfigureAwait(true);
        Assert.Equal("err.ECR-DOC-0422.approvalRouteConsecutiveRole", refusal.MessageKey);

        var route = await ReadRouteAsync(app, manager, stand.ProjectId).ConfigureAwait(true);
        Assert.Equal(stand.RoleIds, route.RoleIds);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.17")]
    public async Task Маршрут_довший_за_межу_422_approvalRouteTooLong_до_будь_яких_запитів_до_ролей()
    {
        var stand = await ArrangeAsync(1).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, ["Project.Manage"], stand.ProjectId, GrantLevel.Manage)
            .ConfigureAwait(true);

        // Ролі кроків — неіснуючі: якби межа перевірялась ПІСЛЯ ролей, відповідь була б 404.
        var tooLong = Enumerable.Range(0, 21).Select(i => Missing - i).ToArray();
        using var put = await manager.PutAsJsonAsync(RouteOf(stand.ProjectId), new { roleIds = tooLong })
            .ConfigureAwait(true);

        var refusal = await RefusalAsync(app, put, HttpStatusCode.UnprocessableEntity, "ECR-DOC-0422").ConfigureAwait(true);
        Assert.Equal("err.ECR-DOC-0422.approvalRouteTooLong", refusal.MessageKey);
    }

    private static Uri RouteOf(int projectId) => new($"/api/v1/projects/{projectId}/approval-route", UriKind.Relative);

    private sealed record RouteView(bool HasRoute, int[] RoleIds);

    private static async Task<RouteView> ReadRouteAsync(EcrApiFactory app, HttpClient client, int projectId)
    {
        using var response = await client.GetAsync(RouteOf(projectId)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}\n{app.ErrorsText}");

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        return new RouteView(
            root.GetProperty("hasRoute").GetBoolean(),
            [.. root.GetProperty("steps").EnumerateArray()
                .OrderBy(s => s.GetProperty("ordinal").GetInt32())
                .Select(s => s.GetProperty("roleId").GetInt32())]);
    }

    /// <summary>Чи є в проєкті власний маршрут — прямо з бази, поза правами будь-кого з тесту.</summary>
    private async Task<bool> ReadRouteAsAdminAsync(int projectId)
    {
        await using var db = Db();

        return await db.Set<Ecr.Domain.Entities.Workflow.ApprovalRoute>()
            .AsNoTracking()
            .AnyAsync(r => r.ProjectId == projectId)
            .ConfigureAwait(false);
    }

    private sealed record Refusal(string? ErrorCode, string? MessageKey, string Body);

    private static async Task<Refusal> RefusalAsync(
        EcrApiFactory app, HttpResponseMessage response, HttpStatusCode status, string errorCode)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(
            response.StatusCode == status,
            $"очікували {(int)status}, отримали {(int)response.StatusCode}\n{body}\n{app.ErrorsText}");

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        string? Str(string name) => root.TryGetProperty(name, out var v) ? v.GetString() : null;

        Assert.Equal(errorCode, Str("errorCode"));

        return new Refusal(Str("errorCode"), Str("messageKey"), body);
    }

    private sealed record Stand(int ProjectId, int[] RoleIds);

    /// <summary>Проєкт і <paramref name="roles"/> власних ролей-кроків (без прав).</summary>
    private async Task<Stand> ArrangeAsync(int roles)
    {
        var document = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(rowCount: 1).ConfigureAwait(false);

        await using var db = Db();
        var ids = new List<int>();

        for (var i = 0; i < roles; i++)
        {
            var role = new Role(
                EcrCode.Create($"AR{Guid.NewGuid():N}"[..14]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Approval route step" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);
            ids.Add(role.Id);
        }

        return new Stand(document.ProjectId, [.. ids]);
    }

    /// <summary>Користувач із власною роллю: права <paramref name="permissions"/> і грант на проєкт.</summary>
    private async Task<HttpClient> SignedInAsync(
        EcrApiFactory app, string[] permissions, int projectId, GrantLevel level)
    {
        var name = $"ar_{Guid.NewGuid():N}"[..20];

        await using (var db = Db())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(
                EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Approval route test" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, level));
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

    private EcrDbContext Db()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
