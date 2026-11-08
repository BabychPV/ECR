// tests/Ecr.Api.Tests/DocumentPermitMonthsTests.cs
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
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Rule (h), human 08.11: a permit (entry of a published permit type header) must cover the months with data —
/// h1 (Contract tab header field) and h4 (existing violations in «Validate» / Issues), through the real HTTP pipeline,
/// middleware localization and the SQL reader of months.
/// </summary>
/// <remarks>
/// Fixture: document of period 202601 (TestDocumentBuilder), numeric data in January (C2). Permits: «March» valid
/// 01.03–30.11.2026 (does not cover January), «Year» valid all 2026.
/// Red proof: without <c>EnsureHeaderPermitCoversDataAsync</c> the header test gets 200; without
/// <c>PermitCoverageIssues</c> in <c>ValidateDocumentHandler</c> the Issues tests find nothing.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentPermitMonthsTests(SqlServerFixture sql)
{
    private const string Password = "Api-Doc-PermitM-2026!";
    private const string PermitCode = "PERMIT";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "ACC-1108-h1")]
    public async Task Header_permit_that_misses_a_month_with_data_is_refused_with_the_explanation()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PatchPermitAsync(client, app, s, s.MarchPermitId).ConfigureAwait(true);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-HDR-4223", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-HDR-4223.permitNotCoveringMonths", problem.GetProperty("messageKey").GetString());

        // The localized detail names the permit, its validity, the uncovered month and the way out.
        var detail = problem.GetProperty("detail").GetString() ?? string.Empty;
        Assert.Contains("KZ-MARCH", detail, StringComparison.Ordinal);
        Assert.Contains("01.03.2026 – 30.11.2026", detail, StringComparison.Ordinal);
        Assert.Contains("Jan 2026", detail, StringComparison.Ordinal);
        Assert.Contains("Pick a permit valid in those months", detail, StringComparison.Ordinal);
        Assert.Null(await StoredPermitAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "ACC-1108-h1")]
    public async Task Header_permit_covering_every_month_with_data_is_saved()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PatchPermitAsync(client, app, s, s.YearPermitId).ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}\n{app.ErrorsText}");
        Assert.Equal(s.YearPermitId, await StoredPermitAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "ACC-1108-h4")]
    public async Task Existing_header_violation_is_listed_in_validation_with_reason_and_fix()
    {
        var s = await ArrangeAsync(headerPermit: true).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var finding = await FindingAsync(client, app, s, f => f.GetProperty("tableDefId").GetInt32() == 0
            && f.GetProperty("ruleCode").GetString() == "ECR-HDR-0422").ConfigureAwait(true);

        Assert.Equal("Error", finding.GetProperty("severity").GetString());
        var message = finding.GetProperty("message").GetString() ?? string.Empty;
        Assert.Contains("KZ-MARCH", message, StringComparison.Ordinal);
        Assert.Contains("01.2026", message, StringComparison.Ordinal);
        Assert.Contains("extend the permit's validity", message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "ACC-1108-h4")]
    public async Task Existing_row_violation_is_listed_in_validation_at_the_permit_cell()
    {
        var s = await ArrangeAsync(rowPermit: true).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var finding = await FindingAsync(client, app, s, f => f.GetProperty("ruleCode").GetString() == "PERMIT-VALIDITY")
            .ConfigureAwait(true);

        Assert.Equal("Error", finding.GetProperty("severity").GetString());
        Assert.Equal(s.RowKey, finding.GetProperty("rowKey").GetString());
        Assert.Equal(s.PermitColumnCode, finding.GetProperty("columnCode").GetString());
        Assert.Contains("01.2026", finding.GetProperty("message").GetString() ?? string.Empty, StringComparison.Ordinal);
    }

    private static Uri HeaderUri(long documentId)
        => new($"/api/v1/documents/{documentId.ToString(CultureInfo.InvariantCulture)}/header", UriKind.Relative);

    private static async Task<JsonElement> FindingAsync(
        HttpClient client, EcrApiFactory app, Scenario s, Func<JsonElement, bool> match)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/validate", UriKind.Relative),
            new { periodKey = 202601 }).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"validate: {response.StatusCode}: {body}\n{app.ErrorsText}");

        var findings = JsonDocument.Parse(body).RootElement.GetProperty("messages").EnumerateArray().Select(f => f.Clone()).ToList();
        var found = findings.Where(match).ToList();
        Assert.True(found.Count == 1, $"очікувалась одна знахідка, є {found.Count}: {body}");
        return found[0];
    }

    private static async Task<HttpResponseMessage> PatchPermitAsync(HttpClient client, EcrApiFactory app, Scenario s, long entryId)
    {
        var get = await client.GetAsync(HeaderUri(s.DocumentId)).ConfigureAwait(false);
        var getBody = await get.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(get.StatusCode == HttpStatusCode.OK, $"GET header: {get.StatusCode}: {getBody}\n{app.ErrorsText}");
        var baseVersion = JsonDocument.Parse(getBody).RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;

        return await client.PatchAsJsonAsync(
            HeaderUri(s.DocumentId),
            new
            {
                fields = new[] { new { code = PermitCode, value = (object?)entryId.ToString(CultureInfo.InvariantCulture), isEmpty = false } },
                baseVersion,
            }).ConfigureAwait(false);
    }

    private async Task<long?> StoredPermitAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return await db.DocumentHeaderValues.AsNoTracking()
            .Where(v => v.DocumentId == s.DocumentId && v.HeaderFieldDefId == s.PermitFieldId)
            .Select(v => v.ValueRegistryEntryId)
            .SingleOrDefaultAsync().ConfigureAwait(false);
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
    /// Document 202601 with numeric data in January; a published permit type whose header registry holds «March» and
    /// «Year»; header field PERMIT targets that header. <paramref name="headerPermit"/> stores «March» in the header;
    /// <paramref name="rowPermit"/> adds a permit Lookup column to the table and stores «March» in the row with data.
    /// </summary>
    private async Task<Scenario> ArrangeAsync(bool headerPermit = false, bool rowPermit = false)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 2, rowCount: 1).ConfigureAwait(false);

        await using var db = builder.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var registry = new RegistryDef(EcrCode.Create($"PRM_PM{tag}"), Text("Permit header"), isTemporal: true);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var type = new PermitTypeDef(EcrCode.Create($"PM{tag}"), Text("Water permit"), PermitCategory.Water, registry.Id);
        type.Publish(null, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        db.PermitTypeDefs.Add(type);

        var march = new RegistryEntry(registry.Id, EcrCode.Create("MAR"), Text("KZ-MARCH"));
        march.SetValidity(new DateOnly(2026, 3, 1), new DateOnly(2026, 12, 1));
        var year = new RegistryEntry(registry.Id, EcrCode.Create("YEAR"), Text("KZ-YEAR"));
        year.SetValidity(new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1));
        db.RegistryEntries.AddRange(march, year);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var permit = new HeaderFieldDef(document.TemplateVersionId, EcrCode.Create(PermitCode), Text("Permit"), 0, CellDataType.Lookup);
        permit.SetLookup(registry.Id);
        db.HeaderFieldDefs.Add(permit);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // January data: C2 (Decimal) of the only row.
        db.CellValues.Add(new CellValue(
            new CellAddress(document.PeriodKey, document.RowIds[0], document.ColumnDefIds[1]),
            document.TableDefId, new CellValueData { ValueNumeric = 12m }));

        var permitColumnCode = string.Empty;
        if (rowPermit)
        {
            permitColumnCode = $"PRM{tag}";
            var column = new ColumnDef(document.TableDefId, EcrCode.Create(permitColumnCode), Text("Permit number"), 9, CellDataType.Lookup);
            column.SetLookup(registry.Id);
            db.ColumnDefs.Add(column);
            await db.SaveChangesAsync().ConfigureAwait(false);
            db.CellValues.Add(new CellValue(
                new CellAddress(document.PeriodKey, document.RowIds[0], column.Id),
                document.TableDefId, new CellValueData { ValueRegistryEntryId = march.Id }));
        }

        if (headerPermit)
        {
            db.DocumentHeaderValues.Add(new DocumentHeaderValue(
                document.DocumentId, permit.Id, new DocumentHeaderValueData { ValueRegistryEntryId = march.Id }));
        }

        var userName = $"prmm_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(EcrCode.Create($"PRMM_{Guid.NewGuid():N}"), Text("Permit months"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RolePermissions.Add(new RolePermission(role.Id, "Registry.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Write));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var rowKey = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == document.PeriodKey.Value && r.Id == document.RowIds[0])
            .Select(r => r.RowKeyValue)
            .SingleAsync().ConfigureAwait(false);

        return new Scenario(document.DocumentId, permit.Id, userName, march.Id, year.Id, rowKey, permitColumnCode);
    }

    private sealed record Scenario(
        long DocumentId, int PermitFieldId, string UserName, long MarchPermitId, long YearPermitId, string RowKey, string PermitColumnCode);
}
