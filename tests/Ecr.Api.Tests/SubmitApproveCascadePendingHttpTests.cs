// tests/Ecr.Api.Tests/SubmitApproveCascadePendingHttpTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Workflow;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// F5 (рішення людини «Заборонити, поки йде перерахунок») наскрізно через HTTP і справжню базу: поки місяць
/// документа-споживача чекає каскаду від зміни джерела (жива задача <c>doc{id}-y{рік}-dependents</c> у
/// <c>itg.JobProgress</c>, місяць у плані), подання й затвердження відмовляють 422 <c>ECR-SUB-4221</c> з
/// <see cref="SourceCascadePendingCheck.MessageKey"/>; відкликання, відхилення й повернення в роботу — ні; крок каскаду,
/// що завершився (місяць знято з плану), чи задача, що закінчилась, — подання й затвердження знову йдуть.
/// </summary>
/// <remarks>
/// Мутації (локальний worktree, не комітяться): прибрати виклик <c>SourceCascadePendingCheck</c> із
/// <c>SubmitSheetHandler</c> чи <c>ApproveSheetHandler</c> — червоний крок 1 чи 5; ворота без перевірки місяця
/// (<c>Pends</c> → <c>true</c>) — червоний крок 2 (наступний місяць ланцюжка вже не в плані).
/// </remarks>
[Collection("SqlServer")]
public sealed class SubmitApproveCascadePendingHttpTests(SqlServerFixture sql)
{
    private const string Password = "Cascade-F5-Pending!";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "F5")]
    [Trait("Decision", "D-566")]
    public async Task Подання_й_затвердження_місяця_що_чекає_каскаду_джерела_відмовляють_а_відкликання_відхилення_повернення_ні()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var author = await SignInAsync(app, s.AuthorName).ConfigureAwait(true);
        using var approver = await SignInAsync(app, s.ApproverName).ConfigureAwait(true);

        var jobId = $"f5-{Guid.NewGuid():N}";
        var target = CalculationTrigger.SourceCascadeTargetOf(s.Document.DocumentId, s.PeriodKey / 100);
        var p = s.PeriodKey.ToString(CultureInfo.InvariantCulture);
        var docId = s.Document.DocumentId.ToString(CultureInfo.InvariantCulture);

        // Задача каскаду ще чекає вікна злиття: місяць у cascadePeriods, план не ухвалено.
        var queued = $$"""{"projectId":0,"documentId":{{docId}},"year":{{s.PeriodKey / 100}},"recalculateSubmitted":true,"cascadePeriods":[{{p}}]}""";

        // Ланцюжок виконує крок МІСЯЦЯ (він у решті плану chainPlan).
        var running = $$"""{"projectId":0,"documentId":{{docId}},"year":{{s.PeriodKey / 100}},"recalculateSubmitted":true,"cascadePeriods":[{{p}}],"chainAfter":{{s.PeriodKey - 1}},"chainDone":[{{s.PeriodKey - 1}}],"chainPlan":[{{p}}]}""";

        // Крок місяця завершився, ланцюжок продовжено далі — місяця в плані вже немає.
        var stepDone = $$"""{"projectId":0,"documentId":{{docId}},"year":{{s.PeriodKey / 100}},"recalculateSubmitted":true,"cascadePeriods":[{{p}}],"chainAfter":{{p}},"chainDone":[{{p}}],"chainPlan":[{{s.PeriodKey + 1}}]}""";

        await InsertJobAsync(jobId, s.Document.DocumentId, target, "Queued", queued).ConfigureAwait(true);

        // 1. Подання, поки місяць чекає каскаду, — 422 з ключем; стан не змінився.
        var refused = await ExpectAsync(app, HttpStatusCode.UnprocessableEntity, WorkflowAsync(author, s, "submit")).ConfigureAwait(true);
        AssertCascadeRefusal(refused);
        Assert.Null(await StatusAsync(s).ConfigureAwait(true));

        // 2. Крок місяця завершився (ланцюжок іде далі) — подання проходить.
        await SetJobAsync(jobId, "Running", stepDone).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.NoContent, WorkflowAsync(author, s, "submit")).ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(s).ConfigureAwait(true));

        // 3. Місяць знову в плані — відкликання не блокується.
        await SetJobAsync(jobId, "Running", running).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.NoContent, RecallAsync(author, s)).ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Draft, await StatusAsync(s).ConfigureAwait(true));

        // 4. Задача закінчилась — подання проходить.
        await SetJobAsync(jobId, "Succeeded", running).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.NoContent, WorkflowAsync(author, s, "submit")).ConfigureAwait(true);

        // 5. Місяць чекає — затвердження 422 з ключем, стан Submitted; відхилення проходить.
        await SetJobAsync(jobId, "Running", running).ConfigureAwait(true);
        var approveRefused = await ExpectAsync(
            app, HttpStatusCode.UnprocessableEntity, ApproveAsync(approver, s, approved: true, reason: null)).ConfigureAwait(true);
        AssertCascadeRefusal(approveRefused);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(s).ConfigureAwait(true));

        await ExpectAsync(
            app, HttpStatusCode.NoContent, ApproveAsync(approver, s, approved: false, reason: "F5: повернути")).ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Rejected, await StatusAsync(s).ConfigureAwait(true));

        // 6. Без каскаду — подання й затвердження проходять.
        await SetJobAsync(jobId, "Succeeded", running).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.NoContent, WorkflowAsync(author, s, "submit")).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.NoContent, ApproveAsync(approver, s, approved: true, reason: null)).ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Approved, await StatusAsync(s).ConfigureAwait(true));

        // 7. Місяць чекає — повернення в роботу не блокується.
        await SetJobAsync(jobId, "Queued", queued).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.NoContent, ReopenAsync(approver, s)).ConfigureAwait(true);
        Assert.NotEqual(DocumentStatus.Approved, await StatusAsync(s).ConfigureAwait(true));

        await SetJobAsync(jobId, "Succeeded", queued).ConfigureAwait(true);
    }

    private static void AssertCascadeRefusal(string body)
    {
        Assert.Contains("ECR-SUB-4221", body, StringComparison.Ordinal);
        Assert.Contains(SourceCascadePendingCheck.MessageKey, body, StringComparison.Ordinal);
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scenario(
        TestDocumentBuilder Builder, TestDocument Document, string AuthorName, string ApproverName, int PeriodKey);

    /// <summary>
    /// Рядок черги каскаду без лейна: у хості тестів черга — Quartz (<c>D-563</c>), і рядок не бере жоден виконавець;
    /// ворота читають лише стан, ціль і payload.
    /// </summary>
    private async Task InsertJobAsync(string jobId, long documentId, string target, string state, string payload)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO itg.JobProgress
                (JobId, JobCode, [State], [Percent], StartedAt, UpdatedAt, HeartbeatAt, CreatedAt, DocumentId, TargetKey, Payload)
            VALUES (@id, N'Ecr.Test.F5Cascade', @state, 0, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(),
                    @doc, @target, @payload);
            """;
        command.Parameters.AddWithValue("@id", jobId);
        command.Parameters.AddWithValue("@state", state);
        command.Parameters.AddWithValue("@doc", documentId);
        command.Parameters.AddWithValue("@target", target);
        command.Parameters.AddWithValue("@payload", payload);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task SetJobAsync(string jobId, string state, string payload)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE itg.JobProgress
               SET [State] = @state, Payload = @payload, UpdatedAt = SYSUTCDATETIME(), HeartbeatAt = SYSUTCDATETIME()
             WHERE JobId = @id;
            """;
        command.Parameters.AddWithValue("@id", jobId);
        command.Parameters.AddWithValue("@state", state);
        command.Parameters.AddWithValue("@payload", payload);
        Assert.Equal(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
    }

    private static async Task<string> ExpectAsync(EcrApiFactory app, HttpStatusCode expected, Task<HttpResponseMessage> call)
    {
        using var response = await call.ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(
            response.StatusCode == expected,
            $"{(int)response.StatusCode} замість {(int)expected}\n{body}\n{app.ErrorsText}");

        return body;
    }

    private static Task<HttpResponseMessage> WorkflowAsync(HttpClient client, Scenario s, string action)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/{action}", UriKind.Relative),
            new { sheetDefId = s.Document.SheetDefId, periodKey = s.PeriodKey });

    private static Task<HttpResponseMessage> RecallAsync(HttpClient client, Scenario s)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/recall", UriKind.Relative),
            new { sheetDefId = s.Document.SheetDefId, periodKey = s.PeriodKey, reason = "F5: відкликати" });

    private static Task<HttpResponseMessage> ReopenAsync(HttpClient client, Scenario s)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/reopen", UriKind.Relative),
            new { sheetDefId = s.Document.SheetDefId, periodKey = s.PeriodKey, reason = "F5: повернути в роботу" });

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, Scenario s, bool approved, string? reason)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/approve", UriKind.Relative),
            new { sheetDefId = s.Document.SheetDefId, periodKey = s.PeriodKey, approved, reason });

    private static async Task<DocumentStatus?> StatusAsync(Scenario s)
    {
        await using var db = s.Builder.CreateContext();

        return await db.ApprovalStates.AsNoTracking()
            .Where(a => a.DocumentId == s.Document.DocumentId
                        && a.SheetDefId == s.Document.SheetDefId
                        && a.PeriodKey == s.PeriodKey)
            .Select(a => (DocumentStatus?)a.Status)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
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
    /// Документ у ВІДКРИТОМУ поточному періоді активного проєкту (як <c>RejectedSheetHttpFlowTests</c>) і два
    /// користувачі: автор (<c>Submit</c>) і погоджувач (<c>Approve</c> + право <c>Document.Reopen</c>).
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
        var tag = Guid.NewGuid().ToString("N")[..10];
        var authorName = $"f5a_{tag}";
        var approverName = $"f5p_{tag}";

        await using var db = builder.CreateContext();
        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, document.SheetDefId));

        foreach (var (name, level) in new[] { (authorName, GrantLevel.Submit), (approverName, GrantLevel.Approve) })
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(
                EcrCode.Create($"F5{Guid.NewGuid():N}"[..14]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "F5 cascade" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
            if (level == GrantLevel.Approve)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, ReopenDocumentHandler.Permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, level));
        }

        var project = await db.Projects.FirstAsync(p => p.Id == document.ProjectId).ConfigureAwait(false);
        project.Activate(now);

        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var period = await db.Periods
            .FirstAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey)
            .ConfigureAwait(false);
        period.RecomputeBoundaries(policy, SiteZone);
        period.AdvanceTo(PeriodState.Open, now);

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(builder, document, authorName, approverName, periodKey);
    }
}
