// tests/Ecr.Api.Tests/ScopedOperatorPermissionTests.cs
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
/// ФВ-6.14: функціональні права ролі з областю діють у проєктах області — і
/// лише там. «Оператор лише проєкту B» = роль Operator{B}.
/// </summary>
/// <remarks>
/// ⛔ Сценарій «чужий проєкт» навмисно жорсткий: документ у B ВИДИМИЙ
/// (роль без області дає грант <c>Approve</c> на B, але жодного
/// функціонального права), а всі функціональні права — з ролі з областю A.
/// Тоді зупинити дію в B може лише проєктна перевірка права
/// (<c>AccessProfile.Has(code, projectId)</c>): гранти її не підмінять.
///
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: у <c>AccessProfile.Has(code, projectId)</c> ігнорувати
/// проєкт (право з будь-якої області) — кожен рядок «чужий» червоніє.
/// </remarks>
[Collection("SqlServer")]
public sealed class ScopedOperatorPermissionTests(SqlServerFixture sql)
{
    private const string Password = "Scoped-Operator-Fv614!";

    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Функціональні права оператора (як DataEntry + Approver, проєктні).</summary>
    private static readonly string[] OperatorPermissions =
    [
        "Document.View", "Document.Create", "Document.Import", "Document.Export", "Document.Reopen",
        "Calculation.View", "Calculation.Recalculate", "Report.BuildSnapshot", "Report.ViewRegulatory",
        "Report.Export",
    ];

    /// <summary>Група дій × власний/чужий проєкт.</summary>
    /// <param name="action">Дія.</param>
    /// <param name="own">Область ролі — проєкт документа (B) чи інший (A).</param>
    [Theory]
    [InlineData("view", true)]
    [InlineData("view", false)]
    [InlineData("tables", true)]
    [InlineData("tables", false)]
    [InlineData("slice", true)]
    [InlineData("slice", false)]
    [InlineData("list", true)]
    [InlineData("list", false)]
    [InlineData("export", true)]
    [InlineData("export", false)]
    [InlineData("recalculate", true)]
    [InlineData("recalculate", false)]
    [InlineData("reopen", true)]
    [InlineData("reopen", false)]
    [InlineData("report", true)]
    [InlineData("report", false)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Право_оператора_діє_лише_в_проєкті_області(string action, bool own)
    {
        var (b, projectA) = await ArrangeAsync(submitted: action == "reopen").ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);

        // own: Operator{B}. Чужий: B видимий через роль без області й без прав,
        // права — лише з Operator{A}.
        var roles = own
            ? new[]
            {
                new RoleSpec([(ResourceKind.Project, b.ProjectId, GrantLevel.Approve)], [b.ProjectId], OperatorPermissions),
            }
            : new[]
            {
                new RoleSpec([(ResourceKind.Project, b.ProjectId, GrantLevel.Approve)], null),
                new RoleSpec([(ResourceKind.Project, projectA, GrantLevel.Approve)], [projectA], OperatorPermissions),
            };

        using var client = await SignedInAsync(app, roles).ConfigureAwait(true);

        var response = await ActAsync(client, action, b).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        var status = (int)response.StatusCode;

        var (expectOwn, expectForeign) = action switch
        {
            "view" => (200, 404),
            "tables" => (200, 403),
            "slice" => (200, 403),
            "list" => (200, 200),
            "export" => (202, 403),
            "recalculate" => (202, 403),
            "reopen" => (204, 403),

            // Невідомий код звіту: 404 означає, що перевірку права й гранта
            // пройдено і обробник дійшов до пошуку версії.
            _ => (404, 403),
        };

        Assert.True(
            status == (own ? expectOwn : expectForeign),
            $"{action} own={own}: {status}\n{body}\n{app.ErrorsText}");

        if (action == "list")
        {
            var ids = JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray()
                .Select(d => d.GetProperty("id").GetInt64())
                .ToList();
            Assert.Equal(own, ids.Contains(b.DocumentId));
        }

        if (action == "report" && own)
        {
            Assert.Equal("ECR-RPT-0404", JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString());
        }
    }

    /// <summary>
    /// Права без проєкту (<c>Security.*</c>) з ролі з областю не діють НІДЕ;
    /// та сама роль без області — діє.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Глобальне_право_з_ролі_з_областю_не_діє(bool scoped)
    {
        var (b, _) = await ArrangeAsync(submitted: false).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(
                app,
                new RoleSpec(
                    [(ResourceKind.Project, b.ProjectId, GrantLevel.Manage)],
                    scoped ? [b.ProjectId] : null,
                    "Security.ManageUsers"))
            .ConfigureAwait(true);

        var response = await client.GetAsync(new Uri("/api/v1/users?limit=5", UriKind.Relative)).ConfigureAwait(true);

        Assert.Equal(scoped ? HttpStatusCode.Forbidden : HttpStatusCode.OK, response.StatusCode);
    }

    private sealed record RoleSpec(
        (ResourceKind Kind, int Id, GrantLevel Level)[] Grants, int[]? Scope, params string[] Permissions);

    private static Task<HttpResponseMessage> ActAsync(HttpClient client, string action, TestDocument b)
        => action switch
        {
            "view" => client.GetAsync(new Uri($"/api/v1/documents/{b.DocumentId}", UriKind.Relative)),
            "tables" => client.GetAsync(
                new Uri($"/api/v1/documents/{b.DocumentId}/tables?periodKey={b.PeriodKey.Value}", UriKind.Relative)),
            "slice" => client.GetAsync(
                new Uri($"/api/v1/documents/{b.DocumentId}/tables/{b.TableInstanceId}", UriKind.Relative)),
            "list" => client.GetAsync(new Uri("/api/v1/documents?limit=200", UriKind.Relative)),
            "export" => client.PostAsJsonAsync(
                new Uri($"/api/v1/documents/{b.DocumentId}/export", UriKind.Relative),
                new { includeFormulas = false, includeStyles = false, language = "en", periodKey = b.PeriodKey.Value }),
            "recalculate" => client.PostAsJsonAsync(
                new Uri($"/api/v1/documents/{b.DocumentId}/recalculate", UriKind.Relative),
                new { periodKey = b.PeriodKey.Value }),
            "reopen" => client.PostAsJsonAsync(
                new Uri($"/api/v1/documents/{b.DocumentId}/reopen", UriKind.Relative),
                new { sheetDefId = b.SheetDefId, periodKey = b.PeriodKey.Value, reason = "ФВ-6.14" }),
            _ => client.PostAsJsonAsync(
                new Uri("/api/v1/reports/NO_SUCH_REPORT_FV614/build", UriKind.Relative),
                new { projectId = b.ProjectId, periodKey = b.PeriodKey.Value }),
        };

    /// <summary>Два проєкти одного шаблону; документ — у B.</summary>
    private async Task<(TestDocument B, int ProjectA)> ArrangeAsync(bool submitted)
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(false);

        await using var db = Context();
        var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync().ConfigureAwait(false);

        var projectA = new Project(
            EcrCode.Create($"PA{Guid.NewGuid():N}"[..12]), Name("FV-6.14 operator A"),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            b.TemplateVersionId, PeriodKind.Monthly, policyId, "Asia/Almaty");
        db.Projects.Add(projectA);
        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, b.SheetDefId));

        if (submitted)
        {
            var approval = new ApprovalState(b.DocumentId, b.SheetDefId, b.PeriodKey.Value);
            approval.Submit(1, Now);
            db.ApprovalStates.Add(approval);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return (b, projectA.Id);
    }

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, params RoleSpec[] roles)
    {
        var name = $"op614_{Guid.NewGuid():N}"[..20];

        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var spec in roles)
            {
                var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("FV-6.14 operator"));
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

        return client;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
