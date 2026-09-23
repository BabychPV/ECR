// tests/Ecr.Api.Tests/DocumentCompareTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Workflow;
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

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Змінені_дата_й_булеве_видно_проти_поточного_стану_з_типом()
    {
        var s = await ArrangeAsync(GrantLevel.Read).ConfigureAwait(true);
        var d = s.Document;

        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            await SetAsync(db, d, d.RowIds[0], 0, date: new DateTime(2026, 1, 2)).ConfigureAwait(true);
            await SetAsync(db, d, d.RowIds[0], 1, flag: false).ConfigureAwait(true);
            await SetAsync(db, d, d.RowIds[1], 0, date: new DateTime(2026, 5, 5)).ConfigureAwait(true); // не змінюється
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var v1 = await SnapshotCurrentAsync(s).ConfigureAwait(true);

        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            await SetAsync(db, d, d.RowIds[0], 0, date: new DateTime(2026, 1, 3)).ConfigureAwait(true);
            await SetAsync(db, d, d.RowIds[0], 1, flag: true).ConfigureAwait(true);
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var changes = (await CompareAsync(client, app, d.DocumentId, v1, "current").ConfigureAwait(true))
            .GetProperty("changes").EnumerateArray().ToList();

        Assert.Equal(2, changes.Count);
        var date = changes.Single(c => c.GetProperty("newType").GetString() == SubmissionPayload.Date);
        Assert.Equal(SubmissionPayload.Date, date.GetProperty("oldType").GetString());
        Assert.StartsWith("2026-01-02", date.GetProperty("oldValue").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("2026-01-03", date.GetProperty("newValue").GetString(), StringComparison.Ordinal);

        var flag = changes.Single(c => c.GetProperty("newType").GetString() == SubmissionPayload.Bool);
        Assert.Equal("false", flag.GetProperty("oldValue").GetString());
        Assert.Equal("true", flag.GetProperty("newValue").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Між_версіями_тип_входить_у_порівняння_а_число_як_decimal()
    {
        var s = await ArrangeAsync(GrantLevel.Read).ConfigureAwait(true);
        var row = s.Document.RowIds[0];

        var v1 = await SnapshotAsync(s, Payload(
            new(row, 1, "true", null),                                  // текст
            new(row, 2, "1.5", null),
            new(row, 3, "5", SubmissionPayload.RegistryEntry),
            new(row, 4, "2026-01-02T00:00:00", SubmissionPayload.Date),
            new(row, 5, "7", SubmissionPayload.Unit))).ConfigureAwait(true);
        var v2 = await SnapshotAsync(s, Payload(
            new(row, 1, "true", SubmissionPayload.Bool),                // те саме значення, інший тип
            new(row, 2, "1.50", null),                                  // та сама величина
            new(row, 3, "6", SubmissionPayload.RegistryEntry),          // інший запис довідника
            new(row, 4, "2026-01-02T00:00:00.0000000", SubmissionPayload.Date), // та сама дата
            new(row, 5, "7", SubmissionPayload.Unit))).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var changes = (await CompareAsync(client, app, s.Document.DocumentId, v1, v2.ToString(CultureInfo.InvariantCulture))
                .ConfigureAwait(true))
            .GetProperty("changes").EnumerateArray().ToList();

        // Упорядковано за колонкою: 1 (текст → bool) і 3 (інший запис довідника); 2, 4, 5 — без змін.
        Assert.Equal(2, changes.Count);

        var typed = changes[0];
        Assert.Equal("true", typed.GetProperty("oldValue").GetString());
        Assert.Equal(JsonValueKind.Null, typed.GetProperty("oldType").ValueKind);
        Assert.Equal(SubmissionPayload.Bool, typed.GetProperty("newType").GetString());

        Assert.Equal("5", changes[1].GetProperty("oldValue").GetString());
        Assert.Equal("6", changes[1].GetProperty("newValue").GetString());
        Assert.Equal(SubmissionPayload.RegistryEntry, changes[1].GetProperty("newType").GetString());

        static string Payload(params SubmissionPayloadCell[] cells) => JsonSerializer.Serialize(cells);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Змінене_поле_шапки_видно_проти_поточного_стану()
    {
        var s = await ArrangeAsync(GrantLevel.Read).ConfigureAwait(true);
        var d = s.Document;

        int areaFieldId;
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var area = new HeaderFieldDef(
                d.TemplateVersionId, EcrCode.Create("AREA"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Area" }), 0, CellDataType.String);
            db.HeaderFieldDefs.Add(area);
            await db.SaveChangesAsync().ConfigureAwait(true);
            areaFieldId = area.Id;
        }

        var v1 = await SnapshotAsync(s, PayloadWithHeader(d.RowIds[0], ("AREA", "Kashagan"))).ConfigureAwait(true);

        // Живе значення шапки — інше за те, що збережено в зрізі v1.
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            db.DocumentHeaderValues.Add(new DocumentHeaderValue(
                d.DocumentId, areaFieldId, new DocumentHeaderValueData { ValueString = "Tengiz" }));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var diff = await CompareAsync(client, app, d.DocumentId, v1, "current").ConfigureAwait(true);

        var change = Assert.Single(diff.GetProperty("headerChanges").EnumerateArray());
        Assert.Equal("AREA", change.GetProperty("code").GetString());
        Assert.Equal("Kashagan", change.GetProperty("oldValue").GetString());
        Assert.Equal("Tengiz", change.GetProperty("newValue").GetString());
        Assert.Equal(JsonValueKind.Null, change.GetProperty("oldType").ValueKind);
        Assert.Equal(JsonValueKind.Null, change.GetProperty("newType").ValueKind);

        // Клітинки не чіпали — зміна шапки не потрапляє в changes.
        Assert.Empty(diff.GetProperty("changes").EnumerateArray());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Поле_шапки_присутнє_лише_в_одному_стані_теж_зміна()
    {
        var s = await ArrangeAsync(GrantLevel.Read).ConfigureAwait(true);
        var d = s.Document;

        // AREA — присутнє лише в v1 (стерто до порожнього в v2); COUNT — з'явилося лише в v2.
        var v1 = await SnapshotAsync(s, PayloadWithHeader(d.RowIds[0], ("AREA", "Kashagan"))).ConfigureAwait(true);
        var v2 = await SnapshotAsync(s, PayloadWithHeader(d.RowIds[0], ("COUNT", "5"))).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var changes = (await CompareAsync(client, app, d.DocumentId, v1, v2.ToString(CultureInfo.InvariantCulture))
                .ConfigureAwait(true))
            .GetProperty("headerChanges").EnumerateArray()
            .OrderBy(c => c.GetProperty("code").GetString(), StringComparer.Ordinal)
            .ToList();

        Assert.Equal(2, changes.Count);

        Assert.Equal("AREA", changes[0].GetProperty("code").GetString());
        Assert.Equal("Kashagan", changes[0].GetProperty("oldValue").GetString());
        Assert.Equal(JsonValueKind.Null, changes[0].GetProperty("newValue").ValueKind);

        Assert.Equal("COUNT", changes[1].GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, changes[1].GetProperty("oldValue").ValueKind);
        Assert.Equal("5", changes[1].GetProperty("newValue").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_змін_шапки_headerChanges_порожній()
    {
        var s = await ArrangeAsync(GrantLevel.Read).ConfigureAwait(true);
        var d = s.Document;

        var v1 = await SnapshotAsync(s, PayloadWithHeader(d.RowIds[0], ("AREA", "Kashagan"))).ConfigureAwait(true);
        var v2 = await SnapshotAsync(s, PayloadWithHeader(d.RowIds[0], ("AREA", "Kashagan"))).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var diff = await CompareAsync(client, app, d.DocumentId, v1, v2.ToString(CultureInfo.InvariantCulture))
            .ConfigureAwait(true);

        Assert.Empty(diff.GetProperty("headerChanges").EnumerateArray());
    }

    /// <summary>Зріз [{cells...}] + секція <c>header</c> — те саме, що пише <see cref="SubmissionPayload.Write"/>,
    /// але зібране руками (рядком, без type — «текст або число», той самий формат, що клітинки).</summary>
    private static string PayloadWithHeader(long rowId, params (string Code, string Value)[] header)
        => JsonSerializer.Serialize(new
        {
            cells = new[] { new { row = rowId, column = 1, value = "x" } },
            header = header.ToDictionary(h => h.Code, h => new { value = h.Value }, StringComparer.Ordinal),
        });

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
        string? numeric = null, string? text = null, DateTime? date = null, bool? flag = null)
    {
        var columnId = d.ColumnDefIds[column];
        var data = new CellValueData
        {
            ValueNumeric = numeric is null ? null : decimal.Parse(numeric, CultureInfo.InvariantCulture),
            ValueString = text,
            ValueDate = date,
            ValueBool = flag,
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

        return await SnapshotAsync(s, SubmissionPayload.Write(cells.Select(c => new CellRecord(
            new CellAddress(s.Document.PeriodKey, c.TableRowId, c.ColumnDefId),
            c.TableDefId,
            new CellValueData
            {
                ValueNumeric = c.ValueNumeric,
                ValueString = c.ValueString,
                ValueDate = c.ValueDate,
                ValueBool = c.ValueBool,
                ValueRegistryEntryId = c.ValueRegistryEntryId,
                ValueUnitId = c.ValueUnitId,
            })))).ConfigureAwait(false);
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
