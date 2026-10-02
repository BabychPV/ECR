// tests/Ecr.Api.Tests/Security/EffectiveAccessInheritanceHttpTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
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
/// ФВ-6.16: розріз ефективного доступу на аркуші, таблиці й колонці в проєкті — гранти по ланцюжку
/// предків Проєкт → Аркуш → Таблиця → Колонка, без стану документа, з обов'язковим застереженням.
/// </summary>
/// <remarks>
/// ⛔ У кожному тесті підсумковий рівень розрізу звіряється з <c>EditRules.Effective</c> над справжнім
/// профілем доступу (той самий метод, що вирішує доступ до комірки). Розріз, що розійшовся з рішенням,
/// показував би доступ, якого немає.
/// </remarks>
[Collection("SqlServer")]
public sealed class EffectiveAccessInheritanceHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Effective-Inherit-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Ланцюжок_предків__найдрібніший_грант_виграє__внески_позначені_предком_а_відповідь_застереженням()
    {
        using var app = new EcrApiFactory(sql);
        var s = await SeedAsync();

        var onProject = await AddRoleAsync(s.Tag, "Prj", [(ResourceKind.Project, s.ProjectA, GrantLevel.Read, false)]);
        var onSheet = await AddRoleAsync(s.Tag, "Sht", [(ResourceKind.Sheet, s.SheetId, GrantLevel.Write, false)]);
        var onColumn = await AddRoleAsync(s.Tag, "Col", [(ResourceKind.Column, s.ColumnId, GrantLevel.Manage, false)]);
        var subject = await AddUserAsync([onProject, onSheet]);
        var subjectWithColumn = await AddUserAsync([onProject, onSheet, onColumn]);
        using var admin = await SignedInAdminAsync(app);

        // Колонка без власного гранта успадковує найдрібніший оголошений: аркуш (Write), а не проєкт (Read).
        var view = await GetAsync(admin, subject.Id, $"Column:{s.ColumnId}", s.ProjectA);
        Assert.Equal("Write", view.GetProperty("level").GetString());
        Assert.Equal("DocumentStateNotConsidered", view.GetProperty("caveat").GetString());
        Assert.Equal(s.ProjectA, view.GetProperty("projectId").GetInt32());
        var rows = Contributions(view);
        Assert.Equal(2, rows.Count);
        Assert.Equal(($"Project:{s.ProjectA}", "Read"), (rows[0].InheritedFrom, rows[0].Level));
        Assert.Equal(($"Sheet:{s.SheetId}", "Write"), (rows[1].InheritedFrom, rows[1].Level));
        Assert.All(rows, r => Assert.True(r.Counted));

        // Грант на саму колонку — не «успадкований»: InheritedFrom порожній, і він перекриває аркуш.
        var own = await GetAsync(admin, subjectWithColumn.Id, $"Column:{s.ColumnId}", s.ProjectA);
        Assert.Equal("Manage", own.GetProperty("level").GetString());
        Assert.Contains(Contributions(own), r => r.InheritedFrom is null && r.Level == "Manage");
        Assert.Equal(3, Contributions(own).Count);

        // Таблиця: колонкового гранта вона не бачить (він дрібніший), тож рівень — аркуш.
        var table = await GetAsync(admin, subjectWithColumn.Id, $"Table:{s.TableId}", s.ProjectA);
        Assert.Equal("Write", table.GetProperty("level").GetString());
        Assert.DoesNotContain(Contributions(table), r => r.Level == "Manage");

        Assert.Equal(GrantLevel.Write, await OracleAsync(app, subject.Id, s, s.ColumnId, s.TableId));
        Assert.Equal(GrantLevel.Manage, await OracleAsync(app, subjectWithColumn.Id, s, s.ColumnId, s.TableId));
        Assert.Equal(GrantLevel.Write, await OracleAsync(app, subjectWithColumn.Id, s, 0, s.TableId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Без_гранта_на_проєкт_грант_на_аркуш_не_діє__заборона_на_проєкті_перекриває()
    {
        using var app = new EcrApiFactory(sql);
        var s = await SeedAsync();

        var onSheet = await AddRoleAsync(s.Tag, "Sht", [(ResourceKind.Sheet, s.SheetId, GrantLevel.Approve, false)]);
        var onProject = await AddRoleAsync(s.Tag, "Prj", [(ResourceKind.Project, s.ProjectA, GrantLevel.Manage, false)]);
        var denier = await AddRoleAsync(s.Tag, "Dny", [(ResourceKind.Project, s.ProjectA, GrantLevel.Read, true)]);
        var sheetOnly = await AddUserAsync([onSheet]);
        var denied = await AddUserAsync([onSheet, onProject, denier]);
        using var admin = await SignedInAdminAsync(app);

        // S2: проєкт невидимий — грант на аркуш (id спільний для проєктів шаблону) не дає нічого.
        var hidden = await GetAsync(admin, sheetOnly.Id, $"Sheet:{s.SheetId}", s.ProjectA);
        Assert.Equal("None", hidden.GetProperty("level").GetString());
        Assert.Equal("NoGrant", hidden.GetProperty("denyReason").GetString());
        Assert.Single(Contributions(hidden));

        var view = await GetAsync(admin, denied.Id, $"Table:{s.TableId}", s.ProjectA);
        Assert.Equal("None", view.GetProperty("level").GetString());
        Assert.True(view.GetProperty("isDenied").GetBoolean());
        Assert.Equal("ExplicitDeny", view.GetProperty("denyReason").GetString());
        Assert.Contains(Contributions(view), r => r.Deny && r.InheritedFrom == $"Project:{s.ProjectA}");

        Assert.Equal(GrantLevel.None, await OracleAsync(app, sheetOnly.Id, s, 0, s.TableId));
        Assert.Equal(GrantLevel.None, await OracleAsync(app, denied.Id, s, 0, s.TableId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Звужене_призначення_і_чужий_проєкт__внесок_не_враховано__рівень_збігається_з_рішенням()
    {
        using var app = new EcrApiFactory(sql);
        var s = await SeedAsync();

        var role = await AddRoleAsync(s.Tag, "Nrw",
            [
                (ResourceKind.Project, s.ProjectA, GrantLevel.Manage, false),
                (ResourceKind.Project, s.ProjectB, GrantLevel.Read, false),
            ]);
        var narrowed = await AddUserAsync([role], scopeProjects: [s.ProjectA], sheetCodes: ["OTHER"]);
        var scoped = await AddUserAsync([role], scopeProjects: [s.ProjectA]);
        using var admin = await SignedInAdminAsync(app);

        // Звужено аркушем, якого тут немає: шар не діє, роль не дає нічого, внесок «Narrowed» не враховано.
        var n = await GetAsync(admin, narrowed.Id, $"Sheet:{s.SheetId}", s.ProjectA);
        Assert.Equal("None", n.GetProperty("level").GetString());
        var nRow = Assert.Single(Contributions(n));
        Assert.Equal("Narrowed", nRow.Scope);
        Assert.False(nRow.Counted);
        Assert.Equal(GrantLevel.None, await OracleAsync(app, narrowed.Id, s, 0, 0, s.SheetId));

        // Область проєкту A: у проєкті B внесок поза областю.
        var inA = await GetAsync(admin, scoped.Id, $"Sheet:{s.SheetId}", s.ProjectA);
        Assert.Equal("Manage", inA.GetProperty("level").GetString());
        Assert.Equal("InScope", Assert.Single(Contributions(inA)).Scope);
        var inB = await GetAsync(admin, scoped.Id, $"Sheet:{s.SheetId}", s.ProjectB);
        Assert.Equal("None", inB.GetProperty("level").GetString());
        Assert.False(Assert.Single(Contributions(inB)).Counted);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Помилки__без_проєкту_422__ресурсу_чи_проєкту_немає_404__аркуш_з_іншого_шаблону_404__довідник_без_застереження()
    {
        using var app = new EcrApiFactory(sql);
        var s = await SeedAsync();
        var subject = await AddUserAsync([]);
        using var admin = await SignedInAdminAsync(app);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Raw(admin, subject.Id, $"Sheet:{s.SheetId}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Raw(admin, subject.Id, $"Column:{s.ColumnId}", 0)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Raw(admin, subject.Id, $"Sheet:{s.SheetId + 90_000}", s.ProjectA)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Raw(admin, subject.Id, $"Table:{s.TableId + 90_000}", s.ProjectA)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Raw(admin, subject.Id, $"Sheet:{s.SheetId}", s.ProjectA + 90_000)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Raw(admin, subject.Id + 500_000, $"Sheet:{s.SheetId}", s.ProjectA)).StatusCode);

        // Аркуш існує, але з іншої версії шаблону, ніж у проєкту.
        Assert.Equal(HttpStatusCode.NotFound, (await Raw(admin, subject.Id, $"Sheet:{s.ForeignSheetId}", s.ProjectA)).StatusCode);

        // Проєкт — без застереження й без проєкту в відповіді.
        var project = await GetAsync(admin, subject.Id, $"Project:{s.ProjectA}", null);
        Assert.Equal(JsonValueKind.Null, project.GetProperty("caveat").ValueKind);
        Assert.Equal(JsonValueKind.Null, project.GetProperty("projectId").ValueKind);
    }

    private static async Task<HttpResponseMessage> Raw(HttpClient client, int userId, string resource, int? projectId)
        => await client.GetAsync(new Uri(
            $"/api/v1/security/users/{userId}/effective-access?resource={Uri.EscapeDataString(resource)}"
            + (projectId is { } p ? $"&projectId={p}" : string.Empty), UriKind.Relative));

    private static async Task<JsonElement> GetAsync(HttpClient client, int userId, string resource, int? projectId)
    {
        var response = await Raw(client, userId, resource, projectId);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static List<Row> Contributions(JsonElement view)
        => [.. view.GetProperty("contributions").EnumerateArray().Select(c => new Row(
            c.GetProperty("level").GetString()!,
            c.GetProperty("isDeny").GetBoolean(),
            c.GetProperty("scope").GetString()!,
            c.GetProperty("counted").GetBoolean(),
            c.GetProperty("inheritedFrom").ValueKind == JsonValueKind.Null ? null : c.GetProperty("inheritedFrom").GetString()))];

    /// <summary>Рівень, який дає рішення про комірку: <c>EditRules.Effective</c> над справжнім профілем.</summary>
    private static async Task<GrantLevel> OracleAsync(
        EcrApiFactory app, int userId, Stand s, int columnId, int tableId, int sheetOnly = 0)
    {
        using var scope = app.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<IAccessDecisionService>();
        var profile = await access.BuildProfileAsync(userId, CancellationToken.None);
        return EditRules.Effective(profile, default(CellAccessContext) with
        {
            ProjectId = s.ProjectA,
            SheetDefId = s.SheetId,
            TableDefId = sheetOnly != 0 ? 0 : tableId,
            ColumnDefId = columnId,
            SheetCode = s.SheetCode,
        });
    }

    private sealed record Row(string Level, bool Deny, string Scope, bool Counted, string? InheritedFrom);

    private sealed record Stand(
        string Tag, int ProjectA, int ProjectB, int SheetId, string SheetCode, int TableId, int ColumnId, int ForeignSheetId);

    private sealed record Account(int Id, string UserName);

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private async Task<Stand> SeedAsync()
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = new EcrDbContext(Options());

        var template = new Template(EcrCode.Create($"EIT_{tag}"), Name("Template"), 1, DateTime.UtcNow);
        var other = new Template(EcrCode.Create($"EIO_{tag}"), Name("Other"), 1, DateTime.UtcNow);
        db.Templates.AddRange(template, other);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, DateTime.UtcNow);
        var otherVersion = new TemplateVersion(other.Id, "1.0.0.0", 1, DateTime.UtcNow);
        db.TemplateVersions.AddRange(version, otherVersion);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"EIS_{tag}"), Name("Sheet"), 1);
        var foreign = new SheetDef(otherVersion.Id, EcrCode.Create($"EIF_{tag}"), Name("Foreign"), 1);
        db.SheetDefs.AddRange(sheet, foreign);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"EITB_{tag}"), Name("Table"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var column = new ColumnDef(table.Id, EcrCode.Create($"EIC_{tag}"), Name("Col"), 1, CellDataType.Decimal);
        db.ColumnDefs.Add(column);
        await db.SaveChangesAsync();

        // periodPolicyId 1 — сіяна політика «ECR-Standard».
        var a = new Project(
            EcrCode.Create($"EIPA_{tag}"), Name("A"), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            version.Id, PeriodKind.Monthly, periodPolicyId: 1, "Asia/Atyrau");
        var b = new Project(
            EcrCode.Create($"EIPB_{tag}"), Name("B"), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            version.Id, PeriodKind.Monthly, periodPolicyId: 1, "Asia/Atyrau");
        db.Projects.AddRange(a, b);
        await db.SaveChangesAsync();

        return new Stand(tag, a.Id, b.Id, sheet.Id, sheet.Code, table.Id, column.Id, foreign.Id);
    }

    private async Task<int> AddRoleAsync(string tag, string suffix, (ResourceKind Kind, int Id, GrantLevel Level, bool Deny)[] grants)
        => await AddRoleAsync(tag, suffix, [], grants);

    private async Task<int> AddRoleAsync(
        string tag, string suffix, string[] permissions,
        (ResourceKind Kind, int Id, GrantLevel Level, bool Deny)[] grants)
    {
        await using var db = new EcrDbContext(Options());
        var role = new Role(EcrCode.Create($"EI{tag}_{suffix}"), Name($"Role {suffix}"));
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

    private async Task<Account> AddUserAsync(int[] roleIds, int[]? scopeProjects = null, string[]? sheetCodes = null)
    {
        var name = $"effi_{Guid.NewGuid():N}"[..20];
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
                assignment.SetScope(RoleAssignmentScope.Create(scopeProjects, sheetCodes));
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
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName = admin.UserName, password = Password });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }
}
