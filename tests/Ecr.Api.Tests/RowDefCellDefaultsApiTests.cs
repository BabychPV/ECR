// tests/Ecr.Api.Tests/RowDefCellDefaultsApiTests.cs
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
/// ✎ B175: <c>PUT /api/v1/template-versions/{id}/tables/{tableId}/rows/{code}</c> з <c>cellDefaults</c> — по HTTP на
/// реальному SQL: придатний дефолт пишеться в <c>cfg.RowDef.CellDefaultsJson</c>, непридатний — <c>422</c> з ключем.
/// </summary>
/// <remarks>Мутація: прибрати розбір значення в <c>SaveRowDefHandler.ValidateCellDefaultsAsync</c> — 422 стає 200.</remarks>
[Collection("SqlServer")]
public sealed class RowDefCellDefaultsApiTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B175")]
    public async Task Дефолт_рядка_пишеться_а_непридатний_дає_422_з_ключем()
    {
        var (versionId, tableId) = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Template.View", "Template.Edit");

        using var bad = await PutAsync(client, versionId, tableId, "R1", new Dictionary<string, string> { ["CDEC"] = "abc" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        using (var problem = JsonDocument.Parse(await bad.Content.ReadAsStringAsync()))
        {
            Assert.Equal("err.ECR-TMPL-0422.rowDefaultValueInvalid", problem.RootElement.GetProperty("messageKey").GetString());
        }

        using var ok = await PutAsync(client, versionId, tableId, "R1", new Dictionary<string, string> { ["CDEC"] = "2,5" });
        Assert.True(ok.StatusCode == HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        using (var body = JsonDocument.Parse(await ok.Content.ReadAsStringAsync()))
        {
            Assert.Equal("2,5", body.RootElement.GetProperty("cellDefaults").GetProperty("CDEC").GetString());
        }

        await using var db = Context();
        var row = await db.RowDefs.AsNoTracking().SingleAsync(r => r.TableDefId == tableId && r.RowKeyValue == "R1");
        Assert.Equal("""{"CDEC":"2,5"}""", row.CellDefaultsJson);
    }

    private static Task<HttpResponseMessage> PutAsync(
        HttpClient client, int versionId, int tableId, string code, Dictionary<string, string> defaults)
        => client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/tables/{tableId}/rows/{code}", UriKind.Relative),
            new
            {
                labelL10n = new Dictionary<string, string> { ["en"] = code },
                ordinal = (int?)null,
                rowKind = "Item",
                parentRowKey = (string?)null,
                isReadOnly = false,
                cellDefaults = defaults,
            });

    private async Task<(int VersionId, int TableId)> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = Context();

        var template = new Template(EcrCode.Create($"RD{tag}"), Name("RD"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"S{tag}"), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"T{tag}"), Name("Table"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        db.ColumnDefs.Add(new ColumnDef(table.Id, EcrCode.Create("CDEC"), Name("CDEC"), 1, CellDataType.Decimal));
        await db.SaveChangesAsync();

        return (version.Id, table.Id);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
