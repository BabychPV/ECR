// tests/Ecr.Api.Tests/ArchivedProjectRulesTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Архівований проєкт справжнім HTTP: повторна архівація, зміна поточного
/// періоду і новий документ — <c>409 ECR-PRD-0409</c> з ключем (F-11, F-12).
/// </summary>
/// <remarks>
/// ⛔ Що відтворили аналітики: повторна архівація — <c>500</c>
/// (<c>InvalidOperationException</c> із <c>Project.Archive</c>); заміна
/// поточного періоду в архівованому проєкті — <c>204</c>; <c>POST /documents</c>
/// в архівованому проєкті — <c>201</c>.
///
/// ⚠ Проєкт архівується в ПІДГОТОВЦІ, прямо в базі, а не першим запитом: інакше
/// між активацією і запитом встигає пройти <c>PeriodStateJob</c> старту
/// застосунку, який обробляє активні проєкти й міг би перерахувати стан періодів.
/// Архівований проєкт він не чіпає. Перша архівація через HTTP окремо
/// перевірена сусідніми тестами обробника.
/// </remarks>
[Collection("SqlServer")]
public sealed class ArchivedProjectRulesTests(SqlServerFixture sql)
{
    private const string Password = "Archived-Project-Rules-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "F-11")]
    public async Task Архівований_проєкт_відмовляє_у_повторній_архівації_поточному_періоді_й_новому_документі()
    {
        var (document, userName, periodId, projectCode) = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, userName).ConfigureAwait(true);

        // F-12: повторна архівація — 409 з ключем, а не 500.
        await AssertConflictAsync(
            client.PostAsync(new Uri($"/api/v1/projects/{document.ProjectId}/archive", UriKind.Relative), null),
            "err.ECR-PRD-0409.projectAlreadyArchived", $"Project \"{projectCode}\" is already archived.", app);

        // F-12: поточний період не фіксується і не знімається.
        var projectArchived = $"Project \"{projectCode}\" is archived: its current period cannot be changed.";
        await AssertConflictAsync(
            client.PutAsJsonAsync(
                new Uri($"/api/v1/projects/{document.ProjectId}/current-period", UriKind.Relative),
                new { pinnedPeriodId = periodId, reason = "після архівації" }),
            "err.ECR-PRD-0409.projectArchivedCurrentPeriod", projectArchived, app);
        await AssertConflictAsync(
            client.PutAsJsonAsync(
                new Uri($"/api/v1/projects/{document.ProjectId}/current-period", UriKind.Relative),
                new { pinnedPeriodId = (int?)null, reason = (string?)null }),
            "err.ECR-PRD-0409.projectArchivedCurrentPeriod", projectArchived, app);

        // F-11: новий документ в архівованому проєкті не заводиться.
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var before = await db.Documents.CountAsync(d => d.ProjectId == document.ProjectId).ConfigureAwait(true);

        await AssertConflictAsync(
            client.PostAsJsonAsync(
                new Uri("/api/v1/documents", UriKind.Relative),
                new { projectId = document.ProjectId, sheetDefIds = new[] { document.SheetDefId } }),
            "err.ECR-PRD-0409.projectArchivedNoDocuments",
            $"Project {document.ProjectId} is archived: new documents cannot be created in it.", app);

        Assert.Equal(before, await db.Documents.CountAsync(d => d.ProjectId == document.ProjectId).ConfigureAwait(true));

        var project = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == document.ProjectId).ConfigureAwait(true);
        Assert.Equal(CurrentPeriodMode.Auto, project.CurrentPeriodMode);
    }

    private static async Task AssertConflictAsync(
        Task<HttpResponseMessage> call, string messageKey, string detail, EcrApiFactory app)
    {
        using var response = await call.ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(
            response.StatusCode == HttpStatusCode.Conflict,
            $"очікували 409, отримали {(int)response.StatusCode}\n{body}\n{app.ErrorsText}");

        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-PRD-0409", problem.GetProperty("errorCode").GetString());
        Assert.Equal(messageKey, problem.GetProperty("messageKey").GetString());
        Assert.Equal(detail, problem.GetProperty("detail").GetString());
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    /// <summary>
    /// Документ у проєкті, що пройшов увесь шлях Draft → Active → періоди
    /// закриті → Archived; користувач із правами й грантом Manage на проєкт.
    /// </summary>
    private async Task<(TestDocument Document, string UserName, int PeriodId, string ProjectCode)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync().ConfigureAwait(false);

        var userName = $"arch_{Guid.NewGuid():N}"[..20];
        var now = DateTime.UtcNow;

        await using var db = builder.CreateContext();

        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"ARCH_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Archived project rules" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var permission in new[] { "Project.Manage", "Period.Configure", "Document.Create", "Document.View" })
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Manage));

        var project = await db.Projects.Include(p => p.Periods)
            .FirstAsync(p => p.Id == document.ProjectId).ConfigureAwait(false);
        project.Activate(now);

        foreach (var period in project.Periods)
        {
            period.AdvanceTo(PeriodState.Closed, now);
        }

        project.Archive(now);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (document, userName, project.Periods[0].Id, project.Code);
    }
}
