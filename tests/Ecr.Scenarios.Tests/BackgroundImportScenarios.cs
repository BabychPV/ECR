using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scenarios.Tests;

/// <summary>
/// `F-01` (UX-PASS 2026-09-23, четвертий раунд): імпорт понад поріг
/// синхронного застосування (<c>ApplyImportHandler.LargeImportThreshold</c>,
/// 2000 комірок) іде у фон — і мусить ПРОХОДИТИ, від імені того, хто його
/// поставив.
/// </summary>
/// <remarks>
/// ⛔ До фікса задача падала на 10 % із <c>ECR-AUTH-0401</c> («Sign in to
/// continue.») ЗАВЖДИ: запис комірок бере поточного користувача, а єдина
/// реалізація <c>ICurrentUser</c> читала його з HTTP-запиту, якого у фоні
/// немає. У базі — нуль змін, у «My tasks» — вимога увійти.
///
/// ⚠ Книга не будується в тесті, а вивантажується продуктом і правиться там,
/// де її правив би користувач, — як у <see cref="ImportRoundTripScenarios"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class BackgroundImportScenarios(SqlServerFixture sql)
{
    private const string Prefix = "R4J01";

    /// <summary>Колонки понад <c>A</c>: 25 колонок × 81 рядок = 2025 комірок &gt; 2000.</summary>
    private static readonly string[] ExtraColumns =
    [
        "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
        "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y",
    ];

    private const int RowCount = 81;

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Finding", "F-01")]
    public async Task Імпорт_понад_поріг_іде_у_фон_і_пише_від_імені_автора()
    {
        using var app = new EcrApiFactory(sql);

        var admin = await Provisioning.AdministratorAsync(
            app,
            Prefix,
            [
                "Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Template.Publish",
                "Document.Export", "Document.Import", "Security.ViewAudit",
            ]);

        var doc = await DataEntryScenarios.ArrangeRealDocumentAsync(
            app, admin, Prefix, rowMode: "Dynamic", extraNumericColumns: ExtraColumns);
        admin = doc.Admin;

        var instanceId = await ImportRoundTripScenarios.TableInstanceAsync(admin.Client, doc);

        for (var i = 0; i < RowCount; i++)
        {
            var row = await admin.Client.PostAsJsonAsync(
                new Uri($"/api/v1/documents/{doc.DocumentId}/rows", UriKind.Relative),
                new { tableInstanceId = instanceId, rowKey = (string?)null });
            Assert.True(row.StatusCode == HttpStatusCode.Created, $"рядок {i}: {row.StatusCode}: {app.ErrorsText}");
        }

        var book = await ImportRoundTripScenarios.ExportAsync(app, admin.Client, doc, includeFormulas: true);
        var filled = FillAll(book, ["A", .. ExtraColumns]);

        var from = DateTime.UtcNow.AddMinutes(-1);
        var preview = await ImportRoundTripScenarios.PreviewAsync(app, admin.Client, doc.DocumentId, filled);

        var cells = RowCount * (ExtraColumns.Length + 1);
        Assert.True(
            preview.GetProperty("rejected").GetArrayLength() == 0,
            $"перегляд дав відмови: {Head(preview.GetProperty("rejected"))}");
        Assert.Equal(cells, preview.GetProperty("changes").GetArrayLength());

        var apply = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/import/apply", UriKind.Relative),
            new { previewToken = preview.GetProperty("previewToken").GetString() });

        // ⚠ Понад поріг — саме ЧЕРГА: інакше тест перевіряв би синхронний шлях,
        // який і так працював.
        Assert.True(
            apply.StatusCode == HttpStatusCode.Accepted,
            $"застосування: {apply.StatusCode}: {await apply.Content.ReadAsStringAsync()}; {app.ErrorsText}");
        var jobId = (await apply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!;

        var job = await ScenarioHelpers.AwaitJobAsync(admin.Client, jobId, TimeSpan.FromSeconds(120));

        // ⛔ Мутація: прибрати встановлення автора в `ExcelImportJob` — тут
        // `Failed` з `ECR-AUTH-0401`, рівно як на живому стенді.
        Assert.True(
            string.Equals(job.GetProperty("state").GetString(), "Succeeded", StringComparison.Ordinal),
            $"задача імпорту: {job.GetRawText()}; {app.ErrorsText}");

        // Значення в базі — ті, що в книзі.
        var slice = await admin.Client.GetAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/tables/{instanceId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, slice.StatusCode);
        var rows = (await slice.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(RowCount, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.True(row.GetProperty("cells").TryGetProperty("A", out var a), row.GetRawText());
            Assert.NotNull(JsonNumber.AsDecimalOrNull(a));
            Assert.True(row.GetProperty("cells").TryGetProperty("Y", out var y), row.GetRawText());
            Assert.NotNull(JsonNumber.AsDecimalOrNull(y));
        });

        // І журнал — від імені автора, з походженням Import.
        var audit = await admin.Client.GetAsync(new Uri(
            "/api/v1/audit/cells"
            + $"?from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}"
            + $"&to={Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(1).ToString("O", CultureInfo.InvariantCulture))}"
            + $"&documentId={doc.DocumentId}&origin=Import&limit=50",
            UriKind.Relative));
        Assert.True(
            audit.StatusCode == HttpStatusCode.OK,
            $"журнал: {audit.StatusCode}: {await audit.Content.ReadAsStringAsync()}; {app.ErrorsText}");
        var items = (await audit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToList();

        Assert.NotEmpty(items);
        Assert.All(items, item => Assert.Equal(admin.UserId, item.GetProperty("changedByUserId").GetInt32()));
    }

    /// <summary>Записує в кожну комірку кожного рядка даних число — «рядок × 100 + колонка».</summary>
    private static byte[] FillAll(byte[] book, IReadOnlyList<string> headers)
    {
        using var workbook = new XLWorkbook(new MemoryStream(book));
        var sheet = workbook.Worksheets.First(w => w.Visibility == XLWorksheetVisibility.Visible);
        var header = ImportRoundTripScenarios.HeaderRow(sheet);

        for (var r = 1; r <= RowCount; r++)
        {
            for (var c = 0; c < headers.Count; c++)
            {
                sheet.Cell(header + r, ImportRoundTripScenarios.Column(sheet, headers[c])).Value = (r * 100) + c + 1;
            }
        }

        using var output = new MemoryStream();
        workbook.SaveAs(output);

        return output.ToArray();
    }

    private static string Head(JsonElement array)
        => string.Join("; ", array.EnumerateArray().Take(5).Select(e => e.GetRawText()));
}
