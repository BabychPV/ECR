// tests/Ecr.Api.Tests/PermitBindingsHttpTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Прив'язки аркуша до дозволів на HTTP (Permits, PM-09): статуси й форма відповідей, які обробниковий тест не бачить -
/// відображення <c>ECR-PRM-4101</c> у <c>409</c> через <c>ExceptionHandlingMiddleware</c>, <c>409</c> за типом винятку для
/// <c>ECR-SCHM-0409</c>, <c>404</c> для <c>ECR-PRM-0404</c>, <c>422</c> з <c>caseKeys</c>.
/// </summary>
/// <remarks>
/// Мутаційний доказ: змінити <c>Permission</c> у <c>SavePermitBindingHandler</c> на <c>Template.View</c> - червоний
/// «Без_права_Template_Edit…» (запис дав би 200). ⚠ Арм <c>PermitBindingDuplicate</c> окремо не доводиться: правило
/// суфікса коду (<c>…-4101</c> → 409) дає той самий статус - арм лише називає намір, як і для <c>ECR-PRM-4091</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitBindingsHttpTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повний_цикл_запис_перелік_читання_перегляд_дублікат_422_404_і_відмова_видалення_з_документами()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Permit.EditType", "Permit.PublishType", "Template.View", "Template.Edit").ConfigureAwait(true);
        await PublishTypeAsync(client).ConfigureAwait(true);
        var stand = await StandAsync().ConfigureAwait(true);
        var root = $"/api/v1/template-versions/{stand.VersionId}/permit-bindings";

        // Порожній перелік - норма; версія-чернетка редагована.
        var empty = await JsonAsync(await client.GetAsync(new Uri(root, UriKind.Relative))).ConfigureAwait(true);
        Assert.True(empty.GetProperty("isEditable").GetBoolean());
        Assert.Equal(0, empty.GetProperty("items").GetArrayLength());

        // Запис: 200 і форма відповіді.
        var saved = await client.PutAsJsonAsync(new Uri($"{root}/B{_tag}", UriKind.Relative), Body(stand, limitColumn: stand.LimitColumnId));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var dto = await JsonAsync(saved).ConfigureAwait(true);
        Assert.Equal(("Rows", "ByFacilityPeriodEnd", $"B{_tag}"), (dto.GetProperty("mode").GetString(), dto.GetProperty("selectionRule").GetString(), dto.GetProperty("code").GetString()));
        Assert.Equal(["RowKey", "Limit"], dto.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("role").GetString()));

        // Друга прив'язка на ту саму таблицю - 409 ECR-PRM-4101.
        var duplicate = await client.PutAsJsonAsync(new Uri($"{root}/C{_tag}", UriKind.Relative), Body(stand, limitColumn: stand.LimitColumnId));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var conflict = await JsonAsync(duplicate).ConfigureAwait(true);
        Assert.Equal(("ECR-PRM-4101", "err.ECR-PRM-4101.sheet"), (conflict.GetProperty("errorCode").GetString(), conflict.GetProperty("messageKey").GetString()));

        // Некоректна: редагована колонка під ліміт - 422 ECR-PRM-0422 із переліком випадків.
        var invalid = await client.PutAsJsonAsync(new Uri($"{root}/D{_tag}", UriKind.Relative), Body(stand, limitColumn: stand.EditableColumnId, tableId: stand.SecondTableId));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        var problem = await JsonAsync(invalid).ConfigureAwait(true);
        Assert.Equal("ECR-PRM-0422", problem.GetProperty("errorCode").GetString());
        Assert.Contains("bindingNotReadOnly", problem.GetProperty("caseKeys").GetString()!.Split(','));

        // Читання, перелік і 404.
        var one = await client.GetAsync(new Uri($"{root}/B{_tag}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal(1, (await JsonAsync(await client.GetAsync(new Uri(root, UriKind.Relative))).ConfigureAwait(true)).GetProperty("items").GetArrayLength());
        var missing = await client.GetAsync(new Uri($"{root}/NOPE", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("ECR-PRM-0404", (await JsonAsync(missing).ConfigureAwait(true)).GetProperty("errorCode").GetString());

        // Перегляд: дозвіл не знайдено - 200 із причиною, а не помилка.
        var preview = await client.GetAsync(new Uri($"{root}/B{_tag}/preview?facilityName=Nowhere&periodEnd=2026-06-30", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var shown = await JsonAsync(preview).ConfigureAwait(true);
        Assert.Equal("noPermit", shown.GetProperty("permit").GetProperty("reasonIfNone").GetString());
        Assert.Equal(0, shown.GetProperty("rows").GetArrayLength());
        Assert.Equal(["RowKey", "Limit", "Fact"], shown.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("role").GetString()));

        // У версії є документ: видалення - Breaking, 409 ECR-SCHM-0409 (статус - за типом винятку).
        var deleted = await client.DeleteAsync(new Uri($"{root}/B{_tag}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
        Assert.Equal("ECR-SCHM-0409", (await JsonAsync(deleted).ConfigureAwait(true)).GetProperty("errorCode").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_права_Template_Edit_запис_і_видалення_дають_403_а_читання_працює()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Template.View").ConfigureAwait(true);
        var stand = await StandAsync().ConfigureAwait(true);
        var root = $"/api/v1/template-versions/{stand.VersionId}/permit-bindings";

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri(root, UriKind.Relative))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync(new Uri($"{root}/B{_tag}", UriKind.Relative), Body(stand, stand.LimitColumnId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(new Uri($"{root}/B{_tag}", UriKind.Relative))).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перегляд_без_Registry_View_дає_403_Registry_View_а_з_ним_200()
    {
        using var app = new EcrApiFactory(sql);
        using var admin = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Permit.EditType", "Permit.PublishType", "Template.View", "Template.Edit").ConfigureAwait(true);
        await PublishTypeAsync(admin).ConfigureAwait(true);
        var stand = await StandAsync().ConfigureAwait(true);
        var root = $"/api/v1/template-versions/{stand.VersionId}/permit-bindings";
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(new Uri($"{root}/B{_tag}", UriKind.Relative), Body(stand, stand.LimitColumnId))).StatusCode);
        var url = new Uri($"{root}/B{_tag}/preview?facilityName=Nowhere&periodEnd=2026-06-30", UriKind.Relative);

        using var reader = await SystemHealthControllerTests.SignedInAsync(sql, app, "Template.View", "Document.View").ConfigureAwait(true);
        var denied = await reader.GetAsync(url);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var problem = await JsonAsync(denied).ConfigureAwait(true);
        Assert.Equal("err.ECR-AUTH-0403.permission", problem.GetProperty("messageKey").GetString());
        Assert.False(problem.TryGetProperty("rows", out _) || problem.TryGetProperty("permit", out _), "відмова не несе даних перегляду");

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(url)).StatusCode);
    }

    /// <summary>
    /// PM-09b: правку структури аркуша тримає прив'язка - <c>409 ECR-PRM-4102</c> (арм у <c>ExceptionHandlingMiddleware</c>)
    /// на <c>Fixed</c>, видалення таблиці й видалення зіставленої колонки; вільна колонка видаляється (204).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прив_язка_тримає_правку_аркуша_Fixed_видалення_таблиці_і_зіставленої_колонки_дають_409_ECR_PRM_4102()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Permit.EditType", "Permit.PublishType", "Template.View", "Template.Edit").ConfigureAwait(true);
        await PublishTypeAsync(client).ConfigureAwait(true);
        var open = await OpenStandAsync().ConfigureAwait(true);
        var version = $"/api/v1/template-versions/{open.VersionId}";
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PutAsJsonAsync(
                new Uri($"{version}/permit-bindings/B{_tag}", UriKind.Relative),
                Body(new Stand(open.VersionId, open.TableId, 0, open.KeyColumnId, open.LimitColumnId, 0), open.LimitColumnId))).StatusCode);

        var fixedMode = await client.PutAsJsonAsync(
            new Uri($"{version}/sheets/{open.SheetCode}/tables/{open.TableCode}", UriKind.Relative),
            new { nameL10n = new { en = "T" }, ordinal = (int?)null, layoutKind = "PerPeriodInstance", rowMode = "Fixed", maxDynamicRows = (int?)null });
        await AssertHeldAsync(fixedMode, "rowMode").ConfigureAwait(true);

        await AssertHeldAsync(
            await client.DeleteAsync(new Uri($"{version}/sheets/{open.SheetCode}/tables/{open.TableCode}", UriKind.Relative)), "table").ConfigureAwait(true);
        await AssertHeldAsync(
            await client.DeleteAsync(new Uri($"{version}/tables/{open.TableId}/columns/{open.KeyColumnCode}", UriKind.Relative)), "column").ConfigureAwait(true);

        var free = await client.DeleteAsync(new Uri($"{version}/tables/{open.TableId}/columns/{open.FreeColumnCode}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, free.StatusCode);
    }

    private static async Task AssertHeldAsync(HttpResponseMessage response, string kind)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await JsonAsync(response).ConfigureAwait(false);
        Assert.Equal(("ECR-PRM-4102", $"err.ECR-PRM-4102.{kind}"), (body.GetProperty("errorCode").GetString(), body.GetProperty("messageKey").GetString()));
    }

    /// <summary>Шаблон-чернетка БЕЗ документів (структурні зміни не <c>Breaking</c>): одна таблиця, три колонки, шапка з <c>Facility</c>.</summary>
    private async Task<OpenStand> OpenStandAsync()
    {
        var name = new LocalizedText(new Dictionary<string, string> { ["en"] = "N" });
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        var template = new Template(EcrCode.Create($"OPT{_tag}"), name, 1, now);
        db.Templates.Add(template);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var version = new TemplateVersion(template.Id, $"1.0.0.{_tag}", 1, now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var sheet = new SheetDef(version.Id, EcrCode.Create($"OSH{_tag}"), name, 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var table = new TableDef(sheet.Id, EcrCode.Create($"OTB{_tag}"), name, 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var key = new ColumnDef(table.Id, EcrCode.Create($"K_{_tag}"), name, 1, CellDataType.String);
        var limit = new ColumnDef(table.Id, EcrCode.Create($"L_{_tag}"), name, 2, CellDataType.Decimal);
        limit.SetReadOnly(true);
        limit.SetNumericFormat(12, 3);
        var free = new ColumnDef(table.Id, EcrCode.Create($"F_{_tag}"), name, 3, CellDataType.String);
        db.ColumnDefs.AddRange(key, limit, free);
        db.HeaderFieldDefs.Add(new HeaderFieldDef(version.Id, EcrCode.Create("Facility"), name, 1, CellDataType.String));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new OpenStand(version.Id, sheet.Code, table.Id, table.Code, key.Id, key.Code, limit.Id, free.Code);
    }

    private object Body(Stand stand, int limitColumn, int? tableId = null) => new
    {
        sheetTableDefId = tableId ?? stand.TableId,
        permitTypeCode = $"AIR{_tag}",
        permitTableCode = "Emissions",
        mode = "Rows",
        selectionRule = "ByFacilityPeriodEnd",
        headerFieldDefId = (int?)null,
        isActive = true,
        columns = new object[]
        {
            new { role = "RowKey", permitFieldCode = "Substance", sheetColumnDefId = stand.KeyColumnId },
            new { role = "Limit", permitFieldCode = "Limit", sheetColumnDefId = limitColumn },
        },
    };

    private async Task PublishTypeAsync(HttpClient client)
    {
        var code = $"AIR{_tag}";
        var created = await client.PostAsJsonAsync(
            new Uri("/api/v1/permit-types", UriKind.Relative), new { code, nameL10n = new { en = "Air" }, category = "Air" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var rowVersion = (await JsonAsync(created).ConfigureAwait(true)).GetProperty("rowVersion").GetString();

        object Col(string name, string type, bool key, int? precision = null, int? scale = null) => new
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
            });
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var draft = (await JsonAsync(replaced).ConfigureAwait(true)).GetProperty("rowVersion").GetString();

        var published = await client.PostAsJsonAsync(
            new Uri($"/api/v1/permit-types/{code}/publish", UriKind.Relative), new { reason = "випуск для прив'язок", rowVersion = draft });
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
    }

    /// <summary>Шаблон-чернетка з документом: дві таблиці, ліміт - лише для читання, шапка з <c>Facility</c>.</summary>
    private async Task<Stand> StandAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowCount: 0, rowMode: TableRowMode.Dynamic, ct: default).ConfigureAwait(false);

        await using var db = builder.CreateContext();
        var limit = await db.ColumnDefs.SingleAsync(c => c.Id == doc.ColumnDefIds[1]).ConfigureAwait(false);
        limit.SetReadOnly(true);
        limit.SetNumericFormat(12, 3);
        db.HeaderFieldDefs.Add(new HeaderFieldDef(
            doc.TemplateVersionId, EcrCode.Create("Facility"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Facility" }), 1, CellDataType.String));
        var second = new TableDef(
            doc.SheetDefId, EcrCode.Create($"TB2_{_tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "B" }), 2,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        db.TableDefs.Add(second);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // Копія колонок у другій таблиці: ліміт редагований - саме її бере тест відмови.
        var key2 = new ColumnDef(second.Id, EcrCode.Create($"K2_{_tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "K" }), 1, CellDataType.String);
        var editable = new ColumnDef(second.Id, EcrCode.Create($"E2_{_tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "E" }), 2, CellDataType.Decimal);
        editable.SetNumericFormat(12, 3);
        db.ColumnDefs.AddRange(key2, editable);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Stand(doc.TemplateVersionId, doc.TableDefId, second.Id, doc.ColumnDefIds[0], doc.ColumnDefIds[1], editable.Id);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;

    private sealed record OpenStand(
        int VersionId, string SheetCode, int TableId, string TableCode, int KeyColumnId, string KeyColumnCode, int LimitColumnId, string FreeColumnCode);

    private sealed record Stand(int VersionId, int TableId, int SecondTableId, int KeyColumnId, int LimitColumnId, int EditableColumnId);
}
