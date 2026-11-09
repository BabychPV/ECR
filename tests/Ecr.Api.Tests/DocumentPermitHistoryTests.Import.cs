// tests/Ecr.Api.Tests/DocumentPermitHistoryTests.Import.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Feature (j) j3 through Excel import: a workbook value for a month the row shares between two permits is refused with
/// <c>ECR-CELL-4225</c> (coordinator 09.11: «Excel import into an unsplit month refused») — the import goes through the
/// same cell write (origin <c>Import</c>), and the stored value stays.
/// </summary>
/// <remarks>Red proof: without <c>EnsureSharedMonthsAreSplitAsync</c> the apply stores 30 (200).</remarks>
public sealed partial class DocumentPermitHistoryTests
{
    private static readonly JsonSerializerOptions MapJson = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "ACC-1108-j3")]
    public async Task Excel_import_of_one_value_into_a_shared_month_is_refused_with_split_required()
    {
        var s = await ArrangeAsync(rowBase: "year").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, (await AddRowAsync(client, s, s.MidJanId, "2026-01-15").ConfigureAwait(true)).StatusCode);

        var (status, body) = await ImportAsync(app, client, s).ConfigureAwait(true);

        Assert.True(status == HttpStatusCode.UnprocessableEntity, $"apply: {status}\n{body}\n{app.ErrorsText}");
        Assert.Equal("ECR-CELL-4225", JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(12m, await CellAsync(s).ConfigureAwait(true));
    }

    /// <summary>
    /// Rule (h) through Excel import (coordinator 09.11: «entered/imported values count»): the row's permit FEB is valid
    /// from 01.02, the workbook writes January — refused exactly like the grid write (ECR-CELL-4223 permitNotCoveringMonths).
    /// Red before: the workbook path never ran the rule — apply 200, 30 stored.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "ACC-1108-h3")]
    public async Task Excel_import_into_a_month_the_permit_does_not_cover_is_refused_like_the_grid()
    {
        var s = await ArrangeAsync(rowBase: "feb").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var (status, body) = await ImportAsync(app, client, s).ConfigureAwait(true);

        Assert.True(status == HttpStatusCode.UnprocessableEntity, $"apply: {status}\n{body}\n{app.ErrorsText}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-CELL-4223", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-CELL-4223.permitNotCoveringMonths", problem.GetProperty("messageKey").GetString());
        Assert.Equal(12m, await CellAsync(s).ConfigureAwait(true));
    }

    private async Task<(HttpStatusCode Status, string Body)> ImportAsync(EcrApiFactory app, HttpClient client, Scenario s)
    {
        var document = s.DocumentId.ToString(CultureInfo.InvariantCulture);
        using var content = new MultipartFormDataContent();
        using var file = new ByteArrayContent(await BookAsync(s).ConfigureAwait(true));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(file, "file", "book.xlsx");
        using var preview = await client.PostAsync(new Uri($"/api/v1/documents/{document}/import/preview", UriKind.Relative), content)
            .ConfigureAwait(true);
        var previewBody = await preview.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(preview.StatusCode == HttpStatusCode.OK, $"preview: {preview.StatusCode}\n{previewBody}\n{app.ErrorsText}");
        var token = JsonDocument.Parse(previewBody).RootElement.GetProperty("previewToken").GetString();

        using var applied = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{document}/import/apply", UriKind.Relative), new { previewToken = token })
            .ConfigureAwait(true);
        return (applied.StatusCode, await applied.Content.ReadAsStringAsync().ConfigureAwait(true));
    }

    /// <summary>A workbook of the document's table: C2 of the row = 30 (the map sheet `_ecr` addresses the cell).</summary>
    private async Task<byte[]> BookAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var instance = await db.TableInstances.AsNoTracking()
            .Where(i => i.Id == s.TableInstanceId)
            .Select(i => new { i.TableDefId, i.DocumentId })
            .SingleAsync().ConfigureAwait(false);
        var sheetDefId = await db.TableDefs.AsNoTracking()
            .Where(t => t.Id == instance.TableDefId)
            .Select(t => t.SheetDefId)
            .SingleAsync().ConfigureAwait(false);
        var templateVersionId = await db.SheetDefs.AsNoTracking()
            .Where(sh => sh.Id == sheetDefId)
            .Select(sh => sh.TemplateVersionId)
            .SingleAsync().ConfigureAwait(false);

        using var workbook = new XLWorkbook();
        var main = workbook.Worksheets.Add("MAIN");
        main.Cell(2, 1).Value = 30m;
        var block = new ExcelTableBlock(
            s.TableInstanceId, instance.TableDefId, "MAIN_FILE", "MAIN", HeaderRow: 1,
            Columns: [new ExcelColumnRef(s.DataColumnId, s.DataColumnCode, 1, false, null)],
            Rows: [new ExcelRowRef(s.RowKey, 2)]);
        var map = new ExcelWorkbookMap(s.DocumentId, 202601, templateVersionId, [block]);
        workbook.Worksheets.Add(ExcelWorkbookMap.SheetName).Cell(1, 1).Value = JsonSerializer.Serialize(map, MapJson);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
