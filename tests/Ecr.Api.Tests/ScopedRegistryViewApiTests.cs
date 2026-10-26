// tests/Ecr.Api.Tests/ScopedRegistryViewApiTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
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
/// P48 (hotfix p47): вибір запису Lookup у шапці й комірці справжнім HTTP під РЕАЛЬНИМИ користувачами: роль із областю
/// «проєкт документа» з <c>Registry.View</c> - 200; View лише на інший проєкт, без View, View + Deny - 422 побайтно як
/// для неіснуючого запису; глобальний View - 200; роль без грантів Write комірок не пише.
/// </summary>
/// <remarks>
/// ⛔ Урок p47: <c>Registry.View</c> входить у <c>PermissionScopes.Global</c>, тож юніт-профіль, зібраний напряму, не бачив,
/// що scoped-оператор після фіксу втрачає вибір Lookup. Тут профіль будує сервер зі справжніх призначень.
/// Мутація: прибрати гілку scoped View в <c>LookupTargetMatcher.CanViewRegistry</c> - червоніють випадки «scoped».
/// </remarks>
[Collection("SqlServer")]
public sealed class ScopedRegistryViewApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Scoped-View-2026!";
    private const int PeriodKeyValue = 202609;

    [Theory]
    [InlineData("scoped", 200)]
    [InlineData("global", 200)]
    [InlineData("scopedElsewhere", 422)]
    [InlineData("none", 422)]
    [InlineData("deniedUnscopedRole", 422)]
    [InlineData("scopedSelfDeny", 422)]
    [InlineData("scopedReadGrant", 200)]
    [InlineData("narrowedDenyPlusView", 422)]
    [InlineData("narrowedViewOnly", 422)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P48")]
    public async Task Шапка_Lookup_за_типом_доступу_до_реєстру(string kind, int expected)
    {
        var s = await ArrangeAsync(kind).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PatchHeaderAsync(client, app, s, s.EntryId).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True((int)response.StatusCode == expected, $"{response.StatusCode}: {body}\n{app.ErrorsText}");

        if (expected == 422)
        {
            // Побайтно як для запису, якого немає (після нормалізації id запису).
            var absent = await PatchHeaderAsync(client, app, s, 987_654_321).ConfigureAwait(true);
            var absentBody = await absent.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, absent.StatusCode);
            Assert.Equal("err.ECR-HDR-0422.lookupEntryMissing", JsonDocument.Parse(body).RootElement.GetProperty("messageKey").GetString());
            Assert.Equal(Normalize(absentBody), Normalize(body));
            Assert.Null(await StoredHeaderAsync(s).ConfigureAwait(true));
        }
        else
        {
            Assert.Equal(s.EntryId, await StoredHeaderAsync(s).ConfigureAwait(true));
        }
    }

    [Theory]
    [InlineData("scoped", 200)]
    [InlineData("global", 200)]
    [InlineData("scopedElsewhere", 422)]
    [InlineData("none", 422)]
    [InlineData("deniedUnscopedRole", 422)]
    [InlineData("scopedSelfDeny", 422)]
    [InlineData("scopedReadGrant", 200)]
    [InlineData("narrowedDenyPlusView", 422)]
    [InlineData("narrowedViewOnly", 422)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P48")]
    public async Task Комірка_Lookup_за_типом_доступу_до_реєстру(string kind, int expected)
    {
        var s = await ArrangeAsync(kind).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var version = await RowVersionAsync(client, s).ConfigureAwait(true);
        var response = await PatchCellAsync(client, s, version, s.EntryId).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True((int)response.StatusCode == expected, $"{response.StatusCode}: {body}\n{app.ErrorsText}");

        if (expected == 422)
        {
            var absent = await PatchCellAsync(client, s, version, 987_654_321).ConfigureAwait(true);
            var absentBody = await absent.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, absent.StatusCode);
            Assert.Equal("err.ECR-CELL-4223.missingEntry", JsonDocument.Parse(body).RootElement.GetProperty("messageKey").GetString());
            Assert.Equal(Normalize(absentBody), Normalize(body));
            Assert.Null(await StoredCellAsync(s).ConfigureAwait(true));
        }
        else
        {
            Assert.Equal(s.EntryId, await StoredCellAsync(s).ConfigureAwait(true));
        }
    }

    /// <summary>
    /// Регресія: роль лише з <c>Document.View</c> (як PeriodAdministrator/BootstrapAdministrator у сіді, без грантів
    /// Write на проєкт) комірок не пише - з Registry.View чи без.
    /// </summary>
    [Theory]
    [InlineData("noGrants")]
    [InlineData("periodAdministrator")]
    [InlineData("bootstrapAdministrator")]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P48")]
    public async Task Роль_без_грантів_Write_Lookup_у_комірку_не_пише(string kind)
    {
        var s = await ArrangeAsync(kind).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PatchCellAsync(client, s, "0x00", s.EntryId).ConfigureAwait(true);

        Assert.False(response.IsSuccessStatusCode, $"{response.StatusCode}");
        Assert.Null(await StoredCellAsync(s).ConfigureAwait(true));
    }

    /// <summary>
    /// P3 (рецензія p48): Excel-книга під РЕАЛЬНИМ користувачем. Код запису, до реєстру якого немає View, у перегляді
    /// відповідає так само, як неіснуючий код (без розкриття існування); scoped-оператор з View на проєкт документа -
    /// зміна в перегляді й запис при застосуванні.
    /// </summary>
    [Theory]
    [InlineData("scoped", true)]
    [InlineData("global", true)]
    [InlineData("scopedElsewhere", false)]
    [InlineData("none", false)]
    [InlineData("scopedSelfDeny", false)]
    [InlineData("scopedReadGrant", true)]
    [InlineData("narrowedDenyPlusView", false)]
    [InlineData("narrowedViewOnly", false)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P3-BOOK")]
    public async Task Книга_Lookup_за_типом_доступу_до_реєстру(string kind, bool allowed)
    {
        var b = await ArrangeBookAsync(kind).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, b.UserName).ConfigureAwait(true);

        var existing = await BookPreviewAsync(app, client, b, "E1").ConfigureAwait(true);
        var missing = await BookPreviewAsync(app, client, b, "ZZZ").ConfigureAwait(true);

        if (allowed)
        {
            Assert.Equal(1, existing.GetProperty("changes").GetArrayLength());
            Assert.Equal(0, existing.GetProperty("rejected").GetArrayLength());
            var token = existing.GetProperty("previewToken").GetString()!;
            using var apply = await client.PostAsJsonAsync(
                new Uri($"/api/v1/documents/{b.Doc.DocumentId}/import/apply", UriKind.Relative),
                new { previewToken = token }).ConfigureAwait(true);
            var applyBody = await apply.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(apply.StatusCode == HttpStatusCode.OK, $"{apply.StatusCode}: {applyBody}\n{app.ErrorsText}");
            Assert.Equal(b.EntryId, await StoredBookCellAsync(b).ConfigureAwait(true));
        }
        else
        {
            Assert.Equal(0, existing.GetProperty("changes").GetArrayLength());
            var refusedExisting = existing.GetProperty("rejected").EnumerateArray().Single();
            var refusedMissing = missing.GetProperty("rejected").EnumerateArray().Single();
            Assert.Equal(
                refusedMissing.GetProperty("messageKey").GetString(),
                refusedExisting.GetProperty("messageKey").GetString());
            Assert.Null(await StoredBookCellAsync(b).ConfigureAwait(true));
        }
    }

    private static async Task<JsonElement> BookPreviewAsync(EcrApiFactory app, HttpClient client, BookScenario b, string code)
    {
        using var content = new MultipartFormDataContent();
        using var file = new ByteArrayContent(Book(b, code));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(file, "file", "book.xlsx");

        using var response = await client.PostAsync(
            new Uri($"/api/v1/documents/{b.Doc.DocumentId}/import/preview", UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"перегляд: {response.StatusCode}\n{body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static byte[] Book(BookScenario b, string code)
    {
        using var workbook = new XLWorkbook();
        var main = workbook.Worksheets.Add("MAIN");
        main.Cell(2, 1).Value = code;

        var block = new ExcelTableBlock(
            b.Doc.TableInstanceId, b.Doc.TableDefId, "MAIN_FILE", "MAIN", HeaderRow: 1,
            Columns: [new ExcelColumnRef(b.Doc.ColumnDefIds[1], b.ColumnCode, 1, false, null)],
            Rows: [new ExcelRowRef(b.RowKey, 2)]);
        var map = new ExcelWorkbookMap(b.Doc.DocumentId, b.PeriodKey, b.Doc.TemplateVersionId, [block]);
        workbook.Worksheets.Add(ExcelWorkbookMap.SheetName).Cell(1, 1).Value =
            JsonSerializer.Serialize(map, MapJson);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private async Task<long?> StoredBookCellAsync(BookScenario b)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return await db.CellValues.AsNoTracking()
            .Where(c => c.PeriodKeyValue == b.PeriodKey && c.TableRowId == b.RowId && c.ColumnDefId == b.Doc.ColumnDefIds[1])
            .Select(c => c.ValueRegistryEntryId)
            .SingleOrDefaultAsync().ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions MapJson = new(JsonSerializerDefaults.Web);

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    private sealed record BookScenario(
        TestDocument Doc, string UserName, long EntryId, string ColumnCode, string RowKey, long RowId, int PeriodKey);

    /// <summary>Документ у відкритому ПОТОЧНОМУ періоді активного проєкту (як <c>ImportPreviewTokenApiTests</c>).</summary>
    private async Task<BookScenario> ArrangeBookAsync(string kind)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SiteZone));
        var periodKey = (siteToday.Year * 100) + siteToday.Month;
        var doc = await builder.BuildAsync(periodKey, columnCount: 2, rowCount: 1).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        await using var db = builder.CreateContext();
        db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));

        var registry = new RegistryDef(EcrCode.Create($"SCB_{Guid.NewGuid():N}"[..20]), Text("Book view"), isTemporal: true);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var entry = new RegistryEntry(registry.Id, EcrCode.Create("E1"), Text("Entry 1"));
        entry.SetValidity(new DateOnly(2025, 1, 1), new DateOnly(2030, 12, 31));
        db.RegistryEntries.Add(entry);

        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE cfg.ColumnDef SET DataType = @type, LookupRegistryDefId = @registry WHERE Id = @id";
            command.Parameters.AddWithValue("@type", (int)CellDataType.Lookup);
            command.Parameters.AddWithValue("@registry", registry.Id);
            command.Parameters.AddWithValue("@id", doc.ColumnDefIds[1]);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var userName = $"scb_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(EcrCode.Create($"SCB_{Guid.NewGuid():N}"[..24]), Text("Book user"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RolePermissions.Add(new RolePermission(role.Id, "Document.Import"));
        if (kind is "scoped" or "global" or "scopedSelfDeny" or "narrowedDenyPlusView")
        {
            db.RolePermissions.Add(new RolePermission(role.Id, "Registry.View"));
        }

        var assignment = new RoleAssignment(role.Id, user.Id, null);
        if (kind is "scoped" or "scopedSelfDeny" or "scopedReadGrant")
        {
            assignment.SetScope(RoleAssignmentScope.Create([doc.ProjectId]));
        }

        db.RoleAssignments.Add(assignment);
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Write));
        if (kind == "scopedReadGrant")
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Registry, registry.Id, GrantLevel.Read));
        }

        if (kind == "scopedSelfDeny")
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Registry, registry.Id, GrantLevel.None, isDeny: true));
        }

        if (kind is "narrowedDenyPlusView" or "narrowedViewOnly")
        {
            var narrowed = new Role(EcrCode.Create($"SCN_{Guid.NewGuid():N}"[..24]), Text("Narrowed"));
            db.Roles.Add(narrowed);
            await db.SaveChangesAsync().ConfigureAwait(false);
            if (kind == "narrowedDenyPlusView")
            {
                db.ResourceGrants.Add(new ResourceGrant(narrowed.Id, ResourceKind.Registry, registry.Id, GrantLevel.None, isDeny: true));
            }
            else
            {
                db.RolePermissions.Add(new RolePermission(narrowed.Id, "Registry.View"));
                db.ResourceGrants.Add(new ResourceGrant(narrowed.Id, ResourceKind.Registry, registry.Id, GrantLevel.Read));
            }

            var narrowAssignment = new RoleAssignment(narrowed.Id, user.Id, null);
            narrowAssignment.SetScope(RoleAssignmentScope.Create([doc.ProjectId], ["SHEET_X"]));
            db.RoleAssignments.Add(narrowAssignment);
        }

        if (kind == "scopedElsewhere")
        {
            var other = new Role(EcrCode.Create($"SCO_{Guid.NewGuid():N}"[..24]), Text("View elsewhere"));
            db.Roles.Add(other);
            await db.SaveChangesAsync().ConfigureAwait(false);
            db.RolePermissions.Add(new RolePermission(other.Id, "Registry.View"));
            var elsewhere = new RoleAssignment(other.Id, user.Id, null);
            elsewhere.SetScope(RoleAssignmentScope.Create([doc.ProjectId + 1_000_000]));
            db.RoleAssignments.Add(elsewhere);
        }

        var project = await db.Projects.FirstAsync(p => p.Id == doc.ProjectId).ConfigureAwait(false);
        project.Activate(now);
        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var period = await db.Periods
            .FirstAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == periodKey).ConfigureAwait(false);
        period.RecomputeBoundaries(policy, SiteZone);
        period.AdvanceTo(PeriodState.Open, now);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var column = await db.ColumnDefs.AsNoTracking().Where(c => c.Id == doc.ColumnDefIds[1])
            .Select(c => c.Code).SingleAsync().ConfigureAwait(false);
        var rowKey = await db.TableRows.AsNoTracking().Where(r => r.Id == doc.RowIds[0])
            .Select(r => r.RowKeyValue).SingleAsync().ConfigureAwait(false);

        return new BookScenario(doc, userName, entry.Id, column, rowKey, doc.RowIds[0], periodKey);
    }
    /// <summary>Без числових ідентифікаторів: id запису й документа в тілі відмови різняться між викликами.</summary>
    private static string Normalize(string body)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(body, @"\d+", "N");
        return System.Text.RegularExpressions.Regex.Replace(text, "\"correlationId\"\\s*:\\s*\"[^\"]*\"", "\"correlationId\":\"x\"");
    }

    private static Uri HeaderUri(long documentId)
        => new($"/api/v1/documents/{documentId.ToString(CultureInfo.InvariantCulture)}/header", UriKind.Relative);

    private static async Task<HttpResponseMessage> PatchHeaderAsync(HttpClient client, EcrApiFactory app, Scenario s, long entryId)
    {
        var get = await client.GetAsync(HeaderUri(s.Doc.DocumentId)).ConfigureAwait(false);
        var getBody = await get.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(get.StatusCode == HttpStatusCode.OK, $"GET header: {get.StatusCode}: {getBody}\n{app.ErrorsText}");
        var baseVersion = JsonDocument.Parse(getBody).RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;

        return await client.PatchAsJsonAsync(
            HeaderUri(s.Doc.DocumentId),
            new
            {
                fields = new[] { new { code = "PERMIT", value = (object?)entryId.ToString(CultureInfo.InvariantCulture), isEmpty = false } },
                baseVersion,
            }).ConfigureAwait(false);
    }

    private static async Task<string> RowVersionAsync(HttpClient client, Scenario s)
    {
        var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/tables/{s.Doc.TableInstanceId}", UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"Зріз: {response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("rows").EnumerateArray()
            .Single(r => r.GetProperty("rowKey").GetString() == s.RowKey)
            .GetProperty("rowVersion").GetString()!;
    }

    private static Task<HttpResponseMessage> PatchCellAsync(HttpClient client, Scenario s, string baseVersion, long entryId)
        => client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = s.Doc.TableInstanceId,
                periodKey = PeriodKeyValue,
                origin = "UserEdit",
                rows = new object[]
                {
                    new
                    {
                        rowKey = s.RowKey,
                        baseVersion,
                        cells = new[] { new { columnCode = s.LookupColumnCode, value = (object)entryId } },
                    },
                },
            });

    private async Task<long?> StoredHeaderAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return await db.DocumentHeaderValues.AsNoTracking()
            .Where(v => v.DocumentId == s.Doc.DocumentId && v.HeaderFieldDefId == s.PermitFieldId)
            .Select(v => v.ValueRegistryEntryId)
            .SingleOrDefaultAsync().ConfigureAwait(false);
    }

    private async Task<long?> StoredCellAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return await db.CellValues.AsNoTracking()
            .Where(c => c.PeriodKeyValue == PeriodKeyValue && c.TableRowId == s.RowId && c.ColumnDefId == s.LookupColumnId)
            .Select(c => c.ValueRegistryEntryId)
            .SingleOrDefaultAsync().ConfigureAwait(false);
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

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>
    /// Документ у відкритому періоді з Lookup-полем шапки <c>PERMIT</c> і Lookup-колонкою на ТОЙ САМИЙ довідник; запис
    /// чинний увесь рік; користувач за <paramref name="kind"/>.
    /// </summary>
    private async Task<Scenario> ArrangeAsync(string kind)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(PeriodKeyValue, columnCount: 2, rowCount: 1).ConfigureAwait(false);
        await using var db = builder.CreateContext();

        var period = await db.Periods
            .FirstAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == PeriodKeyValue).ConfigureAwait(false);
        period.AdvanceTo(PeriodState.Open, DateTime.UtcNow);

        var registry = new RegistryDef(EcrCode.Create($"SCV_{Guid.NewGuid():N}"[..20]), Text("Scoped view"), isTemporal: true);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var entry = new RegistryEntry(registry.Id, EcrCode.Create("E1"), Text("Entry 1"));
        entry.SetValidity(new DateOnly(2025, 1, 1), new DateOnly(2027, 12, 31));
        db.RegistryEntries.Add(entry);

        var permit = new HeaderFieldDef(doc.TemplateVersionId, EcrCode.Create("PERMIT"), Text("Permit"), 0, CellDataType.Lookup);
        permit.SetLookup(registry.Id);
        db.HeaderFieldDefs.Add(permit);
        await db.SaveChangesAsync().ConfigureAwait(false);

        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE cfg.ColumnDef SET DataType = @type, LookupRegistryDefId = @registry WHERE Id = @id";
            command.Parameters.AddWithValue("@type", (int)CellDataType.Lookup);
            command.Parameters.AddWithValue("@registry", registry.Id);
            command.Parameters.AddWithValue("@id", doc.ColumnDefIds[1]);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var userName = $"scv_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(EcrCode.Create($"SCV_{Guid.NewGuid():N}"[..24]), Text("Scoped view user"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // PeriodAdministrator / BootstrapAdministrator: набір прав вбудованої ролі з сіду БЕЗ ресурсних грантів Write.
        var permissions = kind switch
        {
            "periodAdministrator" => new[] { "Document.View", "Project.Manage" },
            "bootstrapAdministrator" => new[] { "Security.ManageUsers", "Security.ManageRoles" },
            _ => new[] { "Document.View", "Document.Import" },
        };
        foreach (var permission in permissions)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        var assignment = new RoleAssignment(role.Id, user.Id, null);
        if (kind is "scoped" or "global" or "deniedUnscopedRole" or "scopedSelfDeny" or "narrowedDenyPlusView")
        {
            db.RolePermissions.Add(new RolePermission(role.Id, "Registry.View"));
        }

        if (kind is "scoped" or "scopedSelfDeny" or "scopedReadGrant")
        {
            assignment.SetScope(RoleAssignmentScope.Create([doc.ProjectId]));
        }

        db.RoleAssignments.Add(assignment);
        if (kind == "scopedReadGrant")
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Registry, registry.Id, GrantLevel.Read));
        }

        if (kind is not ("noGrants" or "periodAdministrator" or "bootstrapAdministrator"))
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Write));
        }

        if (kind is "deniedUnscopedRole" or "scopedSelfDeny")
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Registry, registry.Id, GrantLevel.None, isDeny: true));
        }

        if (kind is "narrowedDenyPlusView" or "narrowedViewOnly")
        {
            // Звужена (аркуші) роль у проєкті документа: Deny на реєстр (narrowedDenyPlusView) або лише View + Read-грант
            // (narrowedViewOnly - fail-closed: доступу не дає).
            var narrowed = new Role(EcrCode.Create($"SCN_{Guid.NewGuid():N}"[..24]), Text("Narrowed"));
            db.Roles.Add(narrowed);
            await db.SaveChangesAsync().ConfigureAwait(false);
            if (kind == "narrowedDenyPlusView")
            {
                db.ResourceGrants.Add(new ResourceGrant(narrowed.Id, ResourceKind.Registry, registry.Id, GrantLevel.None, isDeny: true));
            }
            else
            {
                db.RolePermissions.Add(new RolePermission(narrowed.Id, "Registry.View"));
                db.ResourceGrants.Add(new ResourceGrant(narrowed.Id, ResourceKind.Registry, registry.Id, GrantLevel.Read));
            }

            var narrowAssignment = new RoleAssignment(narrowed.Id, user.Id, null);
            narrowAssignment.SetScope(RoleAssignmentScope.Create([doc.ProjectId], ["SHEET_X"]));
            db.RoleAssignments.Add(narrowAssignment);
        }

        if (kind == "scopedElsewhere")
        {
            // View є, але призначений лише на ІНШИЙ проєкт.
            var other = new Role(EcrCode.Create($"SCO_{Guid.NewGuid():N}"[..24]), Text("View elsewhere"));
            db.Roles.Add(other);
            await db.SaveChangesAsync().ConfigureAwait(false);
            db.RolePermissions.Add(new RolePermission(other.Id, "Registry.View"));
            var elsewhere = new RoleAssignment(other.Id, user.Id, null);
            elsewhere.SetScope(RoleAssignmentScope.Create([doc.ProjectId + 1_000_000]));
            db.RoleAssignments.Add(elsewhere);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        var column = await db.ColumnDefs.AsNoTracking().Where(c => c.Id == doc.ColumnDefIds[1])
            .Select(c => c.Code).SingleAsync().ConfigureAwait(false);
        var rowKey = await db.TableRows.AsNoTracking().Where(r => r.Id == doc.RowIds[0])
            .Select(r => r.RowKeyValue).SingleAsync().ConfigureAwait(false);

        return new Scenario(doc, userName, entry.Id, permit.Id, rowKey, doc.RowIds[0], doc.ColumnDefIds[1], column);
    }

    private sealed record Scenario(
        TestDocument Doc, string UserName, long EntryId, int PermitFieldId, string RowKey, long RowId,
        int LookupColumnId, string LookupColumnCode);
}
