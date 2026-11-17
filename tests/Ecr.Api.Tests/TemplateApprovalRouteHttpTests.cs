// tests/Ecr.Api.Tests/TemplateApprovalRouteHttpTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
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
/// ✎ B155 (варіант C, 16.11): маршрут ШАБЛОНУ в усіх проєктах і виняток проєкту по HTTP — права, перезапуск поданих
/// аркушів з першого кроку, підрахунок (C-8) з видимістю, перелік ролей і шаблонів проєкту.
/// </summary>
/// <remarks>
/// Свої дані: чотири документи (період 2099xx) — A (проєкт P1, шаблон T), B (P2, власний шаблон документа T, B74),
/// C (P3, інший шаблон), D (P4, власний T, затверджений). Прибирання — у finally.
/// ⛔ МУТАЦІЇ: <c>ApprovalRouteImpact.ToDto</c> без фільтра видимості — червоніє «видимих 1 із 2»; <c>ScopeGoverns</c>
/// без перевірки конкретнішого — червоніє «виняток P2 не перезапускається»; <c>AuthorizeAsync</c> з
/// <c>Template.View</c> на запис — червоніє «Template.View → 403»; без <c>RequireProjectManageAsync</c>-гранта —
/// червоніє «менеджер без гранта → 403».
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateApprovalRouteHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-B155-Template-Route-2026!";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "B155")]
    public async Task Маршрут_шаблону_перезапускає_лише_подані_аркуші_областей_і_рахує_видимі()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        try
        {
            using var app = new EcrApiFactory(sql);
            using var admin = await SignedInAsync(app, ["Template.Edit"], (stand.A.ProjectId, GrantLevel.Read)).ConfigureAwait(true);
            using var adminAll = await SignedInAsync(
                app, ["Template.Edit"], (stand.A.ProjectId, GrantLevel.Read), (stand.B.ProjectId, GrantLevel.Read)).ConfigureAwait(true);
            using var adminNone = await SignedInAsync(app, ["Template.Edit"]).ConfigureAwait(true);
            using var manager = await SignedInAsync(app, ["Project.Manage"], (stand.B.ProjectId, GrantLevel.Manage)).ConfigureAwait(true);

            // C-8: підрахунок до збереження — усього 2 (A і B), видимість — своя в кожного (Allow/Deny).
            var all = await PostAsync(app, adminAll, TemplateRoute(stand.T) + "/preview", new { roleIds = new[] { stand.C1 } }).ConfigureAwait(true);
            var one = await PostAsync(app, admin, TemplateRoute(stand.T) + "/preview", new { roleIds = new[] { stand.C1 } }).ConfigureAwait(true);
            var none = await PostAsync(app, adminNone, TemplateRoute(stand.T) + "/preview", new { roleIds = new[] { stand.C1 } }).ConfigureAwait(true);
            Assert.Equal((2, 2), (all.GetProperty("total").GetInt32(), all.GetProperty("visible").GetInt32()));
            Assert.Equal((2, 1), (one.GetProperty("total").GetInt32(), one.GetProperty("visible").GetInt32()));
            Assert.Equal((2, 0), (none.GetProperty("total").GetInt32(), none.GetProperty("visible").GetInt32()));
            Assert.Equal([stand.A.DocumentId], Documents(one.GetProperty("sheets")));
            Assert.Empty(Documents(none.GetProperty("sheets")));

            // PUT (–,T) [C1]: A і B — на перший крок нового маршруту; C (інший шаблон) і D (затверджений) — без змін.
            var put = await PutAsync(app, admin, TemplateRoute(stand.T), new[] { stand.C1 }).ConfigureAwait(true);
            Assert.Equal(1, put.GetProperty("steps").GetInt32());
            Assert.Equal(2, put.GetProperty("restarted").GetProperty("total").GetInt32());
            Assert.Equal([stand.A.DocumentId], Documents(put.GetProperty("restarted").GetProperty("sheets")));

            var templateFirst = await FirstStepAsync(ApprovalRoute.ScopeCode(null, stand.T)).ConfigureAwait(true);
            Assert.Equal(templateFirst, await StepOfAsync(stand.A).ConfigureAwait(true));
            Assert.Equal(templateFirst, await StepOfAsync(stand.B).ConfigureAwait(true));
            Assert.Equal(stand.CStep, await StepOfAsync(stand.C).ConfigureAwait(true));
            Assert.Null(await StepOfAsync(stand.D).ConfigureAwait(true));
            Assert.Equal(DocumentStatus.Approved, await StatusOfAsync(stand.D).ConfigureAwait(true));

            // Виняток проєкту P2 (менеджер P2): перезапускає лише B.
            var overridePut = await PutAsync(app, manager, ProjectTemplateRoute(stand.B.ProjectId, stand.T), new[] { stand.C2 }).ConfigureAwait(true);
            Assert.Equal(1, overridePut.GetProperty("restarted").GetProperty("total").GetInt32());
            var overrideFirst = await FirstStepAsync(ApprovalRoute.ScopeCode(stand.B.ProjectId, stand.T)).ConfigureAwait(true);
            Assert.Equal(overrideFirst, await StepOfAsync(stand.B).ConfigureAwait(true));
            Assert.Equal(templateFirst, await StepOfAsync(stand.A).ConfigureAwait(true));

            // Зміна (–,T) тепер не торкається P2 (виняток): лише A.
            var second = await PutAsync(app, admin, TemplateRoute(stand.T), new[] { stand.C2 }).ConfigureAwait(true);
            Assert.Equal(1, second.GetProperty("restarted").GetProperty("total").GetInt32());
            Assert.Equal(overrideFirst, await StepOfAsync(stand.B).ConfigureAwait(true));
            Assert.Equal(
                await FirstStepAsync(ApprovalRoute.ScopeCode(null, stand.T)).ConfigureAwait(true),
                await StepOfAsync(stand.A).ConfigureAwait(true));

            // Черга «До погодження» — те саме правило: загальний Approver і носій ролі кроку бачать A, інший — ні.
            using var general = await SignedInWithRolesAsync(
                app, ["Document.View"], [stand.GeneralApprover], (stand.A.ProjectId, GrantLevel.Approve)).ConfigureAwait(true);
            using var stepHolder = await SignedInWithRolesAsync(
                app, ["Document.View"], [stand.C2], (stand.A.ProjectId, GrantLevel.Approve)).ConfigureAwait(true);
            using var neither = await SignedInAsync(app, ["Document.View"], (stand.A.ProjectId, GrantLevel.Approve)).ConfigureAwait(true);
            Assert.Contains(stand.A.DocumentId, await QueueAsync(app, general).ConfigureAwait(true));
            Assert.Contains(stand.A.DocumentId, await QueueAsync(app, stepHolder).ConfigureAwait(true));
            Assert.DoesNotContain(stand.A.DocumentId, await QueueAsync(app, neither).ConfigureAwait(true));

            // Той самий набір — нічого не змінюється, нікого не перезапущено.
            var same = await PutAsync(app, admin, TemplateRoute(stand.T), new[] { stand.C2 }).ConfigureAwait(true);
            Assert.Equal(0, same.GetProperty("restarted").GetProperty("total").GetInt32());

            // Читання: власний маршрут, загальний Approver теж затверджує.
            var route = await GetAsync(app, admin, TemplateRoute(stand.T)).ConfigureAwait(true);
            Assert.True(route.GetProperty("hasRoute").GetBoolean());
            Assert.Equal("own", route.GetProperty("effectiveSource").GetString());
            Assert.True(route.GetProperty("generalApproverAlsoApproves").GetBoolean());
            Assert.Equal(stand.GeneralApprover, route.GetProperty("generalApproverRoleId").GetInt32());
            Assert.True(route.GetProperty("canEdit").GetBoolean());
            Assert.Equal([stand.C2], route.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("roleId").GetInt32()));

            // C-5: шаблони P2 — основний (T2, маршруту немає) і T (виняток проєкту).
            using var list = await manager.GetAsync(new Uri($"/api/v1/projects/{stand.B.ProjectId}/template-routes", UriKind.Relative)).ConfigureAwait(true);
            var listBody = await list.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(list.StatusCode == HttpStatusCode.OK, $"{(int)list.StatusCode}: {listBody}\n{app.ErrorsText}");
            using var listJson = JsonDocument.Parse(listBody);
            var byTemplate = listJson.RootElement.EnumerateArray().ToDictionary(
                e => e.GetProperty("templateId").GetInt32(), e => e.GetProperty("effectiveSource").GetString());
            Assert.Equal("own", byTemplate[stand.T]);
            Assert.Equal("none", byTemplate[stand.B.TemplateId]);
        }
        finally
        {
            await CleanupAsync(stand).ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "B155")]
    public async Task Права_маршрут_шаблону_лише_Template_Edit_виняток_лише_менеджер_з_грантом_типовий_лише_ManageRoles()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        var hadDefault = await DefaultRouteExistsAsync().ConfigureAwait(true);
        try
        {
            using var app = new EcrApiFactory(sql);
            using var viewer = await SignedInAsync(app, ["Template.View"]).ConfigureAwait(true);
            using var manager = await SignedInAsync(app, ["Project.Manage"], (stand.B.ProjectId, GrantLevel.Manage)).ConfigureAwait(true);
            using var managerRead = await SignedInAsync(app, ["Project.Manage"], (stand.B.ProjectId, GrantLevel.Read)).ConfigureAwait(true);
            using var templateAdmin = await SignedInAsync(app, ["Template.Edit"]).ConfigureAwait(true);
            using var sysAdmin = await SignedInAsync(app, ["Security.ManageRoles"]).ConfigureAwait(true);
            using var plain = await SignedInAsync(app, ["Document.View"]).ConfigureAwait(true);

            // Маршрут шаблону: Template.View читає (без права змінювати), але не пише; менеджер проєкту — теж ні.
            var read = await GetAsync(app, viewer, TemplateRoute(stand.T)).ConfigureAwait(true);
            Assert.False(read.GetProperty("canEdit").GetBoolean());
            await RefusedAsync(app, viewer, HttpMethod.Put, TemplateRoute(stand.T), new { roleIds = new[] { stand.C1 } }, "Template.Edit").ConfigureAwait(true);
            await RefusedAsync(app, manager, HttpMethod.Put, TemplateRoute(stand.T), new { roleIds = new[] { stand.C1 } }, "Template.Edit").ConfigureAwait(true);
            await RefusedAsync(app, viewer, HttpMethod.Post, TemplateRoute(stand.T) + "/preview", new { roleIds = (int[]?)null }, "Template.Edit").ConfigureAwait(true);

            // Виняток проєкту: право є, а гранта лише Read — 403; TemplateAdmin без Project.Manage — 403.
            await RefusedAsync(app, managerRead, HttpMethod.Put, ProjectTemplateRoute(stand.B.ProjectId, stand.T), new { roleIds = new[] { stand.C1 } }, null).ConfigureAwait(true);
            await RefusedAsync(app, templateAdmin, HttpMethod.Put, ProjectTemplateRoute(stand.B.ProjectId, stand.T), new { roleIds = new[] { stand.C1 } }, null, HttpStatusCode.Forbidden, HttpStatusCode.NotFound).ConfigureAwait(true);

            // Типовий майданчика — лише Security.ManageRoles (запис не перевіряємо: він діяв би на всю спільну тестову базу).
            await RefusedAsync(app, templateAdmin, HttpMethod.Put, new Uri("/api/v1/approval-route/default", UriKind.Relative).ToString(), new { roleIds = new[] { stand.C1 } }, "Security.ManageRoles").ConfigureAwait(true);
            await RefusedAsync(app, templateAdmin, HttpMethod.Get, "/api/v1/approval-route/default", null, "Security.ManageRoles").ConfigureAwait(true);
            var defaultRoute = await GetAsync(app, sysAdmin, "/api/v1/approval-route/default").ConfigureAwait(true);
            Assert.True(defaultRoute.GetProperty("canEdit").GetBoolean());

            // Ролі для кроків: TemplateAdmin бачить (і загальний Approver позначено), простий користувач — 403.
            using var roles = await templateAdmin.GetAsync(new Uri("/api/v1/roles/approvers", UriKind.Relative)).ConfigureAwait(true);
            var rolesBody = await roles.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(roles.StatusCode == HttpStatusCode.OK, $"{(int)roles.StatusCode}: {rolesBody}\n{app.ErrorsText}");
            using (var json = JsonDocument.Parse(rolesBody))
            {
                var general = json.RootElement.EnumerateArray().Single(r => r.GetProperty("isGeneralApprover").GetBoolean());
                Assert.Equal(stand.GeneralApprover, general.GetProperty("id").GetInt32());
                Assert.Contains(json.RootElement.EnumerateArray(), r => r.GetProperty("id").GetInt32() == stand.C1);
            }

            await RefusedAsync(app, plain, HttpMethod.Get, "/api/v1/roles/approvers", null, null).ConfigureAwait(true);

            // Власну роль створює лише SysAdmin (Security.ManageRoles): TemplateAdmin — 403.
            await RefusedAsync(
                app, templateAdmin, HttpMethod.Post, "/api/v1/roles",
                new { code = $"B155X{Guid.NewGuid():N}"[..14], nameL10n = new Dictionary<string, string> { ["en"] = "x" }, permissionCodes = Array.Empty<string>() },
                "Security.ManageRoles").ConfigureAwait(true);

            // Ні запис, ні відмови маршруту не створили.
            await using var db = Db();
            Assert.False(await db.Set<ApprovalRoute>().AnyAsync(r => r.TemplateId == stand.T).ConfigureAwait(true));
        }
        finally
        {
            await CleanupAsync(stand).ConfigureAwait(true);

            // ⚠ Під мутацією права типового маршруту PUT пройшов би й лишив маршрут майданчика для ВСІЄЇ спільної бази.
            if (!hadDefault)
            {
                var code = ApprovalRoute.ScopeCode(null, null);
                await ExecuteAsync($"""
                    DELETE s FROM wf.ApprovalStep s JOIN wf.ApprovalRoute r ON r.Id = s.ApprovalRouteId WHERE r.Code = N'{code}';
                    DELETE FROM wf.ApprovalRoute WHERE Code = N'{code}';
                    """).ConfigureAwait(true);
            }
        }
    }

    private static string TemplateRoute(int templateId) => $"/api/v1/templates/{templateId}/approval-route";

    private static string ProjectTemplateRoute(int projectId, int templateId)
        => $"/api/v1/projects/{projectId}/templates/{templateId}/approval-route";

    private static async Task<long[]> QueueAsync(EcrApiFactory app, HttpClient client)
    {
        var queue = await GetAsync(app, client, "/api/v1/me/approval-queue").ConfigureAwait(false);
        return [.. queue.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("documentId").GetInt64())];
    }

    private static long[] Documents(JsonElement sheets)
        => [.. sheets.EnumerateArray().Select(s => s.GetProperty("documentId").GetInt64()).Order()];

    private static async Task<JsonElement> GetAsync(EcrApiFactory app, HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false);
        return await OkAsync(app, response).ConfigureAwait(false);
    }

    private static async Task<JsonElement> PutAsync(EcrApiFactory app, HttpClient client, string url, int[] roleIds)
    {
        using var response = await client.PutAsJsonAsync(new Uri(url, UriKind.Relative), new { roleIds }).ConfigureAwait(false);
        return await OkAsync(app, response).ConfigureAwait(false);
    }

    private static async Task<JsonElement> PostAsync(EcrApiFactory app, HttpClient client, string url, object body)
    {
        using var response = await client.PostAsJsonAsync(new Uri(url, UriKind.Relative), body).ConfigureAwait(false);
        return await OkAsync(app, response).ConfigureAwait(false);
    }

    private static async Task<JsonElement> OkAsync(EcrApiFactory app, HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}\n{app.ErrorsText}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.Clone();
    }

    private static async Task RefusedAsync(
        EcrApiFactory app,
        HttpClient client,
        HttpMethod method,
        string url,
        object? body,
        string? permission,
        params HttpStatusCode[] allowed)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        HttpStatusCode[] expected = allowed.Length == 0 ? [HttpStatusCode.Forbidden] : allowed;
        Assert.True(
            expected.Contains(response.StatusCode),
            $"{method} {url}: очікували {string.Join("/", expected)}, отримали {(int)response.StatusCode}\n{text}\n{app.ErrorsText}");
        if (permission is not null)
        {
            Assert.Contains(permission, text, StringComparison.Ordinal);
        }
    }

    private sealed record StandData(
        TestDocument A,
        TestDocument B,
        TestDocument C,
        TestDocument D,
        int T,
        int C1,
        int C2,
        int GeneralApprover,
        int CStep,
        int CRoute,
        string ExtraRoute);

    private async Task<StandData> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var a = await builder.BuildAsync(periodKey: 209902, columnCount: 2, rowCount: 1).ConfigureAwait(false);
        var b = await builder.BuildAsync(periodKey: 209902, columnCount: 2, rowCount: 1).ConfigureAwait(false);
        var c = await builder.BuildAsync(periodKey: 209902, columnCount: 2, rowCount: 1).ConfigureAwait(false);
        var d = await builder.BuildAsync(periodKey: 209902, columnCount: 2, rowCount: 1).ConfigureAwait(false);
        var t = a.TemplateId;

        // B і D — документи ВЛАСНОГО шаблону T у своїх проєктах (B74): шаблон документа T, версія — шкала (P, T).
        await ExecuteAsync($"""
            UPDATE doc.Document SET TemplateId = {t} WHERE Id IN ({b.DocumentId}, {d.DocumentId});
            INSERT doc.ProjectTemplate (ProjectId, TemplateId, CurrentTemplateVersionId)
            SELECT x.ProjectId, x.TemplateId, x.VersionId
            FROM (VALUES ({b.ProjectId}, {b.TemplateId}, {b.TemplateVersionId}), ({b.ProjectId}, {t}, {a.TemplateVersionId}),
                         ({d.ProjectId}, {d.TemplateId}, {d.TemplateVersionId}), ({d.ProjectId}, {t}, {a.TemplateVersionId}))
                 AS x (ProjectId, TemplateId, VersionId)
            WHERE NOT EXISTS (SELECT 1 FROM doc.ProjectTemplate p WHERE p.ProjectId = x.ProjectId AND p.TemplateId = x.TemplateId);
            """).ConfigureAwait(false);

        await using var db = Db();
        var general = await db.Roles.Where(r => r.Code == "Approver" && r.IsBuiltIn).Select(r => r.Id).SingleAsync().ConfigureAwait(false);
        var c1 = new Role(EcrCode.Create($"B155H1{Guid.NewGuid():N}"[..14]), Text("Step 1"));
        var c2 = new Role(EcrCode.Create($"B155H2{Guid.NewGuid():N}"[..14]), Text("Step 2"));
        db.Roles.AddRange(c1, c2);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // C — інший шаблон, на кроці маршруту свого проєкту; його крок не має змінитися.
        var extra = $"B155C_{c.ProjectId}";
        var cRoute = new ApprovalRoute(EcrCode.Create(extra), Text(extra), c.ProjectId);
        cRoute.AddStep(c1.Id);
        db.ApprovalRoutes.Add(cRoute);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var at = new DateTime(2099, 2, 10, 0, 0, 0, DateTimeKind.Utc);
        foreach (var doc in new[] { a, b, c, d })
        {
            var state = new ApprovalState(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value);
            state.Submit(1, at, doc == c ? cRoute.Steps[0].Id : null);
            if (doc == d)
            {
                state.Approve(1, at);
            }

            db.ApprovalStates.Add(state);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new StandData(a, b, c, d, t, c1.Id, c2.Id, general, cRoute.Steps[0].Id, cRoute.Id, extra);
    }

    private async Task CleanupAsync(StandData stand)
    {
        var docs = $"{stand.A.DocumentId},{stand.B.DocumentId},{stand.C.DocumentId},{stand.D.DocumentId}";
        var codes = $"N'{ApprovalRoute.ScopeCode(null, stand.T)}', N'{ApprovalRoute.ScopeCode(stand.B.ProjectId, stand.T)}', N'{stand.ExtraRoute}'";
        await ExecuteAsync($"""
            DELETE FROM wf.ApprovalState WHERE DocumentId IN ({docs});
            DELETE s FROM wf.ApprovalStep s JOIN wf.ApprovalRoute r ON r.Id = s.ApprovalRouteId WHERE r.Code IN ({codes});
            DELETE FROM wf.ApprovalRoute WHERE Code IN ({codes});
            UPDATE doc.Document SET TemplateId = NULL WHERE Id IN ({stand.B.DocumentId}, {stand.D.DocumentId});
            DELETE FROM doc.ProjectTemplate WHERE ProjectId IN ({stand.B.ProjectId}, {stand.D.ProjectId}) AND TemplateId = {stand.T};
            """).ConfigureAwait(false);
    }

    private async Task<bool> DefaultRouteExistsAsync()
    {
        await using var db = Db();
        var code = ApprovalRoute.ScopeCode(null, null);
        return await db.ApprovalRoutes.AnyAsync(r => r.Code == code).ConfigureAwait(false);
    }

    private async Task<int?> StepOfAsync(TestDocument doc)
    {
        await using var db = Db();
        return await db.ApprovalStates.Where(s => s.DocumentId == doc.DocumentId).Select(s => s.CurrentStepId).SingleAsync().ConfigureAwait(false);
    }

    private async Task<DocumentStatus> StatusOfAsync(TestDocument doc)
    {
        await using var db = Db();
        return await db.ApprovalStates.Where(s => s.DocumentId == doc.DocumentId).Select(s => s.Status).SingleAsync().ConfigureAwait(false);
    }

    private async Task<int> FirstStepAsync(string code)
    {
        await using var db = Db();
        return await db.ApprovalSteps
            .Where(s => db.ApprovalRoutes.Any(r => r.Id == s.ApprovalRouteId && r.Code == code))
            .OrderBy(s => s.Ordinal)
            .Select(s => s.Id)
            .FirstAsync()
            .ConfigureAwait(false);
    }

    /// <summary>Користувач із власною роллю: права й гранти на проєкти.</summary>
    private Task<HttpClient> SignedInAsync(
        EcrApiFactory app, string[] permissions, params (int ProjectId, GrantLevel Level)[] grants)
        => SignedInWithRolesAsync(app, permissions, [], grants);

    /// <summary>Те саме й додаткові наявні ролі (наприклад, вбудований Approver).</summary>
    private async Task<HttpClient> SignedInWithRolesAsync(
        EcrApiFactory app, string[] permissions, int[] extraRoles, params (int ProjectId, GrantLevel Level)[] grants)
    {
        var name = $"b155_{Guid.NewGuid():N}"[..20];

        await using (var db = Db())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Text("B155 test"));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            foreach (var (projectId, level) in grants)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, level));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            foreach (var extra in extraRoles)
            {
                db.RoleAssignments.Add(new RoleAssignment(extra, user.Id, principalSid: null));
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password })
            .ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private async Task ExecuteAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private EcrDbContext Db()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
