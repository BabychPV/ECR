// tests/Ecr.Api.Tests/ApproverEditHttpFlowTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Documents;
using Ecr.Application.Workflow;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D-442 (BRIEF-1021 П.3) наскрізно через HTTP: після <c>Submit</c> поданий аркуш править ЛИШЕ погоджувач
/// поточного кроку маршруту; правка — у журналі з позначкою «правка погоджувача»
/// (<c>aud.CellChange.Origin = ApproverEdit</c>); при затвердженні знімається новий зріз даних із цією правкою.
/// </summary>
/// <remarks>
/// Матриця прав на поданому аркуші: погоджувач поточного кроку — так; автор (рівень <c>Submit</c>) — ні;
/// погоджувач ІНШОГО кроку — ні; сторонній із <c>Write</c> — ні. Після кроку 1 черга переходить — і право
/// правити переходить разом із нею. Затверджений аркуш не править ніхто.
///
/// Мутаційні докази (у локальному worktree, не комітяться):
/// (а) <c>EditRules.CanEdit</c>: гілку <c>Submitted when IsCurrentApprover</c> прибрати — червоніє крок
///     «погоджувач першого кроку править» (403 DocumentSubmitted);
/// (б) <c>AccessDecisionService.IsCurrentApproverAsync</c>: не передавати роль кроку
///     (<c>CanApprove(profile, ctx)</c> без <c>step?.RoleId</c>) — червоніє «погоджувач ІНШОГО кроку не править»;
/// (в) <c>PatchCellsHandler.EnsureSheetStillEditableAsync</c>: не перевіряти погоджувача під блокуванням
///     (одразу <c>ApproverEdit = true</c> при <c>ByApprover</c>) — тут зелено (кроки йдуть послідовно), гонку
///     ловить <c>PatchCellsTests.D442_черга_перейшла_під_блокуванням_правку_погоджувача_відхилено</c>; (г) <c>BuildAuditRecords</c>: <c>batchOrigin = request.Origin</c> —
///     червоніє перевірка походження <c>ApproverEdit</c>; (д) <c>ApproveSheetHandler</c>: не кликати
///     <c>ApprovalSnapshotRefresh.RefreshAsync</c> — червоніє «зріз затвердження містить правку».
/// </remarks>
[Collection("SqlServer")]
public sealed class ApproverEditHttpFlowTests(SqlServerFixture sql)
{
    private const string Password = "Approver-Edit-D442!";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-442")]
    [Trait("Requirement", "ФВ-5.17")]
    public async Task Поданий_аркуш_править_лише_погоджувач_поточного_кроку_з_позначкою_і_новим_зрізом()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var submitter = await NewUserAsync(s, GrantLevel.Submit).ConfigureAwait(true);
        var first = await NewUserAsync(s, GrantLevel.Approve).ConfigureAwait(true);
        var second = await NewUserAsync(s, GrantLevel.Approve).ConfigureAwait(true);
        var outsider = await NewUserAsync(s, GrantLevel.Write).ConfigureAwait(true);
        await RouteAsync(s, first.RoleId, second.RoleId).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var author = await SignInAsync(app, submitter.Name).ConfigureAwait(true);
        using var firstClient = await SignInAsync(app, first.Name).ConfigureAwait(true);
        using var secondClient = await SignInAsync(app, second.Name).ConfigureAwait(true);
        using var outsiderClient = await SignInAsync(app, outsider.Name).ConfigureAwait(true);

        var column = await NumberColumnCodeAsync(author, s).ConfigureAwait(true);
        var rowKey = $"AE{Guid.NewGuid():N}"[..12];

        // Draft: автор пише й подає.
        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(author, s, rowKey, column, 10m, null)).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.NoContent, WorkflowAsync(author, s, "submit")).ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(s).ConfigureAwait(true));
        Assert.Single(await SnapshotsAsync(s).ConfigureAwait(true));

        // Матриця на кроці 1: автор, сторонній і погоджувач ДРУГОГО кроку — 403 DocumentSubmitted.
        foreach (var (client, who) in new[] { (author, "автор"), (outsiderClient, "сторонній"), (secondClient, "погоджувач іншого кроку") })
        {
            var denied = await ExpectAsync(
                    app, HttpStatusCode.Forbidden,
                    PatchAsync(client, s, rowKey, column, 99m, await RowVersionAsync(client, s, rowKey).ConfigureAwait(true)))
                .ConfigureAwait(true);
            Assert.True(ReasonOf(denied) == nameof(EditDenyReason.DocumentSubmitted), $"{who}: {denied}");
        }

        Assert.Equal(10m, await StoredValueAsync(s, rowKey).ConfigureAwait(true));

        // Погоджувач поточного (першого) кроку править — аркуш лишається Submitted, журнал — ApproverEdit.
        await ExpectAsync(
                app, HttpStatusCode.OK,
                PatchAsync(firstClient, s, rowKey, column, 11m, await RowVersionAsync(firstClient, s, rowKey).ConfigureAwait(true)))
            .ConfigureAwait(true);
        Assert.Equal(11m, await StoredValueAsync(s, rowKey).ConfigureAwait(true));
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(s).ConfigureAwait(true));
        Assert.Equal(
            (CellChangeOrigins.ApproverEdit, await UserIdAsync(s, first.Name).ConfigureAwait(true)),
            await LastChangeAsync(s).ConfigureAwait(true));

        // Крок 1 погоджено: новий зріз містить правку погоджувача; черга (і право правити) — у другого.
        await ExpectAsync(app, HttpStatusCode.NoContent, ApproveAsync(firstClient, s)).ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(s).ConfigureAwait(true));
        var afterStep = await SnapshotsAsync(s).ConfigureAwait(true);
        Assert.Equal(2, afterStep.Count);
        Assert.Equal(11m, CellValue(afterStep[^1], s));

        var stale = await ExpectAsync(
                app, HttpStatusCode.Forbidden,
                PatchAsync(firstClient, s, rowKey, column, 98m, await RowVersionAsync(firstClient, s, rowKey).ConfigureAwait(true)))
            .ConfigureAwait(true);
        Assert.Equal(nameof(EditDenyReason.DocumentSubmitted), ReasonOf(stale));

        await ExpectAsync(
                app, HttpStatusCode.OK,
                PatchAsync(secondClient, s, rowKey, column, 12m, await RowVersionAsync(secondClient, s, rowKey).ConfigureAwait(true)))
            .ConfigureAwait(true);
        Assert.Equal(
            (CellChangeOrigins.ApproverEdit, await UserIdAsync(s, second.Name).ConfigureAwait(true)),
            await LastChangeAsync(s).ConfigureAwait(true));

        // Остаточне затвердження: зріз — з правкою другого; далі не править ніхто.
        await ExpectAsync(app, HttpStatusCode.NoContent, ApproveAsync(secondClient, s)).ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Approved, await StatusAsync(s).ConfigureAwait(true));
        var afterApprove = await SnapshotsAsync(s).ConfigureAwait(true);
        Assert.Equal(3, afterApprove.Count);
        Assert.Equal(12m, CellValue(afterApprove[^1], s));

        var locked = await ExpectAsync(
                app, HttpStatusCode.Forbidden,
                PatchAsync(secondClient, s, rowKey, column, 13m, await RowVersionAsync(secondClient, s, rowKey).ConfigureAwait(true)))
            .ConfigureAwait(true);
        Assert.Equal(nameof(EditDenyReason.DocumentApproved), ReasonOf(locked));
        Assert.Equal(12m, await StoredValueAsync(s, rowKey).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-442")]
    public async Task Затвердження_без_правок_погоджувача_нового_зрізу_не_дає()
    {
        // Без маршруту погоджувач — той, кому `CanApprove` дозволяє (грант Approve): затвердження одноетапне.
        var s = await ArrangeAsync().ConfigureAwait(true);
        var submitter = await NewUserAsync(s, GrantLevel.Submit).ConfigureAwait(true);
        var approver = await NewUserAsync(s, GrantLevel.Approve).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var author = await SignInAsync(app, submitter.Name).ConfigureAwait(true);
        using var approverClient = await SignInAsync(app, approver.Name).ConfigureAwait(true);

        var column = await NumberColumnCodeAsync(author, s).ConfigureAwait(true);
        var rowKey = $"AN{Guid.NewGuid():N}"[..12];

        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(author, s, rowKey, column, 7m, null)).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.NoContent, WorkflowAsync(author, s, "submit")).ConfigureAwait(true);
        var submitted = await SnapshotsAsync(s).ConfigureAwait(true);

        await ExpectAsync(app, HttpStatusCode.NoContent, ApproveAsync(approverClient, s)).ConfigureAwait(true);

        Assert.Equal(DocumentStatus.Approved, await StatusAsync(s).ConfigureAwait(true));
        Assert.Equal(submitted, await SnapshotsAsync(s).ConfigureAwait(true));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scenario(TestDocumentBuilder Builder, TestDocument Document, int PeriodKey);

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

    private static Task<HttpResponseMessage> WorkflowAsync(HttpClient client, Scenario s, string action)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/{action}", UriKind.Relative),
            new { sheetDefId = s.Document.SheetDefId, periodKey = s.PeriodKey });

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, Scenario s)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/approve", UriKind.Relative),
            new { sheetDefId = s.Document.SheetDefId, periodKey = s.PeriodKey, approved = true, reason = (string?)null });

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

    private static decimal CellValue(string payload, Scenario s)
    {
        var cell = Assert.Single(
            SubmissionPayload.Read(payload),
            c => c.Column == s.Document.ColumnDefIds[1] && c.Value is not null);

        return decimal.Parse(cell.Value!, System.Globalization.CultureInfo.InvariantCulture);
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

    private async Task<decimal?> StoredValueAsync(Scenario s, string rowKey)
    {
        await using var db = s.Builder.CreateContext();

        return await (
                from c in db.CellValues.AsNoTracking()
                join r in db.TableRows.AsNoTracking()
                    on new { c.PeriodKeyValue, Id = c.TableRowId } equals new { r.PeriodKeyValue, r.Id }
                where c.PeriodKeyValue == s.PeriodKey
                      && c.ColumnDefId == s.Document.ColumnDefIds[1]
                      && r.TableInstanceId == s.Document.TableInstanceId
                      && r.RowKey == rowKey
                select c.ValueNumeric)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    /// <summary>Походження й автор ОСТАННЬОГО запису журналу числової колонки документа.</summary>
    private async Task<(string Origin, int UserId)> LastChangeAsync(Scenario s)
    {
        await using var db = s.Builder.CreateContext();

        var row = await db.Database
            .SqlQuery<ChangeRow>(
                $"""
                SELECT TOP (1) Origin, ChangedByUserId FROM aud.CellChange
                WHERE DocumentId = {s.Document.DocumentId} AND ColumnDefId = {s.Document.ColumnDefIds[1]}
                  AND PeriodKey = {s.PeriodKey}
                ORDER BY Id DESC
                """)
            .SingleAsync()
            .ConfigureAwait(false);

        return (row.Origin, row.ChangedByUserId);
    }

    private sealed record ChangeRow(string Origin, int ChangedByUserId);

    private async Task<int> UserIdAsync(Scenario s, string name)
    {
        await using var db = s.Builder.CreateContext();

        return await db.Users.AsNoTracking().Where(u => u.UserName == name).Select(u => u.Id).SingleAsync()
            .ConfigureAwait(false);
    }

    /// <summary>Зрізи подання аркуша (<c>calc.SubmissionSnapshot</c>) за порядком створення.</summary>
    private async Task<List<string>> SnapshotsAsync(Scenario s)
    {
        await using var db = s.Builder.CreateContext();

        return await db.Database
            .SqlQuery<string>(
                $"""
                SELECT PayloadJson AS Value FROM calc.SubmissionSnapshot
                WHERE DocumentId = {s.Document.DocumentId} AND SheetDefId = {s.Document.SheetDefId}
                  AND PeriodKey = {s.PeriodKey}
                ORDER BY Id
                """)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    private async Task RouteAsync(Scenario s, params int[] roleIds)
    {
        await using var db = s.Builder.CreateContext();
        var route = new ApprovalRoute(
            EcrCode.Create($"D442_{Guid.NewGuid():N}"[..20]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "D-442 route" }),
            s.Document.ProjectId);
        foreach (var roleId in roleIds)
        {
            route.AddStep(roleId);
        }

        db.ApprovalRoutes.Add(route);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Користувач із власною роллю: грант <paramref name="level"/> на проєкт документа.</summary>
    private async Task<(string Name, int RoleId)> NewUserAsync(Scenario s, GrantLevel level)
    {
        var name = $"d442_{Guid.NewGuid():N}"[..20];

        await using var db = s.Builder.CreateContext();
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "D-442" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, s.Document.ProjectId, level));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (name, role.Id);
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
    /// Документ у ВІДКРИТОМУ поточному періоді активного проєкту (як у <c>RejectedSheetHttpFlowTests</c>:
    /// інакше <c>PeriodStateJob</c> на старті застосунку закрив би минулий місяць).
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

        return new Scenario(builder, document, periodKey);
    }
}
