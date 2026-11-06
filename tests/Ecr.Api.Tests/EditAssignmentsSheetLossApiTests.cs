// tests/Ecr.Api.Tests/EditAssignmentsSheetLossApiTests.cs
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
/// PM-EDITASSIGN-P3 (a): Write-передача не діє на аркуші, право запису на який делегатор утратив ПІСЛЯ видачі,
/// хоча право керувати проєктом у нього лишилось (реальний шлях <c>PATCH /api/v1/documents/{id}/cells</c>).
/// </summary>
/// <remarks>
/// Під час видачі «у межах власних прав» звіряє аркуші (<c>SheetsBeyondOwnRights</c>); під час рішення раніше
/// звірялось лише «делегатор ще менеджер» — і виконавець писав на аркуш, закритий самому делегаторові.
///
/// Мутаційний доказ (локально, не комітиться): прибрати перевірку аркушів для Write у
/// <c>AccessDecisionService.WithDelegatorRightsAsync</c> - червоніє <c>Заборона_аркуша_делегатора_…</c> (200 замість 403).
/// </remarks>
[Collection("SqlServer")]
public sealed class EditAssignmentsSheetLossApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Edit-SheetLoss-2026!";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "PM-EDITASSIGN-P3-a")]
    public async Task Заборона_аркуша_делегатора_після_видачі_знімає_запис_виконавця_а_зняття_заборони_повертає()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var executor = await SignInAsync(app, s.Executor.Name).ConfigureAwait(true);

        // Контроль: делегатор з Manage - Write-передача підіймає виконавця (Read) до запису місячної колонки.
        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(executor, s, 1m)).ConfigureAwait(true);
        Assert.Equal(1m, await StoredValueAsync(s).ConfigureAwait(true));

        // Делегатор лишається менеджером проєкту, але аркуш документа йому заборонено.
        var denyId = await DenySheetAsync(s.ManagerRoleId, s.Document.SheetDefId, s.Delegator.Id).ConfigureAwait(true);
        var denied = await ExpectAsync(app, HttpStatusCode.Forbidden, PatchAsync(executor, s, 2m)).ConfigureAwait(true);
        Assert.Equal("err.ECR-ACCS-0403.deniedCells", MessageKeyOf(denied));
        Assert.Equal(1m, await StoredValueAsync(s).ConfigureAwait(true));

        // Заборону знято - передача знову діє.
        await RemoveGrantAsync(denyId, s.Delegator.Id).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(executor, s, 2m)).ConfigureAwait(true);
        Assert.Equal(2m, await StoredValueAsync(s).ConfigureAwait(true));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Person(int Id, string Name);

    private sealed record Scenario(
        TestDocumentBuilder Builder, TestDocument Document, int PeriodKey, Person Delegator, Person Executor, int ManagerRoleId);

    private static string MessageKeyOf(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("messageKey").GetString() ?? string.Empty;

    private static async Task<string> ExpectAsync(EcrApiFactory app, HttpStatusCode expected, Task<HttpResponseMessage> call)
    {
        using var response = await call.ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(
            response.StatusCode == expected,
            $"{(int)response.StatusCode} замість {(int)expected}\n{body}\n{app.ErrorsText}");

        return body;
    }

    /// <summary>Правка місячної колонки першого наявного рядка з актуальною версією рядка.</summary>
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

    /// <summary>
    /// Заборона аркуша в ролі делегатора. Як і зміна гранту через API, оновлює штамп безпеки власника ролі — інакше
    /// профіль делегатора брався б із кешу (ключ — штамп).
    /// </summary>
    private async Task<int> DenySheetAsync(int roleId, int sheetDefId, int delegatorId)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var deny = new ResourceGrant(roleId, ResourceKind.Sheet, sheetDefId, GrantLevel.Write, isDeny: true);
        db.ResourceGrants.Add(deny);
        (await db.Users.FirstAsync(u => u.Id == delegatorId).ConfigureAwait(false)).RefreshSecurityStamp();
        await db.SaveChangesAsync().ConfigureAwait(false);
        return deny.Id;
    }

    private async Task RemoveGrantAsync(int grantId, int delegatorId)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        await db.ResourceGrants.Where(g => g.Id == grantId).ExecuteDeleteAsync().ConfigureAwait(false);
        (await db.Users.FirstAsync(u => u.Id == delegatorId).ConfigureAwait(false)).RefreshSecurityStamp();
        await db.SaveChangesAsync().ConfigureAwait(false);
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
    /// Документ у відкритому поточному періоді; делегатор з Manage на проєкт; виконавець лише з Read; Write-передача
    /// місяця делегатор → виконавець (створена в базі, як у <c>EditAssignmentsDemotionApiTests</c>).
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
                EcrCode.Create($"ES_{Guid.NewGuid():N}"[..20]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Edit assignment sheet loss" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, level));
            await db.SaveChangesAsync().ConfigureAwait(false);

            return (new Person(user.Id, name), role.Id);
        }

        var (delegator, managerRoleId) = await NewUserAsync("esd", GrantLevel.Manage).ConfigureAwait(false);
        var (executor, _) = await NewUserAsync("ese", GrantLevel.Read).ConfigureAwait(false);

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
