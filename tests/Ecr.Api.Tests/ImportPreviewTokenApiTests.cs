// tests/Ecr.Api.Tests/ImportPreviewTokenApiTests.cs
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
/// ФВ-4.3 («імпорт завжди через перегляд diff») через HTTP: <c>apply</c> діє лише з токеном свого
/// перегляду, один раз; рядок знаходиться за <c>RowKey</c> з карти книги, а не за позицією.
/// </summary>
/// <remarks>
/// ⛔ До цього файлу HTTP-негатив «apply з чужим токеном» був лише на рівні імпортера
/// (<c>ExcelImporterFileRejectionTests</c>, без шару прав і без БД), а трейти ФВ-4.3 стояли на
/// тестах, що склеюють книгу в ClosedXML (<c>WorkbookMapChunkTests</c>) і кешують diff
/// (<c>ImportPreviewStoreTests</c>). Тут — справжній стек: перегляд → токен → <c>apply</c> → база.
///
/// Мутаційні докази (detached-worktree, не комітяться): (а) у <c>ExcelImporter.LoadPlanAsync</c>
/// прибрати перевірку <c>plan.UserId is { } owner &amp;&amp; owner != currentUser.UserId</c> — червоніє
/// «чужий токен»; (б) прибрати <c>previews.RemoveAsync(previewToken)</c> після застосування —
/// червоніє «токен діє один раз»; (в) у розборі книги брати рядок за номером у книзі замість
/// <c>RowKey</c> карти — червоніє «іншій позиції».
/// </remarks>
[Collection("SqlServer")]
public sealed class ImportPreviewTokenApiTests(SqlServerFixture sql)
{
    private const string Password = "Import-Token-D4-3!";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();
    private static readonly JsonSerializerOptions MapJson = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-4.3")]
    public async Task Застосування_без_перегляду_відмовляє_422_а_дані_не_змінюються()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, s.Users[0]).ConfigureAwait(true);

        // Токен, якого ніхто не будував (правдоподібної форми), і порожній — обидва «перегляду немає».
        foreach (var token in new[] { Guid.NewGuid().ToString("N"), string.Empty })
        {
            using var response = await ApplyAsync(client, s, token).ConfigureAwait(true);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

            Assert.True(
                response.StatusCode == HttpStatusCode.UnprocessableEntity,
                $"apply «{token}»: {(int)response.StatusCode}\n{body}\n{app.ErrorsText}");
            Assert.Contains("ECR-IMP-0422", body, StringComparison.Ordinal);
            Assert.Contains("err.ECR-IMP-0422.previewExpired", body, StringComparison.Ordinal);
        }

        Assert.Equal([null, null], await StoredAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-4.3")]
    [Trait("Finding", "L1-20")]
    public async Task Токен_чужого_користувача_не_застосовується_власний_діє_один_раз()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var owner = await SignInAsync(app, s.Users[0]).ConfigureAwait(true);
        using var stranger = await SignInAsync(app, s.Users[1]).ConfigureAwait(true);

        var token = await PreviewTokenAsync(app, owner, s, Book(s, (s.RowKeys[0], 11.5m), (s.RowKeys[1], 22.5m)))
            .ConfigureAwait(true);

        // Чужий користувач із ТИМИ САМИМИ правами: токен його не відкриває, і не розкриває, що він є.
        using (var foreign = await ApplyAsync(stranger, s, token).ConfigureAwait(true))
        {
            var body = await foreign.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(
                foreign.StatusCode == HttpStatusCode.UnprocessableEntity,
                $"чужий токен: {(int)foreign.StatusCode}\n{body}\n{app.ErrorsText}");
            Assert.Contains("err.ECR-IMP-0422.previewExpired", body, StringComparison.Ordinal);
        }

        Assert.Equal([null, null], await StoredAsync(s).ConfigureAwait(true));

        // Власник застосовує — дані лягають.
        using (var mine = await ApplyAsync(owner, s, token).ConfigureAwait(true))
        {
            var body = await mine.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(
                mine.StatusCode == HttpStatusCode.OK,
                $"власний токен: {(int)mine.StatusCode}\n{body}\n{app.ErrorsText}");
        }

        Assert.Equal([11.5m, 22.5m], await StoredAsync(s).ConfigureAwait(true));

        // Токен спожито: повтор — знову «перегляду немає», і нічого не міняється вдруге.
        using (var again = await ApplyAsync(owner, s, token).ConfigureAwait(true))
        {
            var body = await again.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(
                again.StatusCode == HttpStatusCode.UnprocessableEntity,
                $"повтор: {(int)again.StatusCode}\n{body}\n{app.ErrorsText}");
        }

        Assert.Equal([11.5m, 22.5m], await StoredAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-4.3")]
    [Trait("Requirement", "ФВ-0.2")]
    public async Task Рядок_книги_в_іншій_позиції_із_тим_самим_RowKey_змінює_саме_цей_рядок()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, s.Users[0]).ConfigureAwait(true);

        // У книзі рядки МІСЦЯМИ: RowKey[1] стоїть на першій позиції даних, RowKey[0] — на другій.
        var token = await PreviewTokenAsync(
            app, client, s, Book(s, (s.RowKeys[1], 222.5m), (s.RowKeys[0], 111.5m))).ConfigureAwait(true);

        using var applied = await ApplyAsync(client, s, token).ConfigureAwait(true);
        var body = await applied.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(
            applied.StatusCode == HttpStatusCode.OK,
            $"apply: {(int)applied.StatusCode}\n{body}\n{app.ErrorsText}");

        // Значення — за ключем, а не за номером рядка в книзі.
        Assert.Equal([111.5m, 222.5m], await StoredAsync(s).ConfigureAwait(true));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scenario(
        TestDocumentBuilder Builder,
        TestDocument Document,
        IReadOnlyList<string> Users,
        IReadOnlyList<string> RowKeys,
        string ColumnCode,
        int PeriodKey);

    private static async Task<HttpResponseMessage> ApplyAsync(HttpClient client, Scenario s, string previewToken)
        => await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/import/apply", UriKind.Relative),
            new { previewToken }).ConfigureAwait(false);

    private static async Task<string> PreviewTokenAsync(EcrApiFactory app, HttpClient client, Scenario s, byte[] book)
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

        var root = JsonDocument.Parse(body).RootElement;
        var token = root.GetProperty("previewToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token), $"перегляд без токена: {body}");

        return token!;
    }

    /// <summary>
    /// Книга: аркуш <c>MAIN</c>, значення йдуть у такому порядку, у якому передані; ключ рядка береться
    /// з КАРТИ (<c>_ecr</c>) за позицією в книзі, тобто «позиція → RowKey» задається тут.
    /// </summary>
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

    /// <summary>Збережені значення числової колонки двох рядків документа в порядку <c>RowKeys</c>.</summary>
    private async Task<List<decimal?>> StoredAsync(Scenario s)
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
    /// Документ у відкритому поточному періоді активного проєкту (див. <c>CellWriteRoundTripTests</c>) і ДВА
    /// користувачі з однією роллю: <c>Document.View</c> + <c>Document.Import</c>, грант <c>Write</c> на проєкт.
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
        var tag = Guid.NewGuid().ToString("N")[..10];
        var names = new[] { $"imp_a_{tag}", $"imp_b_{tag}" };

        await using var db = builder.CreateContext();
        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, document.SheetDefId));

        var role = new Role(
            EcrCode.Create($"IMP{Guid.NewGuid():N}"[..14]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Import token flow" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RolePermissions.Add(new RolePermission(role.Id, "Document.Import"));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Write));

        foreach (var name in names)
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
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

        return new Scenario(builder, document, names, rowKeys, columnCode, periodKey);
    }
}
