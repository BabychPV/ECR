using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Умовне форматування версії шаблону по HTTP на реальному SQL
/// (<c>GET/PUT /api/v1/template-versions/{id}/conditional-formats</c>,
/// сценарії Н-Е2/Н-Е3 у <c>docs/build/TESTER-SCENARIOS-2026-10-01.md</c>):
/// <c>ETag</c>/<c>If-Match</c>, ключі помилок валідації, право <c>Template.Edit</c>,
/// заморожена версія.
/// </summary>
/// <remarks>
/// <para><b>Мутаційні докази</b> (кожна мутація — у продукті, тест має почервоніти):</para>
/// <list type="bullet">
/// <item><c>Збережене_правило_повертається_GET_а_ETag_змінюється</c> —
/// <c>src/Ecr.Application/Templates/ConditionalFormatHandlers.cs</c>, рядок 136:
/// <c>await rules.ReplaceAsync(...)</c> закоментувати → <c>GET</c> повертає порожній набір.</item>
/// <item><c>PUT_без_If_Match_дає_422_а_застарілий_If_Match_дає_409</c> — той самий файл, рядок 123:
/// <c>if (!string.Equals(actual, expected, …))</c> → <c>if (string.Equals(actual, expected, …))</c>
/// → застарілий <c>If-Match</c> дає <c>200</c> і затирає чуже правило.</item>
/// <item><c>Невалідне_правило_дає_422_з_точним_ключем</c> — кожен випадок окремо:
/// <c>ConditionalFormatRule.cs</c> рядок 83 ключ <c>condFormatOperator</c> → <c>condFormatOperand</c>;
/// рядок 87 <c>needed &gt;= 1</c> → <c>needed &gt;= 3</c> (операнд <c>abc</c> проходить);
/// рядок 101 ключ <c>condFormatColor</c> → <c>condFormatOperand</c>;
/// <c>ConditionalFormatHandlers.cs</c> рядок 101 ключ <c>condFormatColumn</c> → <c>condFormatOperand</c>;
/// рядок 74 <c>requested.Count &gt; MaxRules</c> → <c>requested.Count &gt; MaxRules + 1</c>.</item>
/// <item><c>Без_права_Template_Edit_PUT_дає_403_і_нічого_не_пише</c> —
/// <c>ConditionalFormatHandlers.cs</c> рядок 60 (<c>PermissionCheck.RequireAsync</c>) закоментувати
/// → <c>200</c>.</item>
/// <item><c>PUT_на_опублікованій_версії_дає_409_structurallyFrozen</c> — захист подвійний, тож
/// мутація двоточкова: <c>ConditionalFormatHandlers.cs</c> рядок 72
/// (<c>version.EnsureStructurallyMutable()</c>) і рядок 116
/// (<c>DraftVersionLock.EnsureDraftUnderLockAsync</c>) закоментувати → відповідь не
/// <c>409 structurallyFrozen</c> (доходить до тригера БД). Одна з двох точок — тест зелений,
/// і це очікувано.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class ConditionalFormatsApiTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    private const string Column = "CDEC";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.7")]
    [Trait("Scenario", "Н-Е2")]
    [Trait("Scenario", "Н-Е3")]
    public async Task Збережене_правило_повертається_GET_а_ETag_змінюється()
    {
        var versionId = await ArrangeAsync(publish: false);
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Template.View", "Template.Edit");

        var (empty, etag0) = await GetAsync(client, versionId);
        Assert.Empty(empty);
        Assert.False(string.IsNullOrWhiteSpace(etag0), "GET не віддав ETag — If-Match неможливий.");

        using var put = await PutAsync(client, versionId, etag0, Rule("gt", "100", "#ffcccc", isBold: true));
        Assert.True(put.StatusCode == HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var etag1 = put.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrWhiteSpace(etag1));
        Assert.NotEqual(etag0, etag1);

        var (saved, etag2) = await GetAsync(client, versionId);
        var rule = Assert.Single(saved);
        Assert.Equal(Column, rule.GetProperty("columnCode").GetString());
        Assert.Equal("gt", rule.GetProperty("operator").GetString());
        Assert.Equal("100", rule.GetProperty("value").GetString());
        Assert.Equal("#ffcccc", rule.GetProperty("backgroundHex").GetString());
        Assert.True(rule.GetProperty("isBold").GetBoolean());
        Assert.Equal(etag1, etag2);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.7")]
    [Trait("Scenario", "Н-Е2")]
    [Trait("Scenario", "Н-Е3")]
    public async Task PUT_без_If_Match_дає_422_а_застарілий_If_Match_дає_409()
    {
        var versionId = await ArrangeAsync(publish: false);
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Template.View", "Template.Edit");

        var (_, etag0) = await GetAsync(client, versionId);
        using (var first = await PutAsync(client, versionId, etag0, Rule("gt", "100", "#ffcccc")))
        {
            Assert.True(first.StatusCode == HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        }

        using var noIfMatch = await PutAsync(client, versionId, ifMatch: null, Rule("lt", "5", "#00ff00"));
        await AssertProblemAsync(noIfMatch, HttpStatusCode.UnprocessableEntity, "err.ECR-REQ-0422.condFormatIfMatch");

        // Друга вкладка: правка почалася з набору ДО першого збереження.
        using var stale = await PutAsync(client, versionId, etag0, Rule("lt", "5", "#00ff00"));
        await AssertProblemAsync(stale, HttpStatusCode.Conflict, "err.ECR-TMPL-0409.condFormatChanged");

        var (rules, _) = await GetAsync(client, versionId);
        var rule = Assert.Single(rules);
        Assert.Equal("gt", rule.GetProperty("operator").GetString());
        Assert.Equal("100", rule.GetProperty("value").GetString());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.7")]
    [Trait("Scenario", "Н-Е2")]
    [InlineData("bigger", "100", "#ffcccc", Column, 1, "err.ECR-CFG-0422.condFormatOperator")]
    [InlineData("gt", "abc", "#ffcccc", Column, 1, "err.ECR-CFG-0422.condFormatOperand")]
    [InlineData("gt", "100", "red", Column, 1, "err.ECR-CFG-0422.condFormatColor")]
    [InlineData("gt", "100", "#ffcccc", "NOPE", 1, "err.ECR-CFG-0422.condFormatColumn")]
    [InlineData("gt", "100", "#ffcccc", Column, 501, "err.ECR-CFG-0422.condFormatLimit")]
    public async Task Невалідне_правило_дає_422_з_точним_ключем(
        string op, string value, string background, string columnCode, int count, string messageKey)
    {
        var versionId = await ArrangeAsync(publish: false);
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Template.View", "Template.Edit");

        var (_, etag) = await GetAsync(client, versionId);
        var rules = Enumerable.Range(0, count)
            .Select(_ => Rule(op, value, background, columnCode: columnCode))
            .ToArray();

        using var response = await PutAsync(client, versionId, etag, rules);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, messageKey);
        Assert.Equal(0, await StoredCountAsync(versionId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.7")]
    [Trait("Scenario", "Н-Е2")]
    public async Task Без_права_Template_Edit_PUT_дає_403_і_нічого_не_пише()
    {
        var versionId = await ArrangeAsync(publish: false);
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Template.View");

        var (_, etag) = await GetAsync(client, versionId);
        using var response = await PutAsync(client, versionId, etag, Rule("gt", "100", "#ffcccc"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await StoredCountAsync(versionId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.7")]
    [Trait("Scenario", "Н-Е2")]
    public async Task PUT_на_опублікованій_версії_дає_409_structurallyFrozen()
    {
        var versionId = await ArrangeAsync(publish: true);
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Template.View", "Template.Edit");

        var (_, etag) = await GetAsync(client, versionId);
        using var response = await PutAsync(client, versionId, etag, Rule("gt", "100", "#ffcccc"));

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "err.ECR-TMPL-0409.structurallyFrozen");
        Assert.Equal(0, await StoredCountAsync(versionId));
    }

    private static object Rule(
        string op, string value, string background, bool isBold = false, string columnCode = Column)
        => new
        {
            columnCode,
            @operator = op,
            value,
            valueTo = (string?)null,
            backgroundHex = background,
            foregroundHex = (string?)null,
            isBold,
        };

    private static async Task<(List<JsonElement> Rules, string? ETag)> GetAsync(HttpClient client, int versionId)
    {
        using var response = await client.GetAsync(At(versionId));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rules = body.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
        return (rules, response.Headers.ETag?.Tag);
    }

    private static Task<HttpResponseMessage> PutAsync(
        HttpClient client, int versionId, string? ifMatch, params object[] rules)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, At(versionId))
        {
            Content = JsonContent.Create(new { rules }),
        };

        if (ifMatch is not null)
        {
            // ETag уже в лапках — як віддав сервер.
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return client.SendAsync(request);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string messageKey)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {text}");
        using var problem = JsonDocument.Parse(text);
        Assert.Equal(messageKey, problem.RootElement.GetProperty("messageKey").GetString());
    }

    private static Uri At(int versionId)
        => new($"/api/v1/template-versions/{versionId}/conditional-formats", UriKind.Relative);

    private async Task<int> StoredCountAsync(int versionId)
    {
        await using var db = Context();
        return await db.ConditionalFormatRules.AsNoTracking().CountAsync(r => r.TemplateVersionId == versionId);
    }

    private async Task<int> ArrangeAsync(bool publish)
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = Context();

        var template = new Template(EcrCode.Create($"CF{tag}"), Name("CF"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"S{tag}"), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"T{tag}"), Name("Table"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        db.ColumnDefs.Add(new ColumnDef(table.Id, EcrCode.Create(Column), Name(Column), 1, CellDataType.Decimal));
        db.RowDefs.Add(new RowDef(table.Id, RowKey.Create("R1"), 1, Name("R1"), RowKind.Item));
        await db.SaveChangesAsync();

        if (publish)
        {
            version.Publish(1, Now);
            await db.SaveChangesAsync();
        }

        return version.Id;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
