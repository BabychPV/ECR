// tests/Ecr.Api.Tests/DocumentCompareTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Documents;
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
/// ФВ-5.22: <c>GET /documents/{id}/versions</c> і <c>GET /documents/{id}/compare</c>
/// наскрізно, на справжній базі. Версія — зріз подання <c>calc.SubmissionSnapshot</c>.
/// </summary>
/// <remarks>
/// ⚠ Зріз пишеться тут напряму, у форматі <c>SubmitSheetHandler.SnapshotPayloadAsync</c>
/// (<c>[{row,column,value}]</c>, число — decimal в інваріантній культурі): подання через
/// HTTP потребує маршруту й валідації, які до порівняння не мають стосунку.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentCompareTests(SqlServerFixture sql)
{
    private const string Password = "Api-Doc-Compare-2026!";

    // 28 значущих цифр: double розрізняє лише ~16, тож ці два числа для нього рівні.
    private const string Before = "123456789012.0000000000000001";
    private const string After = "123456789012.0000000000000002";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Змінене_значення_доданий_і_видалений_рядок_видно_проти_поточного_стану()
    {
        var s = await ArrangeAsync(GrantLevel.Read).ConfigureAwait(true);
        var d = s.Document;

        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            await SetAsync(db, d, d.RowIds[0], 0, numeric: Before).ConfigureAwait(true);
            await SetAsync(db, d, d.RowIds[0], 1, numeric: "1.5").ConfigureAwait(true);
            await SetAsync(db, d, d.RowIds[1], 0, text: "keep").ConfigureAwait(true);
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var v1 = await SnapshotCurrentAsync(s).ConfigureAwait(true);

        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            await SetAsync(db, d, d.RowIds[0], 0, numeric: After).ConfigureAwait(true);
            await SetAsync(db, d, d.RowIds[0], 1, numeric: "1.50").ConfigureAwait(true); // та сама величина
            await SetAsync(db, d, d.RowIds[2], 0, text: "new").ConfigureAwait(true);
            var removed = await db.TableRows.SingleAsync(r => r.Id == d.RowIds[1]).ConfigureAwait(true);
            removed.SoftDelete(DateTime.UtcNow);
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var diff = await CompareAsync(client, app, d.DocumentId, v1, "current").ConfigureAwait(true);

        var change = Assert.Single(diff.GetProperty("changes").EnumerateArray());
        Assert.Equal(Before, change.GetProperty("oldValue").GetString());
        Assert.Equal(After, change.GetProperty("newValue").GetString());
        Assert.False(string.IsNullOrEmpty(change.GetProperty("tableCode").GetString()));

        var added = Assert.Single(diff.GetProperty("addedRows").EnumerateArray());
        Assert.Equal(d.RowIds[2], added.GetProperty("rowId").GetInt64());

        // Видалений рядок підписаний, хоч його вже немає в сітці.
        var gone = Assert.Single(diff.GetProperty("removedRows").EnumerateArray());
        Assert.Equal(d.RowIds[1], gone.GetProperty("rowId").GetInt64());
        Assert.False(gone.GetProperty("rowKey").GetString()!.StartsWith('#'));

        Assert.False(diff.GetProperty("truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, diff.GetProperty("toVersionId").ValueKind);

        // Перелік версій знає цей зріз.
        var versions = await client.GetFromJsonAsync<JsonElement>(new Uri(
            $"/api/v1/documents/{d.DocumentId}/versions?periodKey={d.PeriodKey.Value}", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(v1, Assert.Single(versions.EnumerateArray()).GetProperty("versionId").GetInt64());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_змін_порожня_різниця_і_між_версіями_і_з_поточним()
    {
        var s = await ArrangeAsync(GrantLevel.Read).ConfigureAwait(true);
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            await SetAsync(db, s.Document, s.Document.RowIds[0], 0, numeric: Before).ConfigureAwait(true);
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var v1 = await SnapshotCurrentAsync(s).ConfigureAwait(true);
        var v2 = await SnapshotCurrentAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        foreach (var to in new[] { "current", v2.ToString(CultureInfo.InvariantCulture) })
        {
            var diff = await CompareAsync(client, app, s.Document.DocumentId, v1, to).ConfigureAwait(true);
            Assert.Empty(diff.GetProperty("changes").EnumerateArray());
            Assert.Empty(diff.GetProperty("addedRows").EnumerateArray());
            Assert.Empty(diff.GetProperty("removedRows").EnumerateArray());
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Понад_1000_змін_обрізається_до_1000_з_truncated()
    {
        var s = await ArrangeAsync(GrantLevel.Read).ConfigureAwait(true);
        var row = s.Document.RowIds[0];

        var v1 = await SnapshotAsync(s, Payload(row, "1")).ConfigureAwait(true);
        var v2 = await SnapshotAsync(s, Payload(row, "2")).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var diff = await CompareAsync(client, app, s.Document.DocumentId, v1, v2.ToString(CultureInfo.InvariantCulture))
            .ConfigureAwait(true);

        Assert.Equal(1000, diff.GetProperty("changes").GetArrayLength());
        Assert.True(diff.GetProperty("truncated").GetBoolean());

        static string Payload(long rowId, string value) => JsonSerializer.Serialize(
            Enumerable.Range(1, 1001).Select(c => new { row = rowId, column = c, value }));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_гранта_на_проєкт_404_як_неіснуючий()
    {
        var s = await ArrangeAsync(grant: null).ConfigureAwait(true);
        var v1 = await SnapshotCurrentAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var id = s.Document.DocumentId;
        foreach (var url in new[]
                 {
                     $"/api/v1/documents/{id}/compare?from={v1}&to=current",
                     $"/api/v1/documents/{id}/versions?periodKey={s.Document.PeriodKey.Value}",
                 })
        {
            var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
            Assert.Equal("err.ECR-DOC-0404.document", problem.GetProperty("messageKey").GetString());
        }
    }

    // ────────────────────────────── збірка ────────────────────────────

    private static async Task<JsonElement> CompareAsync(HttpClient client, EcrApiFactory app, long documentId, long from, string to)
    {
        var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{documentId}/compare?from={from}&to={to}", UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task SetAsync(
        Ecr.Infrastructure.Persistence.EcrDbContext db, TestDocument d, long rowId, int column,
        string? numeric = null, string? text = null)
    {
        var columnId = d.ColumnDefIds[column];
        var data = new CellValueData
        {
            ValueNumeric = numeric is null ? null : decimal.Parse(numeric, CultureInfo.InvariantCulture),
            ValueString = text,
        };

        var cell = await db.CellValues
            .SingleOrDefaultAsync(c => c.PeriodKeyValue == d.PeriodKey.Value && c.TableRowId == rowId && c.ColumnDefId == columnId)
            .ConfigureAwait(false);
        if (cell is null)
        {
            db.CellValues.Add(new CellValue(new CellAddress(d.PeriodKey, rowId, columnId), d.TableDefId, data));
        }
        else
        {
            cell.Apply(data);
        }
    }

    /// <summary>Зріз поточних комірок — рядком того самого формату, що пише подання.</summary>
    private async Task<long> SnapshotCurrentAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var rows = await db.TableRows.Where(r => r.TableInstanceId == s.Document.TableInstanceId && !r.IsDeleted)
            .Select(r => r.Id).ToListAsync().ConfigureAwait(false);
        var cells = await db.CellValues.Where(c => rows.Contains(c.TableRowId)).ToListAsync().ConfigureAwait(false);

        return await SnapshotAsync(s, JsonSerializer.Serialize(cells
            .OrderBy(c => c.TableRowId).ThenBy(c => c.ColumnDefId)
            .Select(c => new
            {
                row = c.TableRowId,
                column = c.ColumnDefId,
                value = c.ValueNumeric?.ToString(CultureInfo.InvariantCulture) ?? c.ValueString,
            }))).ConfigureAwait(false);
    }

    private async Task<long> SnapshotAsync(Scenario s, string payload)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var snapshot = new SubmissionSnapshot(
            s.Document.DocumentId, s.Document.SheetDefId, s.Document.PeriodKey.Value, s.Document.TemplateVersionId,
            "[]", null, null, payload, new byte[32], DateTime.UtcNow, s.UserId);
        db.SubmissionSnapshots.Add(snapshot);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return snapshot.Id;
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

    /// <summary>Документ на три рядки й дві колонки та користувач із <c>Document.View</c>.</summary>
    private async Task<Scenario> ArrangeAsync(GrantLevel? grant)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 2, rowCount: 3).ConfigureAwait(false);

        await using var db = builder.CreateContext();
        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, document.SheetDefId));

        var userName = $"doccmp_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"DOCCMP_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Doc compare" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, CompareDocumentVersionsHandler.Permission));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        if (grant is { } level)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, level));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        return new Scenario(document, userName, user.Id);
    }

    private sealed record Scenario(TestDocument Document, string UserName, int UserId);
}
