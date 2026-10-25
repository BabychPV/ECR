// tests/Ecr.Api.Tests/ScopedRegistryViewApiTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    [InlineData("denied", 422)]
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
    [InlineData("denied", 422)]
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
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P48")]
    public async Task Роль_без_грантів_Write_Lookup_у_комірку_не_пише()
    {
        var s = await ArrangeAsync("noGrants").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PatchCellAsync(client, s, "0x00", s.EntryId).ConfigureAwait(true);

        Assert.False(response.IsSuccessStatusCode, $"{response.StatusCode}");
        Assert.Null(await StoredCellAsync(s).ConfigureAwait(true));
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

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        var assignment = new RoleAssignment(role.Id, user.Id, null);
        if (kind is "scoped" or "global" or "denied")
        {
            db.RolePermissions.Add(new RolePermission(role.Id, "Registry.View"));
        }

        if (kind == "scoped")
        {
            assignment.SetScope(RoleAssignmentScope.Create([doc.ProjectId]));
        }

        db.RoleAssignments.Add(assignment);
        if (kind != "noGrants")
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Write));
        }

        if (kind == "denied")
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Registry, registry.Id, GrantLevel.None, isDeny: true));
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
