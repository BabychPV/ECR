// tests/Ecr.Api.Tests/YearLayoutYearModesApiTests.cs
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Validation;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
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
/// ✎ LAND-YEAR L6: «річні режими» читань документа річної розкладки (<c>YearWithMonthColumns</c>, якір 209901) —
/// проблеми розрахунку (<c>monthNumber</c>), Issues (<c>GET …/validation?year=true</c>), журнал переходів
/// (<c>…/workflow/history?year=true</c>) і журнал аркуша (<c>…/sheets/{id}/activity?year=true</c>): дані листопада
/// (209911) видно в річному перегляді з позначкою місяця.
/// </summary>
/// <remarks>
/// Allow/Deny пара (жодних змін фільтрів доступу): два користувачі, що відрізняються лише областю ролі за періодами.
/// «Січень–листопад» бачить і січень, і листопад; «лише січень» — тільки січень: межі кожного місяця — СВОЇ (D-214).
/// </remarks>
[Collection("SqlServer")]
public sealed class YearLayoutYearModesApiTests(SqlServerFixture sql)
{
    private const string Password = "Year-Modes-L6-2026!";
    private const string FlagVariable = "ECR_Collection__MonthColumnRouting";
    private const int Jan = 209901;
    private const int Nov = 209911;

    private static readonly DateTime Now = new(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-474")]
    public async Task Проблеми_розрахунку_несуть_місяць_і_рік_збирає_всі_місяці_в_межах_ролі()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        try
        {
            await using (var db = s.Builder.CreateContext())
            {
                db.CalculationIssues.Add(new CalculationIssue(s.Doc.DocumentId, Jan, s.Doc.TableDefId, null, s.RowKey, 1, null, 1, Now));
                db.CalculationIssues.Add(new CalculationIssue(s.Doc.DocumentId, Nov, s.Doc.TableDefId, null, s.RowKey, 1, null, 1, Now));
                await db.SaveChangesAsync().ConfigureAwait(true);
            }

            using var app = CreateApp();
            using var wide = await SignInAsync(app, await AddUserAsync(s, Jan, Nov).ConfigureAwait(true)).ConfigureAwait(true);
            using var narrow = await SignInAsync(app, await AddUserAsync(s, Jan, Jan).ConfigureAwait(true)).ConfigureAwait(true);

            // Один період річного документа — рядок з місяцем свого періоду.
            var single = await GetJsonAsync(app, wide, $"/api/v1/documents/{s.Doc.DocumentId}/calculation-issues?periodKey={Jan}")
                .ConfigureAwait(true);
            Assert.Equal([1], Months(single.GetProperty("items"), "monthNumber"));

            // Allow: рік — січень і листопад.
            var year = await GetJsonAsync(app, wide, $"/api/v1/documents/{s.Doc.DocumentId}/calculation-issues?periodKey={Jan}&year=true")
                .ConfigureAwait(true);
            Assert.Equal([1, 11], Months(year.GetProperty("items"), "monthNumber"));

            // Deny: роль «лише січень» листопада не бачить.
            var denied = await GetJsonAsync(app, narrow, $"/api/v1/documents/{s.Doc.DocumentId}/calculation-issues?periodKey={Jan}&year=true")
                .ConfigureAwait(true);
            Assert.Equal([1], Months(denied.GetProperty("items"), "monthNumber"));
        }
        finally
        {
            await CleanupAsync(s).ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-474")]
    public async Task Issues_року_збирають_перевірки_всіх_місяців_з_періодом_у_межах_ролі()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        try
        {
            await using (var db = s.Builder.CreateContext())
            {
                foreach (var (period, rule) in new[] { (Jan, "RULE-L6-JAN"), (Nov, "RULE-L6-NOV") })
                {
                    var messages = new List<ValidationMessage>
                    {
                        new(ValidationSeverity.Error, rule, "year mode issue " + rule, s.Doc.TableDefId, s.RowKey, null, false),
                    };
                    db.ValidationResults.Add(new ValidationResult(
                        s.Doc.DocumentId, period, Now.AddMinutes(period % 100), 1, 0, 0, JsonSerializer.Serialize(messages)));
                }

                await db.SaveChangesAsync().ConfigureAwait(true);
            }

            using var app = CreateApp();
            using var wide = await SignInAsync(app, await AddUserAsync(s, Jan, Nov).ConfigureAwait(true)).ConfigureAwait(true);
            using var narrow = await SignInAsync(app, await AddUserAsync(s, Jan, Jan).ConfigureAwait(true)).ConfigureAwait(true);

            // Без прапора — як доти: лише січень, без періоду в рядку.
            var single = await GetJsonAsync(app, wide, $"/api/v1/documents/{s.Doc.DocumentId}/validation?periodKey={Jan}")
                .ConfigureAwait(true);
            Assert.Equal(["RULE-L6-JAN"], Rules(single));

            // Allow: рік — обидва місяці, кожне з періодом прогону.
            var year = await GetJsonAsync(app, wide, $"/api/v1/documents/{s.Doc.DocumentId}/validation?periodKey={Jan}&year=true")
                .ConfigureAwait(true);
            Assert.Equal(["RULE-L6-JAN", "RULE-L6-NOV"], Rules(year));
            var nov = year.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("ruleCode").GetString() == "RULE-L6-NOV");
            Assert.Equal(Nov, nov.GetProperty("periodKey").GetInt32());
            Assert.Equal([Jan, Nov], year.GetProperty("validatedPeriods").EnumerateArray().Select(p => p.GetInt32()).ToList());

            // Deny: «лише січень» не бачить правила листопада (зміст прихованого не доходить).
            var denied = await GetJsonAsync(app, narrow, $"/api/v1/documents/{s.Doc.DocumentId}/validation?periodKey={Jan}&year=true")
                .ConfigureAwait(true);
            Assert.DoesNotContain("RULE-L6-NOV", denied.GetRawText(), StringComparison.Ordinal);
            Assert.Contains("RULE-L6-JAN", Rules(denied));
        }
        finally
        {
            await CleanupAsync(s).ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-474")]
    public async Task Журнал_переходів_і_журнал_аркуша_року_містять_листопад_лише_в_межах_ролі()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        try
        {
            await using (var db = s.Builder.CreateContext())
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO wf.ApprovalEvent (DocumentId, SheetDefId, PeriodKey, FromStatus, ToStatus, Action, ByUserId, At, Reason)
                    VALUES ({s.Doc.DocumentId}, {s.Doc.SheetDefId}, {Jan}, 1, 2, 4, NULL, {DateTime.UtcNow.AddMinutes(-20)}, N'l6-jan'),
                           ({s.Doc.DocumentId}, {s.Doc.SheetDefId}, {Nov}, 1, 2, 4, NULL, {DateTime.UtcNow.AddMinutes(-10)}, N'l6-nov');
                    """).ConfigureAwait(true);

                // Правка НЕМІСЯЧНОЇ колонки в листопаді (власник якої — якір): журнал якоря без року її не бачить.
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO aud.CellChange
                        (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId,
                         OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit)
                    VALUES ({DateTime.UtcNow.AddMinutes(-5)}, {Nov}, {s.Doc.DocumentId}, {s.Doc.RowIds[0]}, {s.RowKey},
                            {s.Doc.ColumnDefIds[0]}, N'1', N'l6-nov-cell', 1, N'UserEdit', 0);
                    """).ConfigureAwait(true);
            }

            using var app = CreateApp();
            using var wide = await SignInAsync(app, await AddUserAsync(s, Jan, Nov).ConfigureAwait(true)).ConfigureAwait(true);
            using var narrow = await SignInAsync(app, await AddUserAsync(s, Jan, Jan).ConfigureAwait(true)).ConfigureAwait(true);

            var history = $"/api/v1/documents/{s.Doc.DocumentId}/workflow/history?periodKey={Jan}";
            var activity = $"/api/v1/documents/{s.Doc.DocumentId}/sheets/{s.Doc.SheetDefId}/activity?periodKey={Jan}";

            // Без прапора — лише січень.
            var single = await GetJsonAsync(app, wide, history).ConfigureAwait(true);
            Assert.Equal(["l6-jan"], Reasons(single));
            var singleLog = await GetJsonAsync(app, wide, activity).ConfigureAwait(true);
            Assert.DoesNotContain("l6-nov", singleLog.GetRawText(), StringComparison.Ordinal);

            // Allow: рік — обидва місяці, найновіші перші, з періодом.
            var year = await GetJsonAsync(app, wide, history + "&year=true").ConfigureAwait(true);
            Assert.Equal(["l6-nov", "l6-jan"], Reasons(year));
            Assert.Equal([Nov, Jan], year.EnumerateArray().Select(e => e.GetProperty("periodKey").GetInt32()).ToList());

            var yearLog = await GetJsonAsync(app, wide, activity + "&year=true").ConfigureAwait(true);
            var items = yearLog.GetProperty("items").EnumerateArray().ToList();
            var cell = items.Single(i => i.GetProperty("kind").GetString() == "CellEdit");
            Assert.Equal("l6-nov-cell", cell.GetProperty("newValue").GetString());
            Assert.Equal(Nov, cell.GetProperty("periodKey").GetInt32());
            var events = items.Where(i => i.GetProperty("kind").GetString() == "Workflow").ToList();
            Assert.Equal([Nov, Jan], events.Select(e => e.GetProperty("periodKey").GetInt32()).ToList());

            // Deny: «лише січень» — нічого з листопада ні в журналі переходів, ні в журналі аркуша.
            var denied = await GetJsonAsync(app, narrow, history + "&year=true").ConfigureAwait(true);
            Assert.Equal(["l6-jan"], Reasons(denied));
            var deniedLog = await GetJsonAsync(app, narrow, activity + "&year=true").ConfigureAwait(true);
            Assert.DoesNotContain("l6-nov", deniedLog.GetRawText(), StringComparison.Ordinal);
            Assert.Contains("l6-jan", deniedLog.GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAsync(s).ConfigureAwait(true);
        }
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scenario(TestDocumentBuilder Builder, TestDocument Doc, string RowKey);

    private static List<int> Months(JsonElement items, string property)
        => [.. items.EnumerateArray().Select(i => i.GetProperty(property).GetInt32()).Order()];

    private static List<string> Rules(JsonElement result)
        => [.. result.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("ruleCode").GetString()!).Order(StringComparer.Ordinal)];

    private static List<string> Reasons(JsonElement events)
        => [.. events.EnumerateArray().Select(e => e.GetProperty("reason").GetString()!)];

    private EcrApiFactory CreateApp()
    {
        Environment.SetEnvironmentVariable(FlagVariable, "false");
        try
        {
            return new EcrApiFactory(sql);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FlagVariable, null);
        }
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

    private static async Task<JsonElement> GetJsonAsync(EcrApiFactory app, HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode}\n{text}\n{app.ErrorsText}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>
    /// Документ 209901 (якір) версії YearWithMonthColumns: «місяці в колонках», 12 місячних колонок, календар 1..12 (Open),
    /// екземпляр листопада.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            Jan, columnCount: 2, rowCount: 1, rowMode: TableRowMode.Mixed,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12).ConfigureAwait(false);

        await using (var db = builder.CreateContext())
        {
            var template = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId).ConfigureAwait(false);
            db.Entry(template).Property(v => v.Layout).CurrentValue = TemplateLayout.YearWithMonthColumns;

            db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));
            for (var m = 1; m <= 12; m++)
            {
                var key = (2099 * 100) + m;
                var period = key == Jan
                    ? await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == Jan).ConfigureAwait(false)
                    : db.Periods.Add(new Period(
                        doc.ProjectId, new PeriodKey(key), (byte)m,
                        new DateOnly(2099, m, 1), new DateOnly(2099, m, DateTime.DaysInMonth(2099, m)))).Entity;
                period.AdvanceTo(PeriodState.Open, Now);
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        await builder.AddPeriodInstanceAsync(doc, new PeriodKey(Nov)).ConfigureAwait(false);

        string rowKey;
        await using (var db = builder.CreateContext())
        {
            rowKey = await db.TableRows.AsNoTracking().Where(r => r.Id == doc.RowIds[0])
                .Select(r => r.RowKeyValue).SingleAsync().ConfigureAwait(false);
        }

        return new Scenario(builder, doc, rowKey);
    }

    /// <summary>Власні рядки сценарію (проблеми, результати перевірки, події процесу) — геть.</summary>
    private static async Task CleanupAsync(Scenario s)
    {
        await using var db = s.Builder.CreateContext();
        await db.CalculationIssues.Where(i => i.DocumentId == s.Doc.DocumentId).ExecuteDeleteAsync().ConfigureAwait(false);
        await db.ValidationResults.Where(v => v.DocumentId == s.Doc.DocumentId).ExecuteDeleteAsync().ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wf.ApprovalEvent WHERE DocumentId = {s.Doc.DocumentId}")
            .ConfigureAwait(false);
    }

    /// <summary>Користувач з роллю Document.View на проєкт і областю періодів <paramref name="from"/>..<paramref name="to"/>.</summary>
    private static async Task<string> AddUserAsync(Scenario s, int from, int to)
    {
        var userName = $"l6y_{Guid.NewGuid():N}"[..20];
        await using var db = s.Builder.CreateContext();
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"L6Y{Guid.NewGuid():N}"[..14]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "L6 year modes" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        var assignment = new RoleAssignment(role.Id, user.Id, null);
        assignment.SetScope(RoleAssignmentScope.Create(
            [s.Doc.ProjectId], periodFrom: new PeriodKey(from), periodTo: new PeriodKey(to)));
        db.RoleAssignments.Add(assignment);
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, s.Doc.ProjectId, GrantLevel.Submit));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return userName;
    }
}
