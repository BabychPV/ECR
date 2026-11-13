// tests/Ecr.Api.Tests/ArchivedProjectCellWriteApiTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
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
/// B69 (P1, слово людини: «заархівовані дані не мають змінюватися»): запис комірок архівованого проєкту справжнім HTTP —
/// <c>PATCH …/cells</c> і імпорт Excel — відмовляє з причиною <c>ProjectArchived</c>, і в базі нічого не змінюється.
/// </summary>
/// <remarks>
/// ⛔ Доти рішення <c>EditRules</c> (<c>ProjectArchived</c>/<c>ArchivingInProgress</c>) мало лише сценарій нового
/// рядка (<c>DataEntryScenarios</c>, S-17); запис комірок і імпорт на архівованому проєкті HTTP-тестом не покривались (G6).
/// <para>
/// ⚠ Період архівованого проєкту закритий, і закритий період сам по собі теж відмовив би (<c>PeriodClosed</c>). Тому
/// доказ — саме <c>reason</c>/<c>deny.ProjectArchived</c>: прибрати перевірку архіву в <c>EditRules</c> — причина стає
/// <c>PeriodClosed</c>, тест червоний. Контрольний запис ДО архівації доводить, що підготовка взагалі дозволяє писати.
/// </para>
/// <para>
/// ⚠ Проєкт архівується прямо в базі (як у <c>ArchivedProjectRulesTests</c>): стан проєкту рішення про запис читає
/// з бази на кожен запит (<c>AccessDecisionService</c>), кешу між запитами немає.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class ArchivedProjectCellWriteApiTests(SqlServerFixture sql)
{
    private const string Password = "Archived-Cell-Write-B69!";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();
    private static readonly JsonSerializerOptions MapJson = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B69-G6")]
    public async Task PATCH_комірок_архівованого_проєкту_відмовляє_ProjectArchived_і_нічого_не_пише()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        // Контроль: до архівації той самий запис проходить.
        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(client, s, 1m)).ConfigureAwait(true);
        Assert.Equal(1m, (await StoredAsync(s).ConfigureAwait(true))[0]);

        await ArchiveAsync(s).ConfigureAwait(true);

        var body = await ExpectAsync(app, HttpStatusCode.Forbidden, PatchAsync(client, s, 2m)).ConfigureAwait(true);
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-ACCS-0403", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-ACCS-0403.deniedCells", problem.GetProperty("messageKey").GetString());
        Assert.Equal(nameof(EditDenyReason.ProjectArchived), problem.GetProperty("reason").GetString());

        Assert.Equal(1m, (await StoredAsync(s).ConfigureAwait(true))[0]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B69-G6")]
    public async Task Імпорт_Excel_в_архівований_проєкт_відхиляється_ProjectArchived_і_нічого_не_пише()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        await ArchiveAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        // Перегляд показує кожну комірку книги відмовою саме «проєкт архівовано», не «період закрито».
        var (token, preview) = await PreviewAsync(app, client, s, Book(s, (s.RowKeys[0], 11.5m), (s.RowKeys[1], 22.5m)))
            .ConfigureAwait(true);
        var root = JsonDocument.Parse(preview).RootElement;
        var rejected = root.GetProperty("rejected").EnumerateArray()
            .Select(r => r.TryGetProperty("messageKey", out var key) ? key.GetString() : null)
            .ToList();
        Assert.Equal(2, rejected.Count);
        Assert.All(rejected, key => Assert.Equal(ImportMessageKeys.Denied(EditDenyReason.ProjectArchived), key));
        Assert.Equal(0, root.GetProperty("changes").GetArrayLength());

        // Застосування — «все або нічого» (AN-143): відхилений перегляд не пише жодної комірки.
        using (var applied = await client.PostAsJsonAsync(
                   new Uri($"/api/v1/documents/{s.Document.DocumentId}/import/apply", UriKind.Relative),
                   new { previewToken = token }).ConfigureAwait(true))
        {
            var body = await applied.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(
                applied.StatusCode == HttpStatusCode.UnprocessableEntity,
                $"apply: {(int)applied.StatusCode}\n{body}\n{app.ErrorsText}");
            Assert.Contains(ExcelImporter.ImportRejectedMessageKey, body, StringComparison.Ordinal);
        }

        Assert.Equal([null, null], await StoredAsync(s).ConfigureAwait(true));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scenario(
        TestDocumentBuilder Builder,
        TestDocument Document,
        string UserName,
        IReadOnlyList<string> RowKeys,
        string ColumnCode,
        int PeriodKey);

    private static async Task<string> ExpectAsync(EcrApiFactory app, HttpStatusCode expected, Task<HttpResponseMessage> call)
    {
        using var response = await call.ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(
            response.StatusCode == expected,
            $"{(int)response.StatusCode} замість {(int)expected}\n{body}\n{app.ErrorsText}");

        return body;
    }

    /// <summary>Правка числової колонки першого рядка з актуальною версією рядка.</summary>
    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, Scenario s, decimal value)
    {
        using var table = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Document.DocumentId.ToString(CultureInfo.InvariantCulture)}/tables/{s.Document.TableInstanceId.ToString(CultureInfo.InvariantCulture)}",
            UriKind.Relative)).ConfigureAwait(false);
        var text = await table.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(table.IsSuccessStatusCode, $"GET зрізу: {table.StatusCode}\n{text}");

        var row = JsonDocument.Parse(text).RootElement.GetProperty("rows").EnumerateArray()
            .First(r => r.GetProperty("rowKey").GetString() == s.RowKeys[0]);

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
                        rowKey = s.RowKeys[0],
                        baseVersion = row.GetProperty("rowVersion").GetString(),
                        cells = new object[] { new { columnCode = s.ColumnCode, value = (object)value.ToString(CultureInfo.InvariantCulture) } },
                    },
                },
            }).ConfigureAwait(false);
    }

    private static async Task<(string Token, string Body)> PreviewAsync(EcrApiFactory app, HttpClient client, Scenario s, byte[] book)
    {
        using var content = new MultipartFormDataContent();
        using var file = new ByteArrayContent(book);
        file.Headers.ContentType = new MediaTypeHeaderValue(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(file, "file", "book.xlsx");

        using var response = await client.PostAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/import/preview", UriKind.Relative), content)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"перегляд: {response.StatusCode}\n{body}\n{app.ErrorsText}");

        var token = JsonDocument.Parse(body).RootElement.GetProperty("previewToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token), $"перегляд без токена: {body}");

        return (token!, body);
    }

    /// <summary>Книга з картою <c>_ecr</c>: аркуш <c>MAIN</c>, значення числової колонки за ключами рядків.</summary>
    private static byte[] Book(Scenario s, params (string RowKey, decimal Value)[] rows)
    {
        using var workbook = new XLWorkbook();
        var main = workbook.Worksheets.Add("MAIN");

        var rowRefs = new List<ExcelRowRef>();
        for (var i = 0; i < rows.Length; i++)
        {
            var number = i + 2;
            main.Cell(number, 1).Value = rows[i].Value;
            rowRefs.Add(new ExcelRowRef(rows[i].RowKey, number));
        }

        var block = new ExcelTableBlock(
            s.Document.TableInstanceId, s.Document.TableDefId, "MAIN_FILE", "MAIN", HeaderRow: 1,
            Columns: [new ExcelColumnRef(s.Document.ColumnDefIds[1], s.ColumnCode, 1, false, null)],
            Rows: rowRefs);

        var map = new ExcelWorkbookMap(
            s.Document.DocumentId, s.PeriodKey, s.Document.TemplateVersionId, [block]);
        workbook.Worksheets.Add(ExcelWorkbookMap.SheetName).Cell(1, 1).Value = JsonSerializer.Serialize(map, MapJson);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>Збережені значення числової колонки рядків документа в порядку <c>RowKeys</c>.</summary>
    private static async Task<List<decimal?>> StoredAsync(Scenario s)
    {
        await using var db = s.Builder.CreateContext();
        var column = s.Document.ColumnDefIds[1];

        var result = new List<decimal?>();
        for (var i = 0; i < s.RowKeys.Count; i++)
        {
            var rowId = s.Document.RowIds[i];
            result.Add(await db.CellValues.AsNoTracking()
                .Where(c => c.PeriodKeyValue == s.PeriodKey && c.TableRowId == rowId && c.ColumnDefId == column)
                .Select(c => c.ValueNumeric)
                .SingleOrDefaultAsync()
                .ConfigureAwait(false));
        }

        return result;
    }

    /// <summary>Шлях архівації (D-123): усі періоди закриті → <c>Project.Archive</c>.</summary>
    private static async Task ArchiveAsync(Scenario s)
    {
        await using var db = s.Builder.CreateContext();
        var now = DateTime.UtcNow;

        var project = await db.Projects.Include(p => p.Periods)
            .FirstAsync(p => p.Id == s.Document.ProjectId).ConfigureAwait(false);
        foreach (var period in project.Periods)
        {
            period.AdvanceTo(PeriodState.Closed, now);
        }

        project.Archive(now);
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
    /// Документ у відкритому поточному періоді активного проєкту (як у <c>ImportPreviewTokenApiTests</c>): власна роль
    /// тесту з <c>Document.View</c> + <c>Document.Import</c> і грантом <c>Write</c> на проєкт.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SiteZone));
        var periodKey = (siteToday.Year * 100) + siteToday.Month;

        var document = await builder
            .BuildAsync(periodKey, columnCount: 3, rowCount: 2)
            .ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var userName = $"b69_{Guid.NewGuid():N}"[..20];

        await using var db = builder.CreateContext();
        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, document.SheetDefId));

        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"B69{Guid.NewGuid():N}"[..14]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Archived project cell write" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RolePermissions.Add(new RolePermission(role.Id, "Document.Import"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Write));

        var project = await db.Projects.FirstAsync(p => p.Id == document.ProjectId).ConfigureAwait(false);
        project.Activate(now);

        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var period = await db.Periods
            .FirstAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey)
            .ConfigureAwait(false);
        period.RecomputeBoundaries(policy, SiteZone);
        period.AdvanceTo(PeriodState.Open, now);

        await db.SaveChangesAsync().ConfigureAwait(false);

        var columnCode = await db.ColumnDefs.AsNoTracking()
            .Where(c => c.Id == document.ColumnDefIds[1])
            .Select(c => c.Code)
            .SingleAsync().ConfigureAwait(false);

        var rowKeys = new List<string>();
        foreach (var rowId in document.RowIds)
        {
            rowKeys.Add(await db.TableRows.AsNoTracking()
                .Where(r => r.Id == rowId)
                .Select(r => r.RowKeyValue)
                .SingleAsync().ConfigureAwait(false));
        }

        return new Scenario(builder, document, userName, rowKeys, columnCode, periodKey);
    }
}
