using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
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
/// PA2-S-02: PATCH у колонку з роллю «ключ рядка» рядка, який створила прив'язка до дозволу (ключ <c>P&lt;id&gt;</c>), - 403
/// <c>deniedCells</c> з причиною <c>ColumnReadOnly</c>, як для «ліміту»; звичайна колонка факту й людський рядок без ключа
/// виду <c>P&lt;id&gt;</c> пишуться, як раніше.
/// </summary>
/// <remarks>
/// Мутаційний доказ: прибрати виклик <c>EnsurePermitRowKeysUntouchedAsync</c> у <c>PatchCellsHandler.HandleAsync</c> (або
/// замінити <c>PermitColumnRole.RowKey</c> на <c>Limit</c>) - червоніє перший тест (PATCH ключа дає 200).
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitRowKeyPatchTests(SqlServerFixture sql)
{
    private const string Password = "Permit-RowKey-Patch-2026!";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ключ_рядка_дозволу_людина_не_править_403_ColumnReadOnly_а_факт_і_людський_рядок_пишуться()
    {
        using var app = new EcrApiFactory(sql);
        using var admin = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Permit.EditType", "Permit.PublishType", "Template.View", "Template.Edit").ConfigureAwait(true);
        await PublishTypeAsync(admin).ConfigureAwait(true);
        var (doc, userName, periodKey) = await ArrangeAsync().ConfigureAwait(true);
        await BindAsync(admin, doc).ConfigureAwait(true);

        using var writer = app.CreateClient();
        var login = await writer.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");

        var codes = await ColumnCodesAsync(writer, doc, app).ConfigureAwait(true);
        var (key, fact) = (codes[0], codes[2]);

        // Рядок з ключем виду P<id> (як створює прив'язка) і людський рядок.
        var created = await PatchAsync(writer, doc, periodKey, [Row("P777", null, (key, "Substance A"), (fact, 12.5m)), Row($"HUMAN{_tag}", null, (key, "Mine"))]).ConfigureAwait(true);
        Assert.True(created.Status == HttpStatusCode.OK, created.Text + app.ErrorsText);
        var versions = created.Json.GetProperty("rowVersions");
        var permitRowVersion = versions.GetProperty("P777").GetString();
        var humanRowVersion = versions.GetProperty($"HUMAN{_tag}").GetString();

        // Ключ рядка дозволу: відмова тим самим кодом і причиною, що й для «ліміту».
        var denied = await PatchAsync(writer, doc, periodKey, [Row("P777", permitRowVersion, (key, "hack"))]).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal("ECR-ACCS-0403", denied.Json.GetProperty("errorCode").GetString());
        Assert.Equal("ColumnReadOnly", denied.Json.GetProperty("reason").GetString());

        // Звичайна колонка факту того самого рядка - 200; людський рядок (ключ не P<число>) править навіть колонку-ключ.
        var factOk = await PatchAsync(writer, doc, periodKey, [Row("P777", permitRowVersion, (fact, 13m))]).ConfigureAwait(true);
        Assert.True(factOk.Status == HttpStatusCode.OK, factOk.Text);
        var humanOk = await PatchAsync(writer, doc, periodKey, [Row($"HUMAN{_tag}", humanRowVersion, (key, "Renamed"))]).ConfigureAwait(true);
        Assert.True(humanOk.Status == HttpStatusCode.OK, humanOk.Text);
    }

    private static async Task<(HttpStatusCode Status, string Text, JsonElement Json)> PatchAsync(
        HttpClient client, TestDocument doc, int periodKey, object[] rows)
    {
        var response = await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/cells", UriKind.Relative),
            new { tableInstanceId = doc.TableInstanceId, periodKey, origin = "UserEdit", rows }).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        return (response.StatusCode, text, JsonDocument.Parse(text).RootElement.Clone());
    }

    private static object Row(string rowKey, string? baseVersion, params (string Column, object Value)[] cells)
        => new { rowKey, baseVersion, cells = cells.Select(c => new { columnCode = c.Column, value = c.Value }).ToArray() };

    private static async Task<List<string>> ColumnCodesAsync(HttpClient client, TestDocument doc, EcrApiFactory app)
    {
        var opened = await client.GetAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/tables/{doc.TableInstanceId}", UriKind.Relative)).ConfigureAwait(false);
        var text = await opened.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(opened.IsSuccessStatusCode, $"GET зрізу: {opened.StatusCode}\n{text}\n{app.ErrorsText}");

        return [.. JsonDocument.Parse(text).RootElement.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("code").GetString()!)];
    }

    /// <summary>Документ з динамічною таблицею (ліміт - лише читання), користувач із правом писати, відкритий поточний період.</summary>
    private async Task<(TestDocument Doc, string UserName, int PeriodKey)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SiteZone));
        var periodKey = (siteToday.Year * 100) + siteToday.Month;
        var doc = await builder.BuildAsync(periodKey, columnCount: 3, rowCount: 0, rowMode: TableRowMode.Dynamic).ConfigureAwait(false);

        var userName = $"rk_{Guid.NewGuid():N}"[..20];
        var now = DateTime.UtcNow;
        await using var db = builder.CreateContext();

        var limit = await db.ColumnDefs.SingleAsync(c => c.Id == doc.ColumnDefIds[1]).ConfigureAwait(false);
        limit.SetReadOnly(true);
        limit.SetNumericFormat(12, 3);
        db.HeaderFieldDefs.Add(new HeaderFieldDef(
            doc.TemplateVersionId, EcrCode.Create("Facility"), Name("Facility"), 1, CellDataType.String));
        db.DocumentSheets.Add(new Ecr.Domain.Entities.Documents.DocumentSheet(doc.DocumentId, doc.SheetDefId));

        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(EcrCode.Create($"RK_{Guid.NewGuid():N}"), Name("Permit row key writer"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Write));

        var project = await db.Projects.FirstAsync(p => p.Id == doc.ProjectId).ConfigureAwait(false);
        project.Activate(now);
        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var period = await db.Periods.FirstAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == periodKey).ConfigureAwait(false);
        period.RecomputeBoundaries(policy, SiteZone);
        period.AdvanceTo(PeriodState.Open, now);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (doc, userName, periodKey);
    }

    private async Task BindAsync(HttpClient admin, TestDocument doc)
    {
        var response = await admin.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{doc.TemplateVersionId}/permit-bindings/RK{_tag}", UriKind.Relative),
            new
            {
                sheetTableDefId = doc.TableDefId,
                permitTypeCode = $"AIR{_tag}",
                permitTableCode = "Emissions",
                mode = "Rows",
                selectionRule = "ByFacilityPeriodEnd",
                headerFieldDefId = (int?)null,
                isActive = true,
                columns = new object[]
                {
                    new { role = "RowKey", permitFieldCode = "Substance", sheetColumnDefId = doc.ColumnDefIds[0] },
                    new { role = "Limit", permitFieldCode = "Limit", sheetColumnDefId = doc.ColumnDefIds[1] },
                },
            }).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
    }

    private async Task PublishTypeAsync(HttpClient client)
    {
        var code = $"AIR{_tag}";
        var created = await client.PostAsJsonAsync(
            new Uri("/api/v1/permit-types", UriKind.Relative), new { code, nameL10n = new { en = "Air" }, category = "Air" }).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var rowVersion = JsonDocument.Parse(await created.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement.GetProperty("rowVersion").GetString();

        static object Col(string name, string type, bool key, int? precision = null, int? scale = null) => new
        {
            code = name, nameL10n = new { en = name }, dataType = type, unitId = (int?)null, required = key, key,
            group = (string?)null, precision, scale, listScope = (string?)null, listRegistryCode = (string?)null,
            siblingTableCode = (string?)null, choices = (string[]?)null, order = 1,
        };

        var replaced = await client.PutAsJsonAsync(
            new Uri($"/api/v1/permit-types/{code}", UriKind.Relative),
            new
            {
                rowVersion,
                nameL10n = new { en = "Air" },
                category = "Air",
                tables = new[]
                {
                    new
                    {
                        code = "Emissions", nameL10n = new { en = "Emissions" }, order = 1,
                        columns = new[] { Col("Substance", "String", true), Col("Limit", "Decimal", false, 12, 3) },
                        rules = Array.Empty<object>(),
                    },
                },
                rules = Array.Empty<object>(),
            }).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var draft = JsonDocument.Parse(await replaced.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement.GetProperty("rowVersion").GetString();

        var published = await client.PostAsJsonAsync(
            new Uri($"/api/v1/permit-types/{code}/publish", UriKind.Relative), new { reason = "випуск для PA2-S-02", rowVersion = draft }).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
