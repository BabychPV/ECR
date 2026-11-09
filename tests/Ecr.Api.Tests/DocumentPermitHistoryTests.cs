// tests/Ecr.Api.Tests/DocumentPermitHistoryTests.cs
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
/// Feature (j), human 08.11 («Користувач має сам поділити значення для місяця на 2 частини»), P1: the permit history of a
/// row / header field through the real HTTP pipeline — j1 (history in the same row), j4 (a gap is refused naming the
/// days), j6 (audit: who, both permits, the date), the right to add (anyone who may edit the permit cell), and Ship 1's
/// header rule (a mid-month header change is refused naming the rows to change instead).
/// </summary>
/// <remarks>
/// Fixture: document of period 202601 (TestDocumentBuilder), one row with numeric data in C2; a published permit type
/// whose header registry holds YEAR (all 2026), EARLY (01.01–09.01), MIDJAN (from 15.01), FEB (from 01.02).
/// Red proofs (run before the handlers existed / with the rule removed): no route → 404 on every POST; without the
/// <c>CanEditCellAsync</c> check the reader gets 200; without <c>GapDays</c> the gap add gets 200; without the audit write
/// no <c>PermitSegment</c> record.
/// </remarks>
[Collection("SqlServer")]
public sealed partial class DocumentPermitHistoryTests(SqlServerFixture sql)
{
    private const string Password = "Api-Doc-PermitH-2026!";
    private const string HeaderCode = "PERMIT";
    private static readonly string[] GrantReasons = ["NoGrant", "InsufficientGrantLevel"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "ACC-1108-j1")]
    public async Task A_row_keeps_its_permit_history_and_its_shared_month_needs_a_split_and_the_add_is_audited()
    {
        var s = await ArrangeAsync(rowBase: "year").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await AddRowAsync(client, s, s.MidJanId, "2026-01-15").ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");

        var permits = JsonDocument.Parse(body).RootElement.GetProperty("permits").EnumerateArray().ToList();
        Assert.Equal(2, permits.Count);
        Assert.Equal("KZ-YEAR", permits[0].GetProperty("permit").GetString());
        Assert.Equal("2026-01-14", permits[0].GetProperty("until").GetString());
        Assert.Equal("KZ-MIDJAN", permits[1].GetProperty("permit").GetString());
        Assert.Equal("2026-01-15", permits[1].GetProperty("from").GetString());

        // The history read: January (data in C2) is shared by the two permits and not split yet.
        var history = await client.GetAsync(new Uri($"{Base(s)}?periodKey=202601", UriKind.Relative)).ConfigureAwait(true);
        var historyBody = await history.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(history.StatusCode == HttpStatusCode.OK, $"{history.StatusCode}: {historyBody}");
        var root = JsonDocument.Parse(historyBody).RootElement;
        Assert.Equal(1, root.GetProperty("cellsNeedingSplit").GetInt32());
        var split = root.GetProperty("rows")[0].GetProperty("splitMonths")[0];
        Assert.Equal("2026-01-01", split.GetProperty("month").GetString());
        Assert.Equal(s.DataColumnCode, split.GetProperty("columnCodes")[0].GetString());
        var parts = split.GetProperty("parts").EnumerateArray().ToList();
        Assert.Equal(("2026-01-01", "2026-01-14"), (parts[0].GetProperty("dayFrom").GetString(), parts[0].GetProperty("dayTo").GetString()));
        Assert.Equal(("2026-01-15", "2026-01-31"), (parts[1].GetProperty("dayFrom").GetString(), parts[1].GetProperty("dayTo").GetString()));

        // j6: who added it, both permits and the date.
        var audit = await AuditAsync(s, "Add").ConfigureAwait(true);
        Assert.Contains("\"permit\":\"MIDJAN\"", audit, StringComparison.Ordinal);
        Assert.Contains("\"previousPermit\":\"YEAR\"", audit, StringComparison.Ordinal);
        Assert.Contains("\"validFrom\":\"2026-01-15\"", audit, StringComparison.Ordinal);
    }

    /// <summary>j4: EARLY is valid until 09.01, MIDJAN from 15.01 — 10–14 Jan have no permit; refused naming the days.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "ACC-1108-j4")]
    public async Task A_gap_between_the_permits_is_refused_naming_the_days()
    {
        var s = await ArrangeAsync(rowBase: "early").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await AddRowAsync(client, s, s.MidJanId, "2026-01-15").ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-CELL-4223", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-CELL-4223.permitGap", problem.GetProperty("messageKey").GetString());
        var detail = problem.GetProperty("detail").GetString() ?? string.Empty;
        Assert.Contains("10.01–14.01.2026", detail, StringComparison.Ordinal);
        Assert.Contains("KZ-EARLY", detail, StringComparison.Ordinal);
        Assert.Contains("KZ-MIDJAN", detail, StringComparison.Ordinal);
        Assert.Equal(0, await SegmentCountAsync(s).ConfigureAwait(true));
    }

    /// <summary>Human default 1: anyone who may EDIT the row's permit cell — a reader (Read grant) is refused.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "ACC-1108-j1")]
    public async Task A_reader_who_cannot_edit_the_permit_cell_cannot_add_a_permit()
    {
        var s = await ArrangeAsync(rowBase: "year", grant: GrantLevel.Read).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await AddRowAsync(client, s, s.MidJanId, "2026-01-15").ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-ACCS-0403", problem.GetProperty("errorCode").GetString());

        // The refusal is about the GRANT (the period is open), not about the period or the sheet state.
        Assert.Contains(problem.GetProperty("reason").GetString(), GrantReasons);
        Assert.Equal(0, await SegmentCountAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "ACC-1108-j6")]
    public async Task Undo_removes_the_latest_permit_once_and_is_audited()
    {
        var s = await ArrangeAsync(rowBase: "year").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var added = await AddRowAsync(client, s, s.MidJanId, "2026-01-15").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        var undo = await client.PostAsJsonAsync(
            new Uri($"{Base(s)}/rows/undo", UriKind.Relative),
            new { tableInstanceId = s.TableInstanceId, rowKey = s.RowKey, columnCode = s.PermitColumnCode }).ConfigureAwait(true);
        var body = await undo.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(undo.StatusCode == HttpStatusCode.OK, $"{undo.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Single(JsonDocument.Parse(body).RootElement.GetProperty("permits").EnumerateArray());
        Assert.Equal(0, await SegmentCountAsync(s).ConfigureAwait(true));
        Assert.Contains("\"permit\":\"MIDJAN\"", await AuditAsync(s, "Remove").ConfigureAwait(true), StringComparison.Ordinal);

        var again = await client.PostAsJsonAsync(
            new Uri($"{Base(s)}/rows/undo", UriKind.Relative),
            new { tableInstanceId = s.TableInstanceId, rowKey = s.RowKey, columnCode = s.PermitColumnCode }).ConfigureAwait(true);
        var againBody = await again.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(again.StatusCode == HttpStatusCode.UnprocessableEntity, $"{again.StatusCode}: {againBody}");
        Assert.Equal("err.ECR-REQ-0422.permitHistoryNothingToUndo", JsonDocument.Parse(againBody).RootElement.GetProperty("messageKey").GetString());
    }

    /// <summary>
    /// Coordinator 09.11 (Ship 1): a header permit change mid-month is refused with «add the permit per row», naming the rows
    /// that rely on the header permit and hold data in that month.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "ACC-1108-jh1")]
    public async Task A_header_permit_change_mid_month_is_refused_naming_the_rows_to_change()
    {
        var s = await ArrangeAsync(rowBase: null).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await AddHeaderAsync(client, s, s.MidJanId, "2026-01-15").ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("err.ECR-HDR-4223.permitChangeMidMonth", problem.GetProperty("messageKey").GetString());
        var detail = problem.GetProperty("detail").GetString() ?? string.Empty;
        Assert.Contains($" · {s.RowKey}", detail, StringComparison.Ordinal);
        Assert.Contains("15.01.2026", detail, StringComparison.Ordinal);
        Assert.Contains("add the new permit in the rows", detail, StringComparison.Ordinal);
        Assert.Equal(0, await SegmentCountAsync(s).ConfigureAwait(true));
    }

    /// <summary>Human default 2: a header change on the 1st is accepted and shows in the history.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "ACC-1108-jh1")]
    public async Task A_header_permit_change_on_the_1st_is_kept_in_the_header_history()
    {
        var s = await ArrangeAsync(rowBase: null).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await AddHeaderAsync(client, s, s.FebId, "2026-02-01").ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");

        var history = await client.GetAsync(new Uri($"{Base(s)}?periodKey=202601", UriKind.Relative)).ConfigureAwait(true);
        var root = JsonDocument.Parse(await history.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        var header = root.GetProperty("header")[0];
        Assert.Equal(HeaderCode, header.GetProperty("fieldCode").GetString());
        var permits = header.GetProperty("permits").EnumerateArray().ToList();
        Assert.Equal(("KZ-YEAR", "2026-01-31"), (permits[0].GetProperty("permit").GetString(), permits[0].GetProperty("until").GetString()));
        Assert.Equal(("KZ-FEB", "2026-02-01"), (permits[1].GetProperty("permit").GetString(), permits[1].GetProperty("from").GetString()));
        Assert.Equal(0, root.GetProperty("cellsNeedingSplit").GetInt32());
    }

    private static string Base(Scenario s) => $"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/permit-history";

    private static Task<HttpResponseMessage> AddRowAsync(HttpClient client, Scenario s, long entryId, string validFrom)
        => client.PostAsJsonAsync(
            new Uri($"{Base(s)}/rows", UriKind.Relative),
            new
            {
                tableInstanceId = s.TableInstanceId,
                rowKey = s.RowKey,
                columnCode = s.PermitColumnCode,
                registryEntryId = entryId,
                validFrom,
            });

    private static Task<HttpResponseMessage> AddHeaderAsync(HttpClient client, Scenario s, long entryId, string validFrom)
        => client.PostAsJsonAsync(
            new Uri($"{Base(s)}/header", UriKind.Relative),
            new { fieldCode = HeaderCode, registryEntryId = entryId, validFrom, periodKey = 202601 });

    private async Task<int> SegmentCountAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return await db.PermitSegments.CountAsync(x => x.DocumentId == s.DocumentId).ConfigureAwait(false);
    }

    private async Task<string> AuditAsync(Scenario s, string operation, string entityType = "PermitSegment")
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) COALESCE(NewJson, OldJson) FROM aud.StructureChange
             WHERE EntityType = @entity AND Operation = @op AND ChangedByUserId = @user
             ORDER BY ChangedAt DESC;
            """;
        command.Parameters.AddWithValue("@op", operation);
        command.Parameters.AddWithValue("@entity", entityType);
        command.Parameters.AddWithValue("@user", s.UserId);
        return (await command.ExecuteScalarAsync().ConfigureAwait(false)) as string ?? string.Empty;
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>
    /// Document 202601, one row with C2 = 12 in January. <paramref name="rowBase"/> (<c>year</c>/<c>early</c>) adds a permit
    /// Lookup column holding that permit; <c>null</c> — no row permit (the table relies on the header permit YEAR).
    /// </summary>
    private async Task<Scenario> ArrangeAsync(string? rowBase, GrantLevel grant = GrantLevel.Write)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 2, rowCount: 1).ConfigureAwait(false);

        // A Scheduled period is not editable (PeriodNotOpenYet) — open it, as for any cell write.
        await builder.OpenPeriodAsync(document).ConfigureAwait(false);
        await using var db = builder.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var registry = new RegistryDef(EcrCode.Create($"PRM_PH{tag}"), Text("Permit header"), isTemporal: true);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var type = new PermitTypeDef(EcrCode.Create($"PH{tag}"), Text("Water permit"), PermitCategory.Water, registry.Id);
        type.Publish(null, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        db.PermitTypeDefs.Add(type);

        RegistryEntry Entry(string code, DateOnly from, DateOnly to)
        {
            var e = new RegistryEntry(registry.Id, EcrCode.Create(code), Text($"KZ-{code}"));
            e.SetValidity(from, to);
            return e;
        }

        var year = Entry("YEAR", new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1));
        var early = Entry("EARLY", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 10));
        var midJan = Entry("MIDJAN", new DateOnly(2026, 1, 15), new DateOnly(2027, 1, 1));
        var feb = Entry("FEB", new DateOnly(2026, 2, 1), new DateOnly(2027, 1, 1));
        db.RegistryEntries.AddRange(year, early, midJan, feb);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var header = new HeaderFieldDef(document.TemplateVersionId, EcrCode.Create(HeaderCode), Text("Permit"), 0, CellDataType.Lookup);
        header.SetLookup(registry.Id);
        db.HeaderFieldDefs.Add(header);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.DocumentHeaderValues.Add(new DocumentHeaderValue(
            document.DocumentId, header.Id, new DocumentHeaderValueData { ValueRegistryEntryId = year.Id }));

        db.CellValues.Add(new CellValue(
            new CellAddress(document.PeriodKey, document.RowIds[0], document.ColumnDefIds[1]),
            document.TableDefId, new CellValueData { ValueNumeric = 12m }));

        var permitColumnCode = string.Empty;
        if (rowBase is not null)
        {
            permitColumnCode = $"PRM{tag}";
            var column = new ColumnDef(document.TableDefId, EcrCode.Create(permitColumnCode), Text("Permit number"), 9, CellDataType.Lookup);
            column.SetLookup(registry.Id);
            db.ColumnDefs.Add(column);
            await db.SaveChangesAsync().ConfigureAwait(false);
            db.CellValues.Add(new CellValue(
                new CellAddress(document.PeriodKey, document.RowIds[0], column.Id),
                document.TableDefId, new CellValueData { ValueRegistryEntryId = rowBase == "early" ? early.Id : year.Id }));
        }

        var userName = $"prmh_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(EcrCode.Create($"PRMH_{Guid.NewGuid():N}"), Text("Permit history"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RolePermissions.Add(new RolePermission(role.Id, "Registry.View"));
        db.RolePermissions.Add(new RolePermission(role.Id, "Document.Import"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, grant));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var rowKey = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == document.PeriodKey.Value && r.Id == document.RowIds[0])
            .Select(r => r.RowKeyValue)
            .SingleAsync().ConfigureAwait(false);
        var instanceId = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == document.PeriodKey.Value && r.Id == document.RowIds[0])
            .Select(r => r.TableInstanceId)
            .SingleAsync().ConfigureAwait(false);
        var dataColumnCode = await db.ColumnDefs.AsNoTracking()
            .Where(c => c.Id == document.ColumnDefIds[1])
            .Select(c => c.Code)
            .SingleAsync().ConfigureAwait(false);

        return new Scenario(
            document.DocumentId, instanceId, userName, user.Id, rowKey, permitColumnCode, dataColumnCode.ToString(),
            midJan.Id, feb.Id, document.RowIds[0], document.ColumnDefIds[1]);
    }

    private sealed record Scenario(
        long DocumentId, long TableInstanceId, string UserName, int UserId, string RowKey, string PermitColumnCode,
        string DataColumnCode, long MidJanId, long FebId, long RowId, int DataColumnId);
}
