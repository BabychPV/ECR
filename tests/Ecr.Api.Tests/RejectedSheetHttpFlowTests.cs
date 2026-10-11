// tests/Ecr.Api.Tests/RejectedSheetHttpFlowTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ФВ-5.15 / D-219 наскрізно через HTTP: відхилення аркуша — обов'язковий коментар; відхилений
/// аркуш редагується як <c>Draft</c>, подається знову й лише тоді затверджується.
/// </summary>
/// <remarks>
/// ⛔ До цього файлу «відхилити» мав лише доменний тест (<c>ApprovalStateTests</c>) і тест стору в
/// пам'яті (<c>SubmitApproveTests</c>): ні HTTP <c>approved:false</c>, ні «PATCH на відхиленому
/// аркуші», ні повторне подання не ходили справжнім стеком. Ті самі кроки ловлять і ФВ-5.13
/// («Submitted заморожено» — PATCH 403 <c>DocumentSubmitted</c>) та ФВ-5.14 («Approved» — PATCH 403
/// <c>DocumentApproved</c> і незмінний <c>calc.SubmissionSnapshot</c>).
///
/// Мутаційні докази (detached-worktree, не комітяться): (а) у <c>EditRules.CanEdit</c> додати
/// <c>case DocumentStatus.Rejected: return Deny(DocumentSubmitted)</c> — червоніє PATCH на
/// відхиленому (крок 5); (б) у <c>ApprovalState.Reject</c> прибрати перевірку коментаря — червоніє
/// «без причини → 422»; (в) у <c>ApprovalState.Submit</c> прибрати <c>or DocumentStatus.Rejected</c> —
/// червоніє повторне подання; (г) у <c>EditRules.CanApprove</c> дозволити не-Submitted — червоніє
/// «Rejected не затверджується».
/// </remarks>
[Collection("SqlServer")]
public sealed class RejectedSheetHttpFlowTests(SqlServerFixture sql)
{
    private const string Password = "Reject-Flow-D219!";
    private const string RejectReason = "Невірні значення у рядку 3";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.15")]
    [Trait("Requirement", "ФВ-5.13")]
    [Trait("Requirement", "ФВ-5.14")]
    [Trait("Decision", "D-219")]
    public async Task Відхилення_потребує_коментаря_відхилений_аркуш_редагується_подається_знову_і_лише_тоді_затверджується()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var author = await SignInAsync(app, s.AuthorName).ConfigureAwait(true);
        using var approver = await SignInAsync(app, s.ApproverName).ConfigureAwait(true);

        var numberColumn = await NumberColumnCodeAsync(author, s).ConfigureAwait(true);
        var rowKey = $"RJ{Guid.NewGuid():N}"[..12];

        // Існуючий рядок правиться лише з його версією (baseVersion), тож свіжу версію беремо зі зрізу.
        async Task<string> VersionAsync() => await RowVersionAsync(author, s, rowKey).ConfigureAwait(true);

        // 1. Draft: запис дозволений (контроль, що сценарій узагалі вміє писати).
        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(author, s, rowKey, numberColumn, 10m, null)).ConfigureAwait(true);
        Assert.Equal(10m, await StoredValueAsync(s).ConfigureAwait(true));

        // 2. Submit → Submitted; ФВ-5.13: автору запис заморожено.
        await ExpectAsync(app, HttpStatusCode.NoContent, WorkflowAsync(author, s, "submit")).ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(s).ConfigureAwait(true));

        var frozen = await ExpectAsync(
            app, HttpStatusCode.Forbidden, PatchAsync(author, s, rowKey, numberColumn, 11m, await VersionAsync())).ConfigureAwait(true);
        Assert.Equal("DocumentSubmitted", ReasonOf(frozen));
        Assert.Equal(10m, await StoredValueAsync(s).ConfigureAwait(true));

        // 3. Відхилення без причини → 422, стан не змінився.
        var noReason = await ExpectAsync(
            app, HttpStatusCode.UnprocessableEntity, ApproveAsync(approver, s, approved: false, reason: null))
            .ConfigureAwait(true);
        Assert.Contains("ECR-DOC-0422", noReason, StringComparison.Ordinal);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(s).ConfigureAwait(true));

        // 4. Відхилення з причиною → 204, Rejected, причина збережена.
        await ExpectAsync(
            app, HttpStatusCode.NoContent, ApproveAsync(approver, s, approved: false, reason: RejectReason))
            .ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Rejected, await StatusAsync(s).ConfigureAwait(true));
        Assert.Equal(RejectReason, await RejectedReasonAsync(s).ConfigureAwait(true));

        // 5. Rejected не затверджується: це вже не «подане».
        var notSubmitted = await ExpectAsync(
            app, HttpStatusCode.Forbidden, ApproveAsync(approver, s, approved: true, reason: null)).ConfigureAwait(true);
        Assert.Equal("BusinessRule", ReasonOf(notSubmitted));
        Assert.Equal(DocumentStatus.Rejected, await StatusAsync(s).ConfigureAwait(true));

        // 6. D-219: відхилений аркуш редагується як Draft.
        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(author, s, rowKey, numberColumn, 20m, await VersionAsync())).ConfigureAwait(true);
        Assert.Equal(20m, await StoredValueAsync(s).ConfigureAwait(true));
        Assert.Equal(DocumentStatus.Rejected, await StatusAsync(s).ConfigureAwait(true));

        // 7. Повторне подання з Rejected → Submitted; причина відхилення скинута, зріз подання новий.
        var snapshotsBeforeResubmit = await SubmissionSnapshotsAsync(s).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.NoContent, WorkflowAsync(author, s, "submit")).ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(s).ConfigureAwait(true));
        Assert.Null(await RejectedReasonAsync(s).ConfigureAwait(true));

        var afterResubmit = await SubmissionSnapshotsAsync(s).ConfigureAwait(true);
        Assert.True(
            afterResubmit.Count > snapshotsBeforeResubmit.Count && afterResubmit[^1].Payload.Contains("20", StringComparison.Ordinal),
            "повторне подання мусить зафіксувати нову версію з виправленим значенням");

        // 8. Затвердження → Approved; ФВ-5.14: запис заборонено, зафіксована версія не змінилась.
        await ExpectAsync(
            app, HttpStatusCode.NoContent, ApproveAsync(approver, s, approved: true, reason: null)).ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Approved, await StatusAsync(s).ConfigureAwait(true));

        var fixedVersion = await SubmissionSnapshotsAsync(s).ConfigureAwait(true);

        var locked = await ExpectAsync(
            app, HttpStatusCode.Forbidden, PatchAsync(author, s, rowKey, numberColumn, 30m, await VersionAsync())).ConfigureAwait(true);
        Assert.Equal("DocumentApproved", ReasonOf(locked));
        Assert.Equal(20m, await StoredValueAsync(s).ConfigureAwait(true));
        Assert.Equal(fixedVersion, await SubmissionSnapshotsAsync(s).ConfigureAwait(true));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scenario(
        TestDocumentBuilder Builder, TestDocument Document, string AuthorName, string ApproverName, int PeriodKey);

    private static async Task<string> ExpectAsync(
        EcrApiFactory app, HttpStatusCode expected, Task<HttpResponseMessage> call)
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

    private static Task<HttpResponseMessage> WorkflowAsync(HttpClient client, Scenario s, string action)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/{action}", UriKind.Relative),
            new { sheetDefId = s.Document.SheetDefId, periodKey = s.PeriodKey });

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, Scenario s, bool approved, string? reason)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/approve", UriKind.Relative),
            new { sheetDefId = s.Document.SheetDefId, periodKey = s.PeriodKey, approved, reason });

    private static Task<HttpResponseMessage> PatchAsync(
        HttpClient client, Scenario s, string rowKey, string columnCode, decimal value, string? baseVersion)
        => client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = s.Document.TableInstanceId,
                periodKey = s.PeriodKey,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey,
                        baseVersion,
                        cells = new object[] { new { columnCode, value = (object)value.ToString(System.Globalization.CultureInfo.InvariantCulture) } },
                    },
                },
            });

    /// <summary>Поточна версія рядка (rowVersion) зі зрізу таблиці.</summary>
    private static async Task<string> RowVersionAsync(HttpClient client, Scenario s, string rowKey)
    {
        using var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Document.DocumentId}/tables/{s.Document.TableInstanceId}", UriKind.Relative))
            .ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"GET зрізу: {response.StatusCode}\n{text}");

        foreach (var row in JsonDocument.Parse(text).RootElement.GetProperty("rows").EnumerateArray())
        {
            if (string.Equals(row.GetProperty("rowKey").GetString(), rowKey, StringComparison.Ordinal))
            {
                return row.GetProperty("rowVersion").GetString()!;
            }
        }

        Assert.Fail($"Рядка «{rowKey}» немає у зрізі.\n{text}");
        return string.Empty;
    }

    /// <summary>Код числової колонки береться зі ЗРІЗУ — так його дізнається й клієнт.</summary>
    private static async Task<string> NumberColumnCodeAsync(HttpClient client, Scenario s)
    {
        using var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Document.DocumentId}/tables/{s.Document.TableInstanceId}", UriKind.Relative))
            .ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"GET зрізу: {response.StatusCode}\n{text}");

        return JsonDocument.Parse(text).RootElement.GetProperty("columns").EnumerateArray()
            .Select(c => c.GetProperty("code").GetString()!)
            .ElementAt(1);
    }

    private async Task<DocumentStatus?> StatusAsync(Scenario s)
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

    private async Task<string?> RejectedReasonAsync(Scenario s)
    {
        await using var db = s.Builder.CreateContext();

        return await db.ApprovalStates.AsNoTracking()
            .Where(a => a.DocumentId == s.Document.DocumentId
                        && a.SheetDefId == s.Document.SheetDefId
                        && a.PeriodKey == s.PeriodKey)
            .Select(a => a.RejectedReason)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    private async Task<decimal?> StoredValueAsync(Scenario s)
    {
        await using var db = s.Builder.CreateContext();

        return await db.CellValues.AsNoTracking()
            .Where(c => c.PeriodKeyValue == s.PeriodKey && c.ColumnDefId == s.Document.ColumnDefIds[1])
            .Select(c => c.ValueNumeric)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    /// <summary>Зафіксовані версії подання (<c>calc.SubmissionSnapshot</c>) аркуша за порядком створення.</summary>
    private async Task<List<(long Id, string Payload)>> SubmissionSnapshotsAsync(Scenario s)
    {
        await using var db = s.Builder.CreateContext();

        var rows = await db.Database
            .SqlQuery<SnapshotRow>(
                $"""
                SELECT Id, PayloadJson FROM calc.SubmissionSnapshot
                WHERE DocumentId = {s.Document.DocumentId} AND SheetDefId = {s.Document.SheetDefId}
                  AND PeriodKey = {s.PeriodKey}
                ORDER BY Id
                """)
            .ToListAsync()
            .ConfigureAwait(false);

        return [.. rows.Select(r => (r.Id, r.PayloadJson))];
    }

    private sealed record SnapshotRow(long Id, string PayloadJson);

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
    /// Документ у ВІДКРИТОМУ поточному періоді активного проєкту (як у <c>CellWriteRoundTripTests</c>:
    /// інакше <c>PeriodStateJob</c> на старті застосунку закрив би минулий місяць) і два користувачі:
    /// автор (рівень <c>Submit</c>) та погоджувач (рівень <c>Approve</c>) — автор не погоджує власне подання.
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
        var authorName = $"rja_{tag}";
        var approverName = $"rjp_{tag}";

        await using var db = builder.CreateContext();

        // Склад документа: без рядка складу аркуша «немає», і подання відмовляє.
        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, document.SheetDefId));

        foreach (var (name, level) in new[] { (authorName, GrantLevel.Submit), (approverName, GrantLevel.Approve) })
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(
                EcrCode.Create($"RJ{Guid.NewGuid():N}"[..14]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "D-219 flow" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
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
