// tests/Ecr.Api.Tests/Security/CellStylesApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// CELL-STYLE L3b: користувацька заливка/колір тексту/жирність комірок
/// (<c>PATCH …/tables/{id}/cell-styles</c>) — наскрізно: справжній SQL, справжній вхід, HTTP.
/// </summary>
/// <remarks>
/// ⚠ Колір — НЕ дані: значення комірки, версія рядка й журнал значень не змінюються. Права — ті самі,
/// що на запис комірки; невидимий документ — та сама 404, що й на читання.
/// </remarks>
[Collection("SqlServer")]
public sealed class CellStylesApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-CellStyle-2026!";

    /// <summary>⚠ Не 2026-05…07 — ті архівують <c>ArchiveJobTests</c>.</summary>
    private const int PeriodKeyValue = 202611;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Писар_задає_колір_зріз_його_віддає_а_значення_й_версія_рядка_не_міняються()
    {
        var s = await ArrangeAsync(GrantLevel.Write).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        var before = await SliceAsync(client, s).ConfigureAwait(true);
        var versionBefore = RowVersion(before, s.RowKey);

        var response = await PatchStylesAsync(client, s, new
        {
            rowKey = s.RowKey,
            columnCode = s.ColumnCode,
            backgroundHex = "#ffcc99",
            foregroundHex = (string?)null,
            isBold = true,
        }).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Contains("\"appliedCells\":1", body, StringComparison.Ordinal);

        var after = await SliceAsync(client, s).ConfigureAwait(true);
        var key = $"{s.RowKey}:{s.ColumnCode}";
        var style = after.GetProperty("cellStyles").GetProperty(key);

        // Один регістр у базі (#RRGGBB), решта полів — як задано.
        Assert.Equal("#FFCC99", style.GetProperty("backgroundHex").GetString());
        Assert.Equal(JsonValueKind.Null, style.GetProperty("foregroundHex").ValueKind);
        Assert.True(style.GetProperty("isBold").GetBoolean());

        // ⛔ Колір — не дані: версія рядка та сама, значення комірки не з'явилося.
        Assert.Equal(versionBefore, RowVersion(after, s.RowKey));
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        Assert.False(await db.CellValues.AnyAsync(c => c.PeriodKeyValue == PeriodKeyValue && c.TableRowId == s.RowId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Часткова_зміна_лишає_решту_полів_а_reset_знімає_все_і_видаляє_рядок()
    {
        var s = await ArrangeAsync(GrantLevel.Write).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        await PatchStylesAsync(client, s, new
        {
            rowKey = s.RowKey,
            columnCode = s.ColumnCode,
            backgroundHex = "#FFFF00",
            isBold = true,
        }).ConfigureAwait(true);

        // Лише колір тексту: заливка й жирність лишаються (null = не чіпати).
        var partial = await PatchStylesAsync(client, s, new
        {
            rowKey = s.RowKey,
            columnCode = s.ColumnCode,
            foregroundHex = "#FF0000",
        }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, partial.StatusCode);

        var slice = await SliceAsync(client, s).ConfigureAwait(true);
        var style = slice.GetProperty("cellStyles").GetProperty($"{s.RowKey}:{s.ColumnCode}");
        Assert.Equal("#FFFF00", style.GetProperty("backgroundHex").GetString());
        Assert.Equal("#FF0000", style.GetProperty("foregroundHex").GetString());
        Assert.True(style.GetProperty("isBold").GetBoolean());

        var reset = await PatchStylesAsync(
            client, s, reset: true, new { rowKey = s.RowKey, columnCode = s.ColumnCode }).ConfigureAwait(true);
        var resetBody = await reset.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(reset.StatusCode == HttpStatusCode.OK, $"{reset.StatusCode}: {resetBody}");
        Assert.Contains("\"resetCells\":1", resetBody, StringComparison.Ordinal);

        var cleared = await SliceAsync(client, s).ConfigureAwait(true);
        Assert.False(cleared.GetProperty("cellStyles").TryGetProperty($"{s.RowKey}:{s.ColumnCode}", out _));

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        Assert.False(await db.CellStyles.AnyAsync(c => c.PeriodKeyValue == PeriodKeyValue && c.TableRowId == s.RowId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читач_без_гранта_Write_отримує_403_і_нічого_не_записано()
    {
        var s = await ArrangeAsync(GrantLevel.Read).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PatchStylesAsync(client, s, new
        {
            rowKey = s.RowKey,
            columnCode = s.ColumnCode,
            backgroundHex = "#FFFF00",
        }).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Contains("ECR-ACCS-0403", body, StringComparison.Ordinal);

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        Assert.False(await db.CellStyles.AnyAsync(c => c.PeriodKeyValue == PeriodKeyValue && c.TableRowId == s.RowId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Невидимий_документ_дає_404_як_на_читання()
    {
        var s = await ArrangeAsync(GrantLevel.Write, deny: true).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PatchStylesAsync(client, s, new
        {
            rowKey = s.RowKey,
            columnCode = s.ColumnCode,
            backgroundHex = "#FFFF00",
        }).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Contains("ECR-DOC-0404", body, StringComparison.Ordinal);
        Assert.DoesNotContain(s.ColumnCode, body, StringComparison.Ordinal);

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        Assert.False(await db.CellStyles.AnyAsync(c => c.PeriodKeyValue == PeriodKeyValue && c.TableRowId == s.RowId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Невідома_колонка_колір_не_у_формі_rrggbb_і_чужий_період_дають_422()
    {
        var s = await ArrangeAsync(GrantLevel.Write).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        var unknown = await PatchStylesAsync(client, s, new
        {
            rowKey = s.RowKey,
            columnCode = "NO_SUCH_COLUMN",
            backgroundHex = "#FFFF00",
        }).ConfigureAwait(true);
        var unknownBody = await unknown.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(
            unknown.StatusCode == HttpStatusCode.UnprocessableEntity, $"{unknown.StatusCode}: {unknownBody}\n{app.ErrorsText}");
        Assert.Contains("ECR-CELL-4224", unknownBody, StringComparison.Ordinal);

        var badColor = await PatchStylesAsync(client, s, new
        {
            rowKey = s.RowKey,
            columnCode = s.ColumnCode,
            backgroundHex = "red; background:url(x)",
        }).ConfigureAwait(true);
        var badBody = await badColor.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badColor.StatusCode);
        Assert.Contains("ECR-CELL-4224", badBody, StringComparison.Ordinal);

        var otherPeriod = await client.PatchAsJsonAsync(
            new Uri(
                $"/api/v1/documents/{s.Document.DocumentId}/tables/{s.Document.TableInstanceId}/cell-styles",
                UriKind.Relative),
            new
            {
                periodKey = PeriodKeyValue + 1,
                items = new[] { new { rowKey = s.RowKey, columnCode = s.ColumnCode, backgroundHex = "#FFFF00" } },
            }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, otherPeriod.StatusCode);
        Assert.Contains(
            "ECR-PRD-0422", await otherPeriod.Content.ReadAsStringAsync().ConfigureAwait(true), StringComparison.Ordinal);

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        Assert.False(await db.CellStyles.AnyAsync(c => c.PeriodKeyValue == PeriodKeyValue && c.TableRowId == s.RowId));
    }

    private static Task<HttpResponseMessage> PatchStylesAsync(HttpClient client, Scenario s, params object[] items)
        => PatchStylesAsync(client, s, reset: false, items);

    private static Task<HttpResponseMessage> PatchStylesAsync(
        HttpClient client, Scenario s, bool reset, params object[] items)
        => client.PatchAsJsonAsync(
            new Uri(
                $"/api/v1/documents/{s.Document.DocumentId}/tables/{s.Document.TableInstanceId}/cell-styles",
                UriKind.Relative),
            new { periodKey = PeriodKeyValue, items, reset });

    private static async Task<JsonElement> SliceAsync(HttpClient client, Scenario s)
    {
        var response = await client
            .GetAsync(new Uri(
                $"/api/v1/documents/{s.Document.DocumentId}/tables/{s.Document.TableInstanceId}", UriKind.Relative))
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"Зріз: {response.StatusCode}: {body}");

        using var json = JsonDocument.Parse(body);
        return json.RootElement.Clone();
    }

    private static string RowVersion(JsonElement slice, string rowKey)
        => slice.GetProperty("rows").EnumerateArray()
            .Single(r => r.GetProperty("rowKey").GetString() == rowKey)
            .GetProperty("rowVersion").GetString()!;

    private static async Task<HttpClient> SignInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private async Task<Scenario> ArrangeAsync(GrantLevel level, bool deny = false)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(PeriodKeyValue, columnCount: 2, rowCount: 2).ConfigureAwait(false);

        await using var db = builder.CreateContext();

        // Період відкритий: у Scheduled запис заборонений.
        var period = await db.Periods
            .FirstAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == PeriodKeyValue)
            .ConfigureAwait(false);
        period.AdvanceTo(PeriodState.Open, DateTime.UtcNow);

        var userName = $"csa_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"CSA_W_{Guid.NewGuid():N}"[..24]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "CSA role" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, level));

        if (deny)
        {
            var denier = new Role(
                EcrCode.Create($"CSA_D_{Guid.NewGuid():N}"[..24]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "CSA denier" }));
            db.Roles.Add(denier);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RoleAssignments.Add(new RoleAssignment(denier.Id, user.Id, null));
            db.ResourceGrants.Add(new ResourceGrant(
                denier.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Read, isDeny: true));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        var rowKey = await db.TableRows.AsNoTracking()
            .Where(r => r.Id == doc.RowIds[0])
            .Select(r => r.RowKeyValue)
            .SingleAsync().ConfigureAwait(false);

        var columnCode = await db.ColumnDefs.AsNoTracking()
            .Where(c => c.Id == doc.ColumnDefIds[0])
            .Select(c => c.Code)
            .SingleAsync().ConfigureAwait(false);

        return new Scenario(doc, userName, rowKey, doc.RowIds[0], columnCode);
    }

    private sealed record Scenario(TestDocument Document, string UserName, string RowKey, long RowId, string ColumnCode);
}
