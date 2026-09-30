// tests/Ecr.Api.Tests/RoleAssignmentScopeTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ФВ-6.14: призначення ролі з областю дії дає права ЛИШЕ в перелічених
/// проєктах; без області — як і раніше, в усіх.
/// </summary>
/// <remarks>
/// ⛔ Сценарій — два проєкти ОДНОГО шаблону (A і B), документ у B. Вони ділять
/// <c>SheetDefId</c>, тож грант на аркуш ролі «лише в A» — рівно те, що
/// спільна мапа грантів роздала б і в B.
///
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: у <c>AccessDecisionService.LoadAsync</c> вважати всі
/// призначення безобласними (<c>a.ScopeJson is null</c> → <c>true</c>) — тести
/// «не діє в B» дають <c>204</c>/<c>200</c> і червоніють.
/// </remarks>
[Collection("SqlServer")]
public sealed class RoleAssignmentScopeTests(SqlServerFixture sql)
{
    private const string Password = "Role-Scope-Fv614-2026!";

    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Роль має гранти на обидва проєкти, але область — лише A.</summary>
    [Theory]
    [InlineData("approve")]
    [InlineData("submit")]
    [InlineData("patch")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Роль_з_областю_A_не_діє_в_проєкті_B(string action)
    {
        var scenario = await ArrangeAsync(action == "approve" ? DocumentStatus.Submitted : null).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var (client, _) = await SignedInAsync(
                app,
                new RoleSpec(
                    [(ResourceKind.Project, scenario.ProjectA, GrantLevel.Approve),
                     (ResourceKind.Project, scenario.B.ProjectId, GrantLevel.Approve)],
                    Scope: [scenario.ProjectA]))
            .ConfigureAwait(true);

        using (client)
        {
            var response = await ActAsync(client, action, scenario.B).ConfigureAwait(true);

            await AssertInvisibleAsync(response, app).ConfigureAwait(true);
        }

        Assert.Equal(
            action == "approve" ? DocumentStatus.Submitted : (DocumentStatus?)null,
            await StatusAsync(scenario.B).ConfigureAwait(true));
        Assert.False(await CellWrittenAsync(scenario.B).ConfigureAwait(true));
    }

    /// <summary>Контроль: та сама роль з областю B і без області — діє в B.</summary>
    /// <remarks>⛔ Без нього «фікс» «роль з областю не дає нічого» пройшов би тест вище.</remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Роль_з_областю_B_або_без_області_діє_в_проєкті_B(bool scoped)
    {
        var scenario = await ArrangeAsync(DocumentStatus.Submitted).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var (client, _) = await SignedInAsync(
                app,
                new RoleSpec(
                    [(ResourceKind.Project, scenario.B.ProjectId, GrantLevel.Approve)],
                    Scope: scoped ? [scenario.B.ProjectId] : null))
            .ConfigureAwait(true);

        using (client)
        {
            var response = await ActAsync(client, "approve", scenario.B).ConfigureAwait(true);

            Assert.True(
                response.StatusCode == HttpStatusCode.NoContent,
                $"approve у B: {(int)response.StatusCode}\n{await response.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");
        }

        Assert.Equal(DocumentStatus.Approved, await StatusAsync(scenario.B).ConfigureAwait(true));
    }

    /// <summary>
    /// Грант на АРКУШ ролі з областю A не діє у ВИДИМОМУ проєкті B.
    /// </summary>
    /// <remarks>
    /// ⛔ Найтонше місце: B видимий через іншу роль (без області), тож S2
    /// («лише у видимому проєкті») тут не рятує — рятує лише те, що гранти
    /// ролі з областю на аркуш лягають у шар свого проєкту, а не в спільну мапу.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Грант_на_аркуш_ролі_з_областю_діє_лише_в_її_проєкті(bool scopeIsB)
    {
        var scenario = await ArrangeAsync(DocumentStatus.Submitted).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var (client, _) = await SignedInAsync(
                app,
                new RoleSpec([(ResourceKind.Project, scenario.B.ProjectId, GrantLevel.Read)], Scope: null),
                new RoleSpec(
                    [(ResourceKind.Sheet, scenario.B.SheetDefId, GrantLevel.Approve)],
                    Scope: [scopeIsB ? scenario.B.ProjectId : scenario.ProjectA]))
            .ConfigureAwait(true);

        using (client)
        {
            var response = await ActAsync(client, "approve", scenario.B).ConfigureAwait(true);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

            if (scopeIsB)
            {
                Assert.True(response.StatusCode == HttpStatusCode.NoContent, $"{(int)response.StatusCode}\n{body}\n{app.ErrorsText}");
            }
            else
            {
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{(int)response.StatusCode}\n{body}\n{app.ErrorsText}");
            }
        }

        Assert.Equal(
            scopeIsB ? DocumentStatus.Approved : DocumentStatus.Submitted,
            await StatusAsync(scenario.B).ConfigureAwait(true));
    }

    /// <summary>Симуляція бачить той самий ефективний профіль з областю, що й суб'єкт.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Симуляція_бачить_область_дії_суб_єкта(bool scopeIsB)
    {
        var scenario = await ArrangeAsync(state: null).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var (subjectClient, subjectId) = await SignedInAsync(
                app,
                new RoleSpec([], Scope: null, "Document.View"),
                new RoleSpec(
                    [(ResourceKind.Project, scenario.ProjectA, GrantLevel.Read),
                     (ResourceKind.Project, scenario.B.ProjectId, GrantLevel.Read)],
                    Scope: [scopeIsB ? scenario.B.ProjectId : scenario.ProjectA]))
            .ConfigureAwait(true);
        subjectClient.Dispose();

        var (actor, _) = await SignedInAsync(app, new RoleSpec([], Scope: null, "Security.Simulate"))
            .ConfigureAwait(true);

        using (actor)
        {
            var start = await actor.PostAsJsonAsync(
                new Uri("/api/v1/security/simulation", UriKind.Relative),
                new { subjectUserId = subjectId, reason = "ФВ-6.14" }).ConfigureAwait(true);
            Assert.True(start.StatusCode == HttpStatusCode.Created, $"{(int)start.StatusCode}\n{app.ErrorsText}");

            var response = await actor.GetAsync(
                new Uri($"/api/v1/documents/{scenario.B.DocumentId}", UriKind.Relative)).ConfigureAwait(true);

            if (scopeIsB)
            {
                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}\n{app.ErrorsText}");
            }
            else
            {
                await AssertInvisibleAsync(response, app).ConfigureAwait(true);
            }
        }
    }

    /// <summary>
    /// Адміністратор задає область через <c>PUT /users/{id}/roles</c>: вона
    /// зберігається, переживає збереження форми без поля й знімається явно.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Область_задається_зберігається_й_знімається_через_API()
    {
        var scenario = await ArrangeAsync(state: null).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var (admin, _) = await SignedInAsync(
                app,
                new RoleSpec(
                    [(ResourceKind.Project, scenario.B.ProjectId, GrantLevel.Manage)], Scope: null, "Security.ManageUsers"))
            .ConfigureAwait(true);
        var (target, role) = await TargetAsync().ConfigureAwait(true);

        using (admin)
        {
            var put = await PutRolesAsync(admin, target, new
            {
                roleCodes = new[] { role },
                scopes = new Dictionary<string, object> { [role] = new { projects = new[] { scenario.B.ProjectId } } },
            }).ConfigureAwait(true);
            Assert.True(put.StatusCode == HttpStatusCode.OK, $"{(int)put.StatusCode}\n{await put.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");
            Assert.Equal($"{{\"projects\":[{scenario.B.ProjectId}]}}", await ScopeJsonAsync(target).ConfigureAwait(true));
            Assert.Contains(
                $"\"scopes\":{{\"{role}\":[{scenario.B.ProjectId}]}}",
                await LastRolesEventAsync(target).ConfigureAwait(true),
                StringComparison.Ordinal);

            // Клієнт, що про області не знає, не розширює роль до всіх проєктів.
            var keep = await PutRolesAsync(admin, target, new { roleCodes = new[] { role } }).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, keep.StatusCode);
            Assert.Equal($"{{\"projects\":[{scenario.B.ProjectId}]}}", await ScopeJsonAsync(target).ConfigureAwait(true));

            // Явний словник без ролі — область знято.
            var clear = await PutRolesAsync(
                admin, target, new { roleCodes = new[] { role }, scopes = new Dictionary<string, object>() }).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
            Assert.Null(await ScopeJsonAsync(target).ConfigureAwait(true));
        }
    }

    /// <summary>
    /// Неіснуючий і невидимий проєкт — однаково <c>422</c>; видимий без
    /// <c>Manage</c> — <c>403</c>. Нічого не записано.
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("invisible")]
    [InlineData("readOnly")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Область_на_чужий_або_неіснуючий_проєкт_відхиляється(string kind)
    {
        var scenario = await ArrangeAsync(state: null).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var (admin, _) = await SignedInAsync(
                app,
                new RoleSpec(
                    [(ResourceKind.Project, scenario.B.ProjectId, GrantLevel.Read)], Scope: null, "Security.ManageUsers"))
            .ConfigureAwait(true);
        var (target, role) = await TargetAsync().ConfigureAwait(true);

        var projectId = kind switch
        {
            "missing" => int.MaxValue,
            "invisible" => scenario.ProjectA,
            _ => scenario.B.ProjectId,
        };

        using (admin)
        {
            var put = await PutRolesAsync(admin, target, new
            {
                roleCodes = new[] { role },
                scopes = new Dictionary<string, object> { [role] = new { projects = new[] { projectId } } },
            }).ConfigureAwait(true);
            var body = await put.Content.ReadAsStringAsync().ConfigureAwait(true);
            var root = JsonDocument.Parse(body).RootElement;

            if (kind == "readOnly")
            {
                Assert.True(put.StatusCode == HttpStatusCode.Forbidden, $"{(int)put.StatusCode}\n{body}");
                Assert.Equal("ECR-AUTH-0403", root.GetProperty("errorCode").GetString());
                Assert.Equal("err.ECR-AUTH-0403.noProjectManageGrant", root.GetProperty("messageKey").GetString());
            }
            else
            {
                Assert.True(put.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)put.StatusCode}\n{body}");
                Assert.Equal("ECR-REQ-0422", root.GetProperty("errorCode").GetString());
            }
        }

        await using var db = Context();
        Assert.False(await db.RoleAssignments.AnyAsync(a => a.UserId == target).ConfigureAwait(true));
    }

    /// <summary>Групове призначення з областю: зберігається й повертається в переліку.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Групове_призначення_несе_область_дії()
    {
        var scenario = await ArrangeAsync(state: null).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var (admin, _) = await SignedInAsync(
                app,
                new RoleSpec(
                    [(ResourceKind.Project, scenario.B.ProjectId, GrantLevel.Manage)], Scope: null, "Security.ManageUsers"))
            .ConfigureAwait(true);
        var (_, role) = await TargetAsync().ConfigureAwait(true);
        var roleId = await RoleIdAsync(role).ConfigureAwait(true);
        var unique = BitConverter.ToUInt32(Guid.NewGuid().ToByteArray(), 0);
        var sid = $"S-1-5-21-614-{unique}-1105";

        using (admin)
        {
            var post = await admin.PostAsJsonAsync(
                new Uri("/api/v1/security/group-assignments", UriKind.Relative),
                new { roleId, principal = sid, scope = new { projects = new[] { scenario.B.ProjectId } } })
                .ConfigureAwait(true);
            Assert.True(post.StatusCode == HttpStatusCode.Created, $"{(int)post.StatusCode}\n{await post.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");

            var list = JsonDocument.Parse(await admin.GetStringAsync(
                new Uri("/api/v1/security/group-assignments", UriKind.Relative)).ConfigureAwait(true)).RootElement;

            var row = list.EnumerateArray().Single(r => r.GetProperty("principalSid").GetString() == sid);
            Assert.Equal(
                scenario.B.ProjectId,
                row.GetProperty("scope").GetProperty("projects").EnumerateArray().Single().GetInt32());
        }
    }

    /// <summary>Специфікація ролі тестового користувача.</summary>
    /// <param name="Grants">Гранти ролі.</param>
    /// <param name="Scope">Область призначення; <c>null</c> — без області.</param>
    /// <param name="Permissions">Функціональні права ролі.</param>
    private sealed record RoleSpec(
        (ResourceKind Kind, int Id, GrantLevel Level)[] Grants, int[]? Scope, params string[] Permissions);

    private Task<HttpResponseMessage> ActAsync(HttpClient client, string action, TestDocument b)
        => action switch
        {
            "approve" => client.PostAsJsonAsync(
                new Uri($"/api/v1/documents/{b.DocumentId}/approve", UriKind.Relative),
                new { sheetDefId = b.SheetDefId, periodKey = b.PeriodKey.Value, approved = true, reason = (string?)null }),
            "submit" => client.PostAsJsonAsync(
                new Uri($"/api/v1/documents/{b.DocumentId}/submit", UriKind.Relative),
                new { sheetDefId = b.SheetDefId, periodKey = b.PeriodKey.Value }),
            _ => PatchAsync(client, b),
        };

    private async Task<HttpResponseMessage> PatchAsync(HttpClient client, TestDocument b)
    {
        var (rowKey, columnCode) = await RowAndColumnAsync(b).ConfigureAwait(false);

        return await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{b.DocumentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = b.TableInstanceId,
                periodKey = b.PeriodKey.Value,
                origin = "UserEdit",
                rows = new[]
                {
                    new { rowKey, baseVersion = (string?)null, cells = new object[] { new { columnCode, value = (object)42m } } },
                },
            }).ConfigureAwait(false);
    }

    /// <summary>Невидимий документ — <c>404 ECR-DOC-0404</c>, як неіснуючий (B-08).</summary>
    private static async Task AssertInvisibleAsync(HttpResponseMessage response, EcrApiFactory app)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(
            response.StatusCode == HttpStatusCode.NotFound,
            $"очікували 404, отримали {(int)response.StatusCode}\n{body}\n{app.ErrorsText}");
        Assert.Equal("ECR-DOC-0404", JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString());
    }

    /// <summary>Два проєкти одного шаблону; документ — у B.</summary>
    private async Task<(TestDocument B, int ProjectA)> ArrangeAsync(DocumentStatus? state)
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(false);

        await using var db = Context();
        var tag = $"{Guid.NewGuid():N}"[..10];
        var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync().ConfigureAwait(false);

        var projectA = new Project(
            EcrCode.Create($"PA{tag}"), Name("FV-6.14 project A"),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            b.TemplateVersionId, PeriodKind.Monthly, policyId, "Asia/Almaty");
        db.Projects.Add(projectA);

        // Аркуш у складі документа B: без цього подання відмовляє раніше за права.
        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, b.SheetDefId));

        if (state is not null)
        {
            // Подав bootstrap (Id 1): інакше затвердження зупинило б правило
            // чотирьох очей, а не область дії.
            var approval = new ApprovalState(b.DocumentId, b.SheetDefId, b.PeriodKey.Value);
            approval.Submit(1, Now);
            db.ApprovalStates.Add(approval);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return (b, projectA.Id);
    }

    private async Task<DocumentStatus?> StatusAsync(TestDocument document)
    {
        await using var db = Context();

        return await db.ApprovalStates
            .AsNoTracking()
            .Where(a => a.DocumentId == document.DocumentId
                        && a.SheetDefId == document.SheetDefId
                        && a.PeriodKey == document.PeriodKey.Value)
            .Select(a => (DocumentStatus?)a.Status)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
    }

    private async Task<bool> CellWrittenAsync(TestDocument document)
    {
        await using var db = Context();
        var column = document.ColumnDefIds[1];

        return await db.CellValues.AnyAsync(
            c => c.PeriodKeyValue == document.PeriodKey.Value && c.ColumnDefId == column).ConfigureAwait(false);
    }

    private async Task<(string RowKey, string ColumnCode)> RowAndColumnAsync(TestDocument document)
    {
        await using var db = Context();

        var rowKey = await db.TableRows
            .AsNoTracking()
            .Where(r => r.PeriodKeyValue == document.PeriodKey.Value && r.Id == document.RowIds[0])
            .Select(r => r.RowKeyValue)
            .SingleAsync()
            .ConfigureAwait(false);

        var column = document.ColumnDefIds[1];
        var code = await db.ColumnDefs
            .AsNoTracking()
            .Where(c => c.Id == column)
            .Select(c => c.Code)
            .SingleAsync()
            .ConfigureAwait(false);

        return (rowKey, code);
    }

    /// <summary>Клієнт із сеансом і ролями з названими грантами й областями.</summary>
    private async Task<(HttpClient Client, int UserId)> SignedInAsync(EcrApiFactory app, params RoleSpec[] roles)
    {
        var name = $"fv614_{Guid.NewGuid():N}"[..20];
        int userId;

        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);
            userId = user.Id;

            foreach (var spec in roles)
            {
                var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("FV-6.14 role"));
                db.Roles.Add(role);
                await db.SaveChangesAsync().ConfigureAwait(false);

                foreach (var permission in spec.Permissions)
                {
                    db.RolePermissions.Add(new RolePermission(role.Id, permission));
                }

                var assignment = new RoleAssignment(role.Id, user.Id, principalSid: null);
                if (spec.Scope is { } scope)
                {
                    assignment.SetScope(RoleAssignmentScope.Create(scope));
                }

                db.RoleAssignments.Add(assignment);
                foreach (var (kind, id, level) in spec.Grants)
                {
                    db.ResourceGrants.Add(new ResourceGrant(role.Id, kind, id, level));
                }

                await db.SaveChangesAsync().ConfigureAwait(false);
            }
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return (client, userId);
    }

    /// <summary>Користувач без ролей і роль, яку йому призначатимуть.</summary>
    private async Task<(int UserId, string RoleCode)> TargetAsync()
    {
        await using var db = Context();

        var name = $"fv614t_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("FV-6.14 target role"));
        db.Users.Add(user);
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (user.Id, role.Code);
    }

    private static Task<HttpResponseMessage> PutRolesAsync(HttpClient client, int userId, object body)
        => client.PutAsJsonAsync(new Uri($"/api/v1/users/{userId}/roles", UriKind.Relative), body);

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

    private async Task<string> LastRolesEventAsync(int userId)
    {
        await using var db = Context();

        return await db.Database
            .SqlQuery<string>($"""
                SELECT TOP (1) DetailsJson AS Value FROM aud.SecurityEvent
                WHERE EventType = N'UserRolesReplaced' AND TargetUserId = {userId}
                ORDER BY ChangedAt DESC
                """)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
