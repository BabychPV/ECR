// tests/Ecr.Api.Tests/CellFormulaAuthoringApiTests.cs
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
/// D1: формула КОМІРКИ (<c>Scope = Cell</c>) пишеться й знімається через
/// <c>PUT/DELETE …/formulas/cell/{rowKey}/{columnDefId}</c> — крізь справжній
/// хост, конвеєр помилок і базу (<c>CK_Formula_Scope</c> бачить обидва ключі).
/// </summary>
[Collection("SqlServer")]
public sealed class CellFormulaAuthoringApiTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Формула_комірки_створюється_оновлюється_і_знімається_м_яко()
    {
        var draft = await ArrangeAsync();

        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Template.View", "Template.Edit");

        var url = new Uri(
            $"/api/v1/template-versions/{draft.VersionId}/tables/{draft.TableId}/formulas/cell/R1/{draft.FormulaColumnId}",
            UriKind.Relative);

        using var created = await client.PutAsJsonAsync(url, new { dialect = "Template", expression = "[CDEC] / 1000" });
        Assert.True(created.StatusCode == HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        var formulaId = createdBody.GetProperty("id").GetInt32();
        Assert.Equal(draft.FormulaColumnId, createdBody.GetProperty("columnDefId").GetInt32());
        Assert.Equal(draft.RowId, createdBody.GetProperty("rowDefId").GetInt32());

        // Повторний запис тієї самої комірки — та сама формула, а не друга.
        using var resaved = await client.PutAsJsonAsync(url, new { dialect = "Template", expression = "[CDEC] / 1000 + 1" });
        Assert.Equal(HttpStatusCode.OK, resaved.StatusCode);
        var resavedBody = await resaved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(formulaId, resavedBody.GetProperty("id").GetInt32());

        await using (var db = Context())
        {
            var stored = await db.FormulaDefs.SingleAsync(f => f.TableDefId == draft.TableId);
            Assert.Equal(FormulaScope.Cell, stored.Scope);
            Assert.Equal("[CDEC] / 1000 + 1", stored.Expression);
            Assert.False(stored.IsDeleted);
        }

        using var removed = await client.DeleteAsync(url);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        await using (var db = Context())
        {
            Assert.True((await db.FormulaDefs.SingleAsync(f => f.TableDefId == draft.TableId)).IsDeleted);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Формула_комірки_на_ручній_колонці_відхиляється_а_неіснуюча_адреса_дає_404()
    {
        var draft = await ArrangeAsync();

        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Template.View", "Template.Edit");

        var body = new { dialect = "Template", expression = "[CDEC] / 1000" };

        using var manual = await client.PutAsJsonAsync(
            new Uri(
                $"/api/v1/template-versions/{draft.VersionId}/tables/{draft.TableId}/formulas/cell/R1/{draft.ManualColumnId}",
                UriKind.Relative),
            body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, manual.StatusCode);
        var problem = await manual.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ECR-TMPL-4227", problem.GetProperty("errorCode").GetString());

        using var noRow = await client.PutAsJsonAsync(
            new Uri(
                $"/api/v1/template-versions/{draft.VersionId}/tables/{draft.TableId}/formulas/cell/NOPE/{draft.FormulaColumnId}",
                UriKind.Relative),
            body);
        Assert.Equal(HttpStatusCode.NotFound, noRow.StatusCode);

        await using var db = Context();
        Assert.False(await db.FormulaDefs.AnyAsync(f => f.TableDefId == draft.TableId));
    }

    private async Task<Draft> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = Context();

        var template = new Template(EcrCode.Create($"D1C{tag}"), Name("D1"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"S{tag}"), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"FT{tag}"), Name("FT"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var dec = new ColumnDef(table.Id, EcrCode.Create("CDEC"), Name("CDEC"), 1, CellDataType.Decimal);
        var frm = new ColumnDef(table.Id, EcrCode.Create("CFRM"), Name("CFRM"), 2, CellDataType.Formula);
        db.ColumnDefs.AddRange(dec, frm);
        var row = new RowDef(table.Id, RowKey.Create("R1"), 1, Name("R1"), RowKind.Item);
        db.RowDefs.Add(row);
        await db.SaveChangesAsync();

        return new Draft(version.Id, table.Id, frm.Id, dec.Id, row.Id);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Draft(int VersionId, int TableId, int FormulaColumnId, int ManualColumnId, int RowId);
}
