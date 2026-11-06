// tests/Ecr.Api.Tests/EditAssignmentsCalendarApiTests.cs
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
/// PM-EDITASSIGN-P3 (c): календар періодів проєкту (<c>GET /api/v1/projects/{id}/periods</c>) для виконавця без ролі
/// на проєкт відкривається лише діючим наданням - тим, чий делегатор ще може його видавати (D-443, P2-2).
/// </summary>
/// <remarks>
/// Сервісний рівень (<c>ParticipatesInProjectAsync</c>) покриває <c>EditAssignmentDelegatorRightsTests</c>; тут - реальний
/// шлях: ролі делегатора знімає й повертає ендпоінт адміністрування (<c>PUT /api/v1/users/{id}/roles</c>, з ротацією
/// штампа), а відмова звіряється з відповіддю на неіснуючий проєкт (S17: 404 без оракула існування).
///
/// Мутаційний доказ (локально, не комітиться): у <c>AccessDecisionService.ParticipatesInProjectAsync</c> повертати
/// <c>true</c> для будь-якого кандидата (сира участь, без <c>ParticipationGrantsAsync</c>) - червоніє
/// <c>Календар_…</c> (200 замість 404).
/// </remarks>
[Collection("SqlServer")]
public sealed class EditAssignmentsCalendarApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Edit-Calendar-2026!";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "PM-EDITASSIGN-P3-c")]
    public async Task Календар_проєкту_виконавця_закривається_зі_зняттям_ролей_делегатора_і_відкривається_з_поверненням()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var adminName = await NewUserAdminAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var executor = await SignInAsync(app, s.Executor.Name).ConfigureAwait(true);
        using var admin = await SignInAsync(app, adminName).ConfigureAwait(true);

        // Контроль: делегатор з Manage - виконавець без ролей бачить календар проєкту свого документа.
        var calendar = await ExpectAsync(app, HttpStatusCode.OK, CalendarAsync(executor, s.ProjectId)).ConfigureAwait(true);
        Assert.Equal(s.ProjectId, JsonDocument.Parse(calendar).RootElement.GetProperty("projectId").GetInt32());

        // Відмова на неіснуючий проєкт - еталон для порівняння (S17).
        var missing = await ExpectAsync(app, HttpStatusCode.NotFound, CalendarAsync(executor, int.MaxValue)).ConfigureAwait(true);

        // Ролі делегатора знято штатним ендпоінтом - надання лишається рядком, але календаря не відкриває.
        await SetRolesAsync(app, admin, s.Delegator.Id, []).ConfigureAwait(true);
        var denied = await ExpectAsync(app, HttpStatusCode.NotFound, CalendarAsync(executor, s.ProjectId)).ConfigureAwait(true);
        Assert.Equal(ErrorCodeOf(missing), ErrorCodeOf(denied));

        // Роль повернуто - надання знову діє.
        await SetRolesAsync(app, admin, s.Delegator.Id, [s.ManagerRoleCode]).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.OK, CalendarAsync(executor, s.ProjectId)).ConfigureAwait(true);
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Person(int Id, string Name);

    private sealed record Scenario(int ProjectId, Person Delegator, Person Executor, string ManagerRoleCode);

    private static string ErrorCodeOf(string body)
    {
        var root = JsonDocument.Parse(body).RootElement;
        var key = root.TryGetProperty("messageKey", out var k) ? k.GetString() : null;
        return $"{root.GetProperty("errorCode").GetString()}|{key}";
    }

    private static Task<HttpResponseMessage> CalendarAsync(HttpClient client, int projectId)
        => client.GetAsync(new Uri(
            $"/api/v1/projects/{projectId.ToString(CultureInfo.InvariantCulture)}/periods", UriKind.Relative));

    private static async Task<string> ExpectAsync(EcrApiFactory app, HttpStatusCode expected, Task<HttpResponseMessage> call)
    {
        using var response = await call.ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(
            response.StatusCode == expected,
            $"{(int)response.StatusCode} замість {(int)expected}\n{body}\n{app.ErrorsText}");

        return body;
    }

    private static async Task SetRolesAsync(EcrApiFactory app, HttpClient admin, int userId, string[] roleCodes)
    {
        using var put = await admin.PutAsJsonAsync(
            new Uri($"/api/v1/users/{userId.ToString(CultureInfo.InvariantCulture)}/roles", UriKind.Relative),
            new { roleCodes }).ConfigureAwait(false);
        Assert.True(put.IsSuccessStatusCode, $"ролі користувача {userId}: {put.StatusCode}: {app.ErrorsText}");
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

    private static User NewUser(EcrDbContext db, string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        return user;
    }

    private static Role NewRole(EcrDbContext db, string prefix, string title)
    {
        var role = new Role(
            EcrCode.Create($"{prefix}_{Guid.NewGuid():N}"[..20]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = title }));
        db.Roles.Add(role);
        return role;
    }

    /// <summary>Адміністратор користувачів (лише <c>Security.ManageUsers</c>).</summary>
    private async Task<string> NewUserAdminAsync()
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var user = NewUser(db, "eca");
        var role = NewRole(db, "ECA", "User admin");
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Security.ManageUsers"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        await db.SaveChangesAsync().ConfigureAwait(false);
        return user.UserName;
    }

    /// <summary>
    /// Документ у відкритому поточному періоді; делегатор з Manage на проєкт; виконавець БЕЗ ролей; Read-передача
    /// аркуша делегатор → виконавець (створена в базі, як у <c>EditAssignmentsDemotionApiTests</c>).
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SiteZone));
        var periodKey = (siteToday.Year * 100) + siteToday.Month;

        var document = await builder
            .BuildAsync(periodKey, columnCount: 2, rowCount: 1, rowMode: TableRowMode.Mixed)
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

        var delegator = NewUser(db, "ecd");
        var executor = NewUser(db, "ece");
        var managerRole = NewRole(db, "ECM", "Edit assignment calendar manager");
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(managerRole.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(managerRole.Id, delegator.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(managerRole.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Manage));

        var assignment = new EditAssignment(
            document.DocumentId,
            EditAssignmentSlice.Create(document.SheetCode, tableCode: null, monthNumber: null, periodKey: null),
            isExclusive: false,
            delegatedByUserId: delegator.Id,
            handoffId: Guid.NewGuid(),
            createdByUserId: delegator.Id,
            utcNow: now,
            level: GrantLevel.Read);
        assignment.AddRecipient(executor.Id, delegator.Id, now);
        new EditAssignmentStore(db).Add(assignment);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(
            document.ProjectId,
            new Person(delegator.Id, delegator.UserName),
            new Person(executor.Id, executor.UserName),
            managerRole.Code);
    }
}
