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
/// Порядок РЯДКІВ конструктора (<c>ФВ-2.6</c>): <c>Id</c> рядка у структурі і
/// <c>RowDef.Ordinal</c> у презентаційному патчі — по HTTP на реальному SQL.
/// </summary>
[Collection("SqlServer")]
public sealed class PresentationRowOrderApiTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 30, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.6")]
    public async Task Структура_віддає_Id_рядків_а_патч_порядку_міняє_їхню_послідовність()
    {
        var draft = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Template.View", "Template.Edit");

        var before = await RowsAsync(client, draft.VersionId);
        Assert.Equal("R1,R2", string.Join(",", before.Select(r => r.Key)));
        Assert.Equal($"{draft.Row1},{draft.Row2}", string.Join(",", before.Select(r => r.Id)));

        var patch = await PatchAsync(client, draft.VersionId, new object[]
        {
            new { entityType = "RowDef", entityId = draft.Row1, field = "Ordinal", value = "2" },
            new { entityType = "RowDef", entityId = draft.Row2, field = "Ordinal", value = "1" },
        });
        Assert.True(patch.StatusCode == HttpStatusCode.OK, await patch.Content.ReadAsStringAsync());

        var after = await RowsAsync(client, draft.VersionId);
        Assert.Equal("R2,R1", string.Join(",", after.Select(r => r.Key)));
        Assert.Equal("1,2", string.Join(",", after.Select(r => r.Ordinal)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.6")]
    public async Task Невалідний_порядок_рядка_дає_422_з_ключем_повідомлення()
    {
        var draft = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Template.View", "Template.Edit");

        var response = await PatchAsync(client, draft.VersionId, new object[]
        {
            new { entityType = "RowDef", entityId = draft.Row1, field = "Ordinal", value = "abc" },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("err.ECR-TMPL-0422.ordinalInvalid", problem.RootElement.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.6")]
    public async Task Без_права_Template_Edit_порядок_рядка_не_міняється()
    {
        var draft = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Template.View");

        var response = await PatchAsync(client, draft.VersionId, new object[]
        {
            new { entityType = "RowDef", entityId = draft.Row1, field = "Ordinal", value = "9" },
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var db = Context();
        Assert.Equal(1, (await db.RowDefs.AsNoTracking().SingleAsync(r => r.Id == draft.Row1)).Ordinal);
    }

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, int versionId, object[] body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Patch, new Uri($"/api/v1/template-versions/{versionId}/presentation", UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        return client.SendAsync(request);
    }

    private static async Task<List<(int Id, string Key, int Ordinal)>> RowsAsync(HttpClient client, int versionId)
    {
        var body = await client.GetFromJsonAsync<JsonElement>(
            new Uri($"/api/v1/template-versions/{versionId}/structure", UriKind.Relative));
        var table = body.GetProperty("sheets")[0].GetProperty("tables")[0];
        return table.GetProperty("rows").EnumerateArray()
            .Select(r => (r.GetProperty("id").GetInt32(), r.GetProperty("rowKey").GetString()!, r.GetProperty("ordinal").GetInt32()))
            .ToList();
    }

    private async Task<Draft> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = Context();

        var template = new Template(EcrCode.Create($"RO{tag}"), Name("RO"), 1, Now);
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

        db.ColumnDefs.Add(new ColumnDef(table.Id, EcrCode.Create("CDEC"), Name("CDEC"), 1, CellDataType.Decimal));
        var row1 = new RowDef(table.Id, RowKey.Create("R1"), 1, Name("R1"), RowKind.Item);
        var row2 = new RowDef(table.Id, RowKey.Create("R2"), 2, Name("R2"), RowKind.Item);
        db.RowDefs.AddRange(row1, row2);
        await db.SaveChangesAsync();

        return new Draft(version.Id, row1.Id, row2.Id);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Draft(int VersionId, int Row1, int Row2);
}
