// tests/Ecr.Api.Tests/SubmissionTargetApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
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
/// D-441 (AN-128), наскрізно через HTTP на справжній базі: у лютому з неподаним січнем «Подати» подає
/// січень (<c>GET …/submission-target</c>), колонка «Січень» таблиці <c>MonthsInColumns</c> позначається
/// станом аркуша за січень, погоджувач бачить січень у черзі, а <c>Scheduled</c>-період подати не можна.
/// </summary>
/// <remarks>
/// ⛔ На старому коді: маршруту <c>submission-target</c> немає (404), а подання березня (<c>Scheduled</c>)
/// проходило <c>204</c> — <c>EditRules.CanSubmit</c> відмовляв лише для <c>Closed</c>.
/// ⚠ Проєкт будівника — чернетка: рішення бере ЗБЕРЕЖЕНІ стани періодів (Jan Grace, Feb Open, Mar Scheduled),
/// не дати, тож тест не залежить від годинника.
/// </remarks>
[Collection("SqlServer")]
public sealed class SubmissionTargetApiTests(SqlServerFixture sql)
{
    private const string Password = "Submission-Target-D441!";
    private const int Jan = 202601;
    private const int Feb = 202602;
    private const int Mar = 202603;

    private static readonly DateTime Now = new(2026, 2, 10, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-441")]
    public async Task У_лютому_з_неподаним_січнем_подається_січень_колонка_позначена_погоджувач_бачить_січень()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var submitter = await NewUserAsync(s.Doc, GrantLevel.Submit).ConfigureAwait(true);
        var approver = await NewUserAsync(s.Doc, GrantLevel.Approve).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var submitterClient = await SignInAsync(app, submitter).ConfigureAwait(true);
        using var approverClient = await SignInAsync(app, approver).ConfigureAwait(true);

        // Відкрито лютий: ціль — січень (Grace), наступний — лютий; березень (Scheduled) не пропонується.
        var before = await TargetAsync(app, submitterClient, s.Doc, Feb).ConfigureAwait(true);
        Assert.Equal(Jan, before.GetProperty("targetPeriodKey").GetInt32());
        Assert.Equal(Feb, before.GetProperty("nextPeriodKey").GetInt32());
        Assert.Equal(
            [(Jan, "Grace", "Draft", true), (Feb, "Open", "Draft", true)],
            before.GetProperty("periods").EnumerateArray().Select(p => (
                p.GetProperty("periodKey").GetInt32(),
                p.GetProperty("state").GetString(),
                p.GetProperty("sheetStatus").GetString(),
                p.GetProperty("submittable").GetBoolean())));

        // «Подати» шле саме ціль.
        using (var submitted = await SubmitAsync(submitterClient, s.Doc, Jan).ConfigureAwait(true))
        {
            Assert.True(
                submitted.StatusCode == HttpStatusCode.NoContent,
                $"{(int)submitted.StatusCode}\n{await submitted.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");
        }

        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(s.Doc, Jan).ConfigureAwait(true));
        Assert.Null(await StatusAsync(s.Doc, Feb).ConfigureAwait(true));

        // Після подання січня ціль — лютий; колонка «Січень» несе стан аркуша за січень.
        var after = await TargetAsync(app, submitterClient, s.Doc, Feb).ConfigureAwait(true);
        Assert.Equal(Feb, after.GetProperty("targetPeriodKey").GetInt32());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("nextPeriodKey").ValueKind);

        var months = after.GetProperty("monthColumns").EnumerateArray()
            .ToDictionary(m => m.GetProperty("columnCode").GetString()!, m => m);
        Assert.Equal(["FEB", "JAN", "MAR"], months.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(Jan, months["JAN"].GetProperty("periodKey").GetInt32());
        Assert.Equal("Submitted", months["JAN"].GetProperty("sheetStatus").GetString());
        Assert.Equal(s.MonthTableId, months["JAN"].GetProperty("tableDefId").GetInt32());
        Assert.Equal("Draft", months["FEB"].GetProperty("sheetStatus").GetString());

        // Погоджувач бачить СІЧЕНЬ у своїй черзі.
        var queue = await GetJsonAsync(app, approverClient, "/api/v1/me/approval-queue").ConfigureAwait(true);
        var item = Assert.Single(
            queue.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("documentId").GetInt64() == s.Doc.DocumentId);
        Assert.Equal(Jan, item.GetProperty("periodKey").GetInt32());
        Assert.Equal(s.Doc.SheetDefId, item.GetProperty("sheetDefId").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-441")]
    public async Task Подання_Scheduled_періоду_403_PeriodNotOpenYet_ДО_було_204()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var submitter = await NewUserAsync(s.Doc, GrantLevel.Submit).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, submitter).ConfigureAwait(true);

        using var response = await SubmitAsync(client, s.Doc, Mar).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{(int)response.StatusCode}\n{body}\n{app.ErrorsText}");
        using var json = JsonDocument.Parse(body);
        Assert.Equal("ECR-ACCS-0403", json.RootElement.GetProperty("errorCode").GetString());
        Assert.Contains("PeriodNotOpenYet", body, StringComparison.Ordinal);
        Assert.Contains("deny.PeriodNotOpenYet.Submit", body, StringComparison.Ordinal);
        Assert.Null(await StatusAsync(s.Doc, Mar).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-441")]
    public async Task Невидимий_документ_404_як_неіснуючий()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var stranger = await NewUserAsync(s.Doc, level: null).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, stranger).ConfigureAwait(true);

        using var response = await client.GetAsync(Uri(s.Doc, Feb)).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{(int)response.StatusCode}\n{body}");
        Assert.DoesNotContain("targetPeriodKey", body, StringComparison.Ordinal);
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scenario(TestDocument Doc, int MonthTableId);

    private static Uri Uri(TestDocument doc, int periodKey)
        => new($"/api/v1/documents/{doc.DocumentId}/submission-target?sheetDefId={doc.SheetDefId}&periodKey={periodKey}", UriKind.Relative);

    private static async Task<JsonElement> TargetAsync(EcrApiFactory app, HttpClient client, TestDocument doc, int periodKey)
        => await GetJsonAsync(app, client, Uri(doc, periodKey).OriginalString).ConfigureAwait(false);

    private static async Task<JsonElement> GetJsonAsync(EcrApiFactory app, HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {(int)response.StatusCode}\n{body}\n{app.ErrorsText}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, TestDocument doc, int periodKey)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/submit", UriKind.Relative),
            new { sheetDefId = doc.SheetDefId, periodKey });

    /// <summary>
    /// Документ у лютому 2026 (Open) з неподаним січнем (Grace) і березнем (Scheduled); на аркуші — таблиця
    /// <c>MonthsInColumns</c> з колонками JAN/FEB/MAR (місяці 1–3) і немісячною TOTAL.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: Feb).ConfigureAwait(false);

        await using var db = Context();
        db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));

        var feb = await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == Feb).ConfigureAwait(false);
        feb.AdvanceTo(PeriodState.Open, Now);

        var jan = new Period(doc.ProjectId, new PeriodKey(Jan), 1, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        jan.AdvanceTo(PeriodState.Grace, Now);
        db.Periods.Add(jan);
        db.Periods.Add(new Period(doc.ProjectId, new PeriodKey(Mar), 3, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)));

        var table = new TableDef(
            doc.SheetDefId, EcrCode.Create($"T7M{Guid.NewGuid():N}"[..12]), Name("Water consolidation"), 2,
            TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync().ConfigureAwait(false);

        byte month = 0;
        foreach (var code in new[] { "JAN", "FEB", "MAR" })
        {
            var column = new ColumnDef(table.Id, EcrCode.Create(code), Name(code), ++month, CellDataType.Decimal);
            column.SetMonth(true, month);
            db.ColumnDefs.Add(column);
        }

        db.ColumnDefs.Add(new ColumnDef(table.Id, EcrCode.Create("TOTAL"), Name("Total"), 13, CellDataType.Decimal));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(doc, table.Id);
    }

    private async Task<DocumentStatus?> StatusAsync(TestDocument doc, int periodKey)
    {
        await using var db = Context();

        return await db.ApprovalStates.AsNoTracking()
            .Where(a => a.DocumentId == doc.DocumentId && a.SheetDefId == doc.SheetDefId && a.PeriodKey == periodKey)
            .Select(a => (DocumentStatus?)a.Status)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
    }

    /// <summary>Користувач з роллю: грант на проєкт документа заданого рівня (<c>null</c> — без гранта) і Document.View.</summary>
    private async Task<string> NewUserAsync(TestDocument doc, GrantLevel? level)
    {
        var name = $"d441_{Guid.NewGuid():N}"[..20];

        await using var db = Context();
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("D-441 submission target"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
        if (level is { } granted)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, granted));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return name;
    }

    private static async Task<HttpClient> SignInAsync(EcrApiFactory app, string name)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
