// tests/Ecr.Api.Tests/EditAssignmentsDemotionApiTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Documents;
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
/// D-454 (PM-EDITASSIGN-P3, п. g): зниження прав делегатора передачі на РЕАЛЬНОМУ шляху HTTP
/// (<c>PATCH /api/v1/documents/{id}/cells</c>), а не лише на рівні сервісу рішень.
/// </summary>
/// <remarks>
/// Виконавець має лише <c>Read</c> на проєкт; право писати йому дає тільки Write-передача аркуша, видана делегатором.
/// Передачу створено прямо в базі (через HTTP її не видав би делегатор без права керувати - саме це й перевіряється).
/// Дзеркала сервісних тестів: <c>EditAssignmentAccessDecisionTests.Write_передача_після_зняття_ролей_делегатора_…</c>
/// (P3-WRITE-DEMOTION) і <c>EditAssignmentDelegatorRightsTests.Адміністративні_права_без_гранта_Manage_…</c> (P3-ADMIN).
///
/// Мутаційні докази (локально, не комітяться): у <c>AccessDecisionService</c> вимкнути перевірку прав делегатора
/// (<c>WithDelegatorRightsAsync</c> / <c>DelegatorActive</c>) - червоніють обидва тести (403 замінюється на 200).
/// </remarks>
[Collection("SqlServer")]
public sealed class EditAssignmentsDemotionApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Edit-Demotion-2026!";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P3-WRITE-DEMOTION-HTTP")]
    public async Task Write_передача_після_зняття_ролей_делегатора_дає_403_а_відновлення_повертає_200()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var executor = await SignInAsync(app, s.Executor.Name).ConfigureAwait(true);

        // Контроль: делегатор з Manage - Write-передача підіймає виконавця (Read) до запису місячної колонки.
        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(executor, s, 1m)).ConfigureAwait(true);
        Assert.Equal(1m, await StoredValueAsync(s).ConfigureAwait(true));

        // Ролі делегатора знято - передача не діє, PATCH виконавця відхилено рішенням сервера, значення не змінилось.
        await RemoveRolesAsync(s.Delegator.Id).ConfigureAwait(true);
        var denied = await ExpectAsync(app, HttpStatusCode.Forbidden, PatchAsync(executor, s, 2m)).ConfigureAwait(true);
        Assert.Equal("err.ECR-ACCS-0403.deniedCells", MessageKeyOf(denied));
        Assert.Equal("InsufficientGrantLevel", ReasonOf(denied));
        Assert.Equal(1m, await StoredValueAsync(s).ConfigureAwait(true));

        // Права делегатора відновлено - той самий запис знову проходить.
        await AssignRoleAsync(s.Delegator.Id, s.ManagerRoleId).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(executor, s, 2m)).ConfigureAwait(true);
        Assert.Equal(2m, await StoredValueAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P3-ADMIN-HTTP")]
    public async Task Адміністративні_права_без_гранта_Manage_не_підтримують_передачу_а_Manage_так()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var executor = await SignInAsync(app, s.Executor.Name).ConfigureAwait(true);

        // Контроль: з грантом Manage на проєкт передача діє.
        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(executor, s, 1m)).ConfigureAwait(true);
        Assert.Equal(1m, await StoredValueAsync(s).ConfigureAwait(true));

        // «Глобальний адмін»: права безпеки/довідників/шаблонів, але без ресурсного гранта Manage на проєкт документа.
        await RemoveRolesAsync(s.Delegator.Id).ConfigureAwait(true);
        var adminRoleId = await NewAdminRoleAsync().ConfigureAwait(true);
        await AssignRoleAsync(s.Delegator.Id, adminRoleId).ConfigureAwait(true);

        var denied = await ExpectAsync(app, HttpStatusCode.Forbidden, PatchAsync(executor, s, 2m)).ConfigureAwait(true);
        Assert.Equal("err.ECR-ACCS-0403.deniedCells", MessageKeyOf(denied));
        Assert.Equal("InsufficientGrantLevel", ReasonOf(denied));
        Assert.Equal(1m, await StoredValueAsync(s).ConfigureAwait(true));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private static string MessageKeyOf(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("messageKey").GetString() ?? string.Empty;

    private sealed record Person(int Id, string Name);

    private sealed record Scenario(
        TestDocumentBuilder Builder, TestDocument Document, int PeriodKey, Person Delegator, Person Executor, int ManagerRoleId);

    private static async Task<string> ExpectAsync(EcrApiFactory app, HttpStatusCode expected, Task<HttpResponseMessage> call)
    {
        using var response = await call.ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(
            response.StatusCode == expected,
            $"{(int)response.StatusCode} замість {(int)expected}\n{body}\n{app.ErrorsText}");

        return body;
    }

    private static string ReasonOf(string body)
    {
        var root = JsonDocument.Parse(body).RootElement;
        if (root.TryGetProperty("reason", out var reason))
        {
            return reason.GetString() ?? string.Empty;
        }

        return root.TryGetProperty("details", out var details) && details.TryGetProperty("reason", out var inner)
            ? inner.GetString() ?? string.Empty
            : $"немає reason у відповіді: {body}";
    }

    /// <summary>
    /// Правка місячної колонки ПЕРШОГО наявного рядка (передача місяця не дає створювати рядки - D5), з актуальною
    /// версією рядка зі свіжого GET зрізу.
    /// </summary>
    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, Scenario s, decimal value)
    {
        using var table = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Document.DocumentId.ToString(CultureInfo.InvariantCulture)}/tables/{s.Document.TableInstanceId.ToString(CultureInfo.InvariantCulture)}",
            UriKind.Relative)).ConfigureAwait(false);
        var text = await table.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(table.IsSuccessStatusCode, $"GET зрізу: {table.StatusCode}\n{text}");

        var root = JsonDocument.Parse(text).RootElement;
        var columnCode = root.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("code").GetString()!).ElementAt(1);
        var row = root.GetProperty("rows").EnumerateArray().First();

        return await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId.ToString(CultureInfo.InvariantCulture)}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = s.Document.TableInstanceId,
                periodKey = s.PeriodKey,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey = row.GetProperty("rowKey").GetString(),
                        baseVersion = row.GetProperty("rowVersion").GetString(),
                        cells = new object[] { new { columnCode, value = (object)value.ToString(CultureInfo.InvariantCulture) } },
                    },
                },
            }).ConfigureAwait(false);
    }

    /// <summary>Збережене значення місячної колонки (одна комірка в документі: правиться один рядок).</summary>
    private static async Task<decimal?> StoredValueAsync(Scenario s)
    {
        await using var db = s.Builder.CreateContext();
        return await (
                from c in db.CellValues.AsNoTracking()
                join r in db.TableRows.AsNoTracking()
                    on new { c.PeriodKeyValue, Id = c.TableRowId } equals new { r.PeriodKeyValue, r.Id }
                where c.PeriodKeyValue == s.PeriodKey
                      && c.ColumnDefId == s.Document.ColumnDefIds[1]
                      && r.TableInstanceId == s.Document.TableInstanceId
                orderby r.Id
                select c.ValueNumeric)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
    }

    private async Task RemoveRolesAsync(int userId)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        await db.RoleAssignments.Where(a => a.UserId == userId).ExecuteDeleteAsync().ConfigureAwait(false);
    }

    private async Task AssignRoleAsync(int userId, int roleId)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        db.RoleAssignments.Add(new RoleAssignment(roleId, userId, null));
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task<int> NewAdminRoleAsync()
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var admin = new Role(
            EcrCode.Create($"DRA_{Guid.NewGuid():N}"[..20]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Global admin" }));
        db.Roles.Add(admin);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var permission in new[] { "Document.View", "Security.ManageUsers", "Security.ManageRoles", "Registry.View", "Template.View" })
        {
            db.RolePermissions.Add(new RolePermission(admin.Id, permission));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        return admin.Id;
    }

    private static async Task<HttpClient> SignInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    /// <summary>
    /// Документ у ВІДКРИТОМУ поточному періоді активного проєкту (як у <c>ApproverEditHttpFlowTests</c>); делегатор з роллю
    /// Manage на проєкт; виконавець лише з Read; Write-передача аркуша делегатор → виконавець.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SiteZone));
        var periodKey = (siteToday.Year * 100) + siteToday.Month;

        var document = await builder
            .BuildAsync(periodKey, columnCount: 3, rowCount: 2, rowMode: TableRowMode.Mixed)
            .ConfigureAwait(false);

        var now = DateTime.UtcNow;

        await using var db = builder.CreateContext();
        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, document.SheetDefId));

        var project = await db.Projects.FirstAsync(p => p.Id == document.ProjectId).ConfigureAwait(false);
        project.Activate(now);

        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var period = await db.Periods
            .FirstAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey)
            .ConfigureAwait(false);
        period.RecomputeBoundaries(policy, SiteZone);
        period.AdvanceTo(PeriodState.Open, now);
        await db.SaveChangesAsync().ConfigureAwait(false);

        async Task<(Person Person, int RoleId)> NewUserAsync(string prefix, GrantLevel level)
        {
            var name = $"{prefix}_{Guid.NewGuid():N}"[..20];
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(
                EcrCode.Create($"ED_{Guid.NewGuid():N}"[..20]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Edit assignment demotion" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, level));
            await db.SaveChangesAsync().ConfigureAwait(false);

            return (new Person(user.Id, name), role.Id);
        }

        var (delegator, managerRoleId) = await NewUserAsync("edd", GrantLevel.Manage).ConfigureAwait(false);
        var (executor, _) = await NewUserAsync("ede", GrantLevel.Read).ConfigureAwait(false);

        // Write-передача - це передача МІСЯЦЯ: числова колонка документа стає місячною колонкою поточного місяця.
        var month = (byte)siteToday.Month;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE cfg.ColumnDef SET IsMonthColumn = 1, MonthNumber = {month} WHERE Id = {document.ColumnDefIds[1]}")
            .ConfigureAwait(false);

        var assignment = new EditAssignment(
            document.DocumentId,
            EditAssignmentSlice.Create(sheetCode: null, tableCode: null, monthNumber: month, periodKey: null),
            isExclusive: false,
            delegatedByUserId: delegator.Id,
            handoffId: Guid.NewGuid(),
            createdByUserId: delegator.Id,
            utcNow: now,
            level: GrantLevel.Write);
        assignment.AddRecipient(executor.Id, delegator.Id, now);
        new EditAssignmentStore(db).Add(assignment);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(builder, document, periodKey, delegator, executor, managerRoleId);
    }
}
