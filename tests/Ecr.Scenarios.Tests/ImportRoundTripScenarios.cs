using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scenarios.Tests;

/// <summary>
/// `V-10` (UX-PASS 2026-09-23, третій раунд): експорт → імпорт ПО КОЛУ.
/// Вивантажена продуктом книга, повернута без змін, — це «файл збігається з
/// аркушем», а не перелік відмов, через який Apply вимкнено назавжди.
/// </summary>
/// <remarks>
/// ⛔ До фікса експорт писав у колонку-формулу і значення, і формулу, а
/// імпорт відхиляв КОЖНУ обчислювану комірку (`ECR-CELL-4221`) — навіть ту,
/// якої ніхто не чіпав: для типу <c>Formula</c> значення з файлу читалося
/// текстом і порівнювалося з <c>ValueString</c>, якого в числової комірки
/// немає. Оскільки часткове застосування заборонене, незмінена книга не
/// застосовувалася взагалі — а змінена у ВХІДНІЙ комірці так само.
///
/// ⚠ Книга не будується в тесті — вона ВИВАНТАЖУЄТЬСЯ тим самим шляхом, що
/// в інтерфейсі (<c>ExportButton</c>: <c>includeFormulas: true</c>), і
/// правиться ClosedXML лише там, де її правив би користувач.
/// </remarks>
[Collection("SqlServer")]
public sealed class ImportRoundTripScenarios(SqlServerFixture sql)
{
    internal const string Prefix = "V10R";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Integration")]
    [Trait("Finding", "V-10")]
    public async Task Незмінена_книга_дає_нуль_змін_і_нуль_відмов_а_змінена_вхідна_комірка_застосовується(
        bool includeFormulas)
    {
        using var app = new EcrApiFactory(sql);

        var admin = await Provisioning.AdministratorAsync(
            app,
            Prefix,
            [
                "Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Template.Publish",
                "Calculation.View", "Calculation.EditFormula", "Calculation.Recalculate",
                "Document.Export", "Document.Import", "System.ViewHealth",
            ]);

        var doc = await DataEntryScenarios.ArrangeRealDocumentAsync(
            app, admin, Prefix, formulaColumn: ("F", "[A] * 2"));
        admin = doc.Admin;

        var instanceId = await TableInstanceAsync(admin.Client, doc);

        await WriteAsync(app, admin.Client, doc, instanceId, "R1", 5m);
        await WriteAsync(app, admin.Client, doc, instanceId, "R2", 7m);

        // ⚠ Колонка-формула мусить бути ПОРАХОВАНА до експорту: саме її число
        // (або формула над ним) і було тим, що імпорт відхиляв.
        await AwaitCellAsync(app, admin.Client, doc, instanceId, "R1", "F", 10m);
        await AwaitCellAsync(app, admin.Client, doc, instanceId, "R2", "F", 14m);

        var book = await ExportAsync(app, admin.Client, doc, includeFormulas);

        // 1. Незмінена книга: нуль змін, нуль відмов — «The file matches the sheet».
        var unchanged = await PreviewAsync(app, admin.Client, doc.DocumentId, book);
        Assert.True(
            unchanged.GetProperty("rejected").GetArrayLength() == 0,
            $"незмінена книга дала відмови: {unchanged.GetRawText()}");
        Assert.True(
            unchanged.GetProperty("changes").GetArrayLength() == 0,
            $"незмінена книга дала зміни: {unchanged.GetRawText()}");

        // 2. Змінено рівно одну ВХІДНУ комірку (R1.A) — рівно одна зміна, і
        //    формула поруч (у Excel вона вже перерахувала б F) не дає відмови.
        var edited = Edit(book, "R1", "A", 6m);
        var preview = await PreviewAsync(app, admin.Client, doc.DocumentId, edited);

        Assert.True(
            preview.GetProperty("rejected").GetArrayLength() == 0,
            $"змінена книга дала відмови: {preview.GetRawText()}");
        var change = Assert.Single(preview.GetProperty("changes").EnumerateArray().ToList());
        Assert.Equal("R1", change.GetProperty("rowKey").GetString());
        Assert.Equal("A", change.GetProperty("columnCode").GetString());

        // ⚠ `V-10`: і НАЗИВАЄ таблицю — у шаблоні з десятками таблиць однакові
        // `R1`/`A` нічого не кажуть без неї.
        Assert.Equal("TABLE1", change.GetProperty("tableCode").GetString());

        // 3. Apply → база.
        var apply = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/import/apply", UriKind.Relative),
            new { previewToken = preview.GetProperty("previewToken").GetString() });
        Assert.True(
            apply.StatusCode == HttpStatusCode.OK,
            $"застосування: {apply.StatusCode}: {await apply.Content.ReadAsStringAsync()}; {app.ErrorsText}");

        Assert.Equal(6m, await ReadAsync(admin.Client, doc, instanceId, "R1", "A"));
        Assert.Equal(7m, await ReadAsync(admin.Client, doc, instanceId, "R2", "A"));
    }

    /// <summary>Номер рядка книги для ключа рядка: підпис рядка — «Row N» у порядку ключів.</summary>
    /// <remarks>
    /// ⚠ Книга не пише ключ рядка видимо; рядки йдуть у порядку шаблону одразу
    /// під заголовком (рядок 2), тож <c>R1</c> — рядок 3, <c>R2</c> — рядок 4.
    /// </remarks>
    internal static int Row(IXLWorksheet sheet, string rowKey)
        => HeaderRow(sheet) + int.Parse(rowKey[1..], System.Globalization.CultureInfo.InvariantCulture);

    internal static int HeaderRow(IXLWorksheet sheet)
        => sheet.CellsUsed(c => string.Equals(c.GetString(), "A", StringComparison.Ordinal)).First().Address.RowNumber;

    internal static int Column(IXLWorksheet sheet, string header)
        => sheet.Row(HeaderRow(sheet)).CellsUsed(c => string.Equals(c.GetString(), header, StringComparison.Ordinal))
            .First().Address.ColumnNumber;

    /// <summary>Книга з однією зміненою коміркою — так, як її змінив би користувач.</summary>
    internal static byte[] Edit(byte[] book, string rowKey, string columnHeader, decimal value)
    {
        using var workbook = new XLWorkbook(new MemoryStream(book));
        var sheet = workbook.Worksheets.First(w => w.Visibility == XLWorksheetVisibility.Visible);

        sheet.Cell(Row(sheet, rowKey), Column(sheet, columnHeader)).Value = value;

        using var output = new MemoryStream();
        workbook.SaveAs(output);

        return output.ToArray();
    }

    internal static async Task<long> TableInstanceAsync(HttpClient client, DataEntryScenarios.RealDocument doc)
    {
        var tables = await client.GetAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/tables?periodKey={doc.PeriodKey}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, tables.StatusCode);

        return (await tables.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().First().GetProperty("tableInstanceId").GetInt64();
    }

    internal static async Task WriteAsync(
        EcrApiFactory app, HttpClient client, DataEntryScenarios.RealDocument doc, long tableInstanceId,
        string rowKey, decimal value)
    {
        var row = await RowAsync(client, doc.DocumentId, tableInstanceId, rowKey);

        var patch = await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId,
                periodKey = doc.PeriodKey,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey,
                        baseVersion = row.TryGetProperty("rowVersion", out var version) ? version.GetString() : null,
                        cells = new object[] { new { columnCode = "A", value } },
                    },
                },
            });

        Assert.True(
            patch.StatusCode == HttpStatusCode.OK,
            $"запис {rowKey}.A = {value}: {patch.StatusCode}: {await patch.Content.ReadAsStringAsync()}; {app.ErrorsText}");
    }

    internal static async Task<decimal?> ReadAsync(
        HttpClient client, DataEntryScenarios.RealDocument doc, long tableInstanceId, string rowKey, string column)
    {
        var row = await RowAsync(client, doc.DocumentId, tableInstanceId, rowKey);

        return row.GetProperty("cells").TryGetProperty(column, out var cell) ? JsonNumber.AsDecimalOrNull(cell) : null;
    }

    internal static async Task AwaitCellAsync(
        EcrApiFactory app, HttpClient client, DataEntryScenarios.RealDocument doc, long tableInstanceId,
        string rowKey, string column, decimal expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        decimal? last = null;

        while (DateTime.UtcNow < deadline)
        {
            last = await ReadAsync(client, doc, tableInstanceId, rowKey, column);
            if (last == expected)
            {
                return;
            }

            await Task.Delay(250);
        }

        Assert.Fail($"{rowKey}.{column} не став {expected} за 60 с (останнє: {last}); {app.ErrorsText}");
    }

    internal static async Task<JsonElement> RowAsync(HttpClient client, long documentId, long tableInstanceId, string rowKey)
    {
        var slice = await client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables/{tableInstanceId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, slice.StatusCode);

        var body = await slice.Content.ReadFromJsonAsync<JsonElement>();
        var row = body.GetProperty("rows").EnumerateArray().FirstOrDefault(
            r => string.Equals(r.GetProperty("rowKey").GetString(), rowKey, StringComparison.Ordinal));

        Assert.True(row.ValueKind == JsonValueKind.Object, $"рядка {rowKey} немає у зрізі: {body.GetRawText()}");

        return row;
    }

    internal static async Task<byte[]> ExportAsync(
        EcrApiFactory app, HttpClient client, DataEntryScenarios.RealDocument doc, bool includeFormulas)
    {
        var start = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/export", UriKind.Relative),
            new { includeFormulas, includeStyles = true, language = "en", periodKey = doc.PeriodKey });
        Assert.True(start.StatusCode == HttpStatusCode.Accepted, $"експорт: {start.StatusCode}: {app.ErrorsText}");
        var jobId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!;

        var job = await ScenarioHelpers.AwaitJobAsync(client, jobId, TimeSpan.FromSeconds(60));
        Assert.True(
            string.Equals(job.GetProperty("state").GetString(), "Succeeded", StringComparison.Ordinal),
            $"задача експорту: {job.GetRawText()}; {app.ErrorsText}");
        var exportId = job.GetProperty("message").GetString()!;

        var download = await client.GetAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/export/{exportId}", UriKind.Relative));
        Assert.True(download.StatusCode == HttpStatusCode.OK, $"книга: {download.StatusCode}: {app.ErrorsText}");

        return await download.Content.ReadAsByteArrayAsync();
    }

    internal static async Task<JsonElement> PreviewAsync(
        EcrApiFactory app, HttpClient client, long documentId, byte[] book)
    {
        using var content = new MultipartFormDataContent();
        using var file = new ByteArrayContent(book);
        file.Headers.ContentType = new MediaTypeHeaderValue(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(file, "file", "book.xlsx");

        var preview = await client.PostAsync(
            new Uri($"/api/v1/documents/{documentId}/import/preview", UriKind.Relative), content);

        var body = await preview.Content.ReadAsStringAsync();
        Assert.True(preview.StatusCode == HttpStatusCode.OK, $"перегляд: {preview.StatusCode}: {body}; {app.ErrorsText}");

        return JsonSerializer.Deserialize<JsonElement>(body);
    }
}
