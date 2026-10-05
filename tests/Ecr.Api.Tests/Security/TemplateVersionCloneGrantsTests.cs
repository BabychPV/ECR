// tests/Ecr.Api.Tests/Security/TemplateVersionCloneGrantsTests.cs
using System.Net.Http.Json;
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
/// A1-04: заборона на аркуш/таблицю/колонку діє в НОВОМУ проєкті на клоні версії шаблону.
/// </summary>
/// <remarks>
/// <c>sec.ResourceGrant</c> адресує ресурс числовим Id версії й не має <c>ProjectId</c>; клон дає
/// ресурсам нові Id, тож без копіювання грантів проєкт на v2 мав <c>Write</c> замість <c>None</c>.
/// Оракул — <c>EditRules.Effective</c> на профілі зі справжньої служби: те саме рішення, яким
/// користуються читання сітки, запис, історія й Excel. Мутація: прибрати <c>CloneResourceGrantsAsync</c>
/// з <c>SaveCloneAsync</c> — червоніють усі чотири сценарії (Column/Table/Sheet і кеш профілю).
/// ⚠ Свідомі обмеження (відомі, не цього циклу): Deny, додане на v1 ПІСЛЯ клону, на v2 не діє;
/// клон не переносить правила доступу до періоду.
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateVersionCloneGrantsTests(SqlServerFixture sql)
{
    [Theory]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    [Trait("Finding", "A1-04")]
    [InlineData(ResourceKind.Column)]
    [InlineData(ResourceKind.Table)]
    [InlineData(ResourceKind.Sheet)]
    public async Task Заборона_діє_в_новому_проєкті_на_клоні_версії(ResourceKind kind)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 3, rowCount: 2).ConfigureAwait(true);
        var column = doc.ColumnDefIds[1];
        var columnCode = await CodeOfAsync(builder, column).ConfigureAwait(true);
        var deniedId = kind switch
        {
            ResourceKind.Sheet => doc.SheetDefId,
            ResourceKind.Table => doc.TableDefId,
            _ => column,
        };

        var (userId, roleId) = await CreateRestrictedAsync(builder, doc.ProjectId, kind, deniedId).ConfigureAwait(true);
        var cloneId = await CloneAndPublishAsync(builder, doc).ConfigureAwait(true);
        var v2 = await ResolveAsync(builder, cloneId, doc.SheetCode, columnCode).ConfigureAwait(true);
        Assert.NotEqual(column, v2.Column);

        var p2 = await CreateProjectAsync(builder, cloneId, roleId).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);

        // Контроль: у проєкті на v1 колонка закрита.
        Assert.Equal(GrantLevel.None, await EffectiveAsync(
            app, userId, doc.ProjectId, doc.SheetDefId, doc.TableDefId, column, doc.SheetCode).ConfigureAwait(true));

        // ⛔ Предмет тесту: та сама колонка (код) у проєкті на клоні.
        Assert.Equal(GrantLevel.None, await EffectiveAsync(
            app, userId, p2, v2.Sheet, v2.Table, v2.Column, doc.SheetCode).ConfigureAwait(true));
    }

    /// <summary>
    /// Профіль, прогрітий ДО клону, після клону (без жодного іншого гранту) містить заборону на колонку v2:
    /// кеш профілів ключується відбитком грантів ролі, нові рядки його міняють.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "A1-04")]
    public async Task Профіль_прогрітий_до_клону_бачить_заборону_на_колонку_клону()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 3, rowCount: 2).ConfigureAwait(true);
        var column = doc.ColumnDefIds[1];
        var columnCode = await CodeOfAsync(builder, column).ConfigureAwait(true);
        var (userId, _) = await CreateRestrictedAsync(builder, doc.ProjectId, ResourceKind.Column, column).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);

        using (var scope = app.Services.CreateScope())
        {
            var warm = await scope.ServiceProvider.GetRequiredService<IAccessDecisionService>()
                .BuildProfileAsync(userId, CancellationToken.None).ConfigureAwait(true);
            Assert.Contains($"Column:{column}", warm.Denies);
        }

        // Клон — справжнім шляхом (POST …/clone), який скидає кеш профілів.
        var adminName = $"a104a_{Guid.NewGuid():N}"[..20];
        await using (var db = builder.CreateContext())
        {
            var admin = new User(adminName, adminName, AuthProvider.Local);
            admin.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(admin);
            var adminRole = new Role(EcrCode.Create($"A104A_{Guid.NewGuid():N}"), Name("Template editor"));
            db.Roles.Add(adminRole);
            await db.SaveChangesAsync().ConfigureAwait(true);
            db.RoleAssignments.Add(new RoleAssignment(adminRole.Id, admin.Id, null));
            db.RolePermissions.Add(new RolePermission(adminRole.Id, "Template.Edit"));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        using var http = app.CreateClient();
        var login = await http.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName = adminName, password = Password }).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        var cloned = await http.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{doc.TemplateVersionId}/clone", UriKind.Relative),
            new { newVersion = $"3.{Random.Shared.Next(1, 99999)}.0.1" }).ConfigureAwait(true);
        var clonedBody = await cloned.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(cloned.IsSuccessStatusCode, $"{cloned.StatusCode}: {clonedBody}\n{app.ErrorsText}");
        var cloneId = System.Text.Json.JsonDocument.Parse(clonedBody).RootElement
            .EnumerateObject().First(p => p.Name.Equals("versionId", StringComparison.OrdinalIgnoreCase)).Value.GetInt32();
        var v2 = await ResolveAsync(builder, cloneId, doc.SheetCode, columnCode).ConfigureAwait(true);

        using var scope2= app.Services.CreateScope();
        var after = await scope2.ServiceProvider.GetRequiredService<IAccessDecisionService>()
            .BuildProfileAsync(userId, CancellationToken.None).ConfigureAwait(true);
        Assert.Contains($"Column:{v2.Column}", after.Denies);
    }

    private const string Password = "Api-A104-Clone-2026!";

    private static async Task<(int UserId, int RoleId)> CreateRestrictedAsync(
        TestDocumentBuilder builder, int projectId, ResourceKind kind, int deniedId)
    {
        await using var db = builder.CreateContext();
        var name = $"a104_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash("Api-A104-Clone-2026!"));
        db.Users.Add(user);
        var role = new Role(EcrCode.Create($"A104_{Guid.NewGuid():N}"), Name("Restricted"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(true);

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Write));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, kind, deniedId, GrantLevel.None, isDeny: true));
        await db.SaveChangesAsync().ConfigureAwait(true);
        return (user.Id, role.Id);
    }

    private static async Task<int> CloneAndPublishAsync(TestDocumentBuilder builder, TestDocument doc)
    {
        var now = new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc);
        int cloneId;
        await using (var db = builder.CreateContext())
        {
            cloneId = await new TemplateVersionStore(db)
                .CloneAsync(doc.TemplateVersionId, $"2.0.0.{doc.SheetCode}", 1, now, CancellationToken.None)
                .ConfigureAwait(true);
        }

        await using (var db = builder.CreateContext())
        {
            var clone = await db.TemplateVersions.SingleAsync(v => v.Id == cloneId).ConfigureAwait(true);
            clone.Publish(1, now);
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        return cloneId;
    }

    private static async Task<int> CreateProjectAsync(TestDocumentBuilder builder, int cloneId, int roleId)
    {
        await using var db = builder.CreateContext();
        var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync().ConfigureAwait(true);
        var project = new Project(
            EcrCode.Create($"PRJCL{Guid.NewGuid():N}"[..20]), Name("Clone project"),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            cloneId, PeriodKind.Monthly, policyId, "Asia/Atyrau");
        db.Projects.Add(project);
        await db.SaveChangesAsync().ConfigureAwait(true);

        db.ResourceGrants.Add(new ResourceGrant(roleId, ResourceKind.Project, project.Id, GrantLevel.Write));
        await db.SaveChangesAsync().ConfigureAwait(true);
        return project.Id;
    }

    private static async Task<string> CodeOfAsync(TestDocumentBuilder builder, int columnId)
    {
        await using var db = builder.CreateContext();
        return await db.ColumnDefs.Where(c => c.Id == columnId).Select(c => c.Code).SingleAsync().ConfigureAwait(false);
    }

    private static async Task<(int Sheet, int Table, int Column)> ResolveAsync(
        TestDocumentBuilder builder, int versionId, string sheetCode, string columnCode)
    {
        await using var db = builder.CreateContext();
        var row = await (from s in db.SheetDefs
                         join t in db.TableDefs on s.Id equals t.SheetDefId
                         join c in db.ColumnDefs on t.Id equals c.TableDefId
                         where s.TemplateVersionId == versionId && s.Code == sheetCode && c.Code == columnCode
                         select new { Sheet = s.Id, Table = t.Id, Column = c.Id })
            .SingleAsync().ConfigureAwait(false);
        return (row.Sheet, row.Table, row.Column);
    }

    private static async Task<GrantLevel> EffectiveAsync(
        EcrApiFactory app, int userId, int projectId, int sheetId, int tableId, int columnId, string sheetCode)
    {
        using var scope = app.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<IAccessDecisionService>();
        var profile = await access.BuildProfileAsync(userId, CancellationToken.None).ConfigureAwait(false);
        return EditRules.Effective(profile, default(CellAccessContext) with
        {
            ProjectId = projectId,
            SheetDefId = sheetId,
            TableDefId = tableId,
            ColumnDefId = columnId,
            SheetCode = sheetCode,
        });
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
