using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Scenarios.Tests;

/// <summary>
/// `DAT-05` / `D14-04` — імпорт `.xlsx` застосовується «все або нічого»,
/// наскрізно: книга на ТРИ таблиці, друга конфліктує.
/// </summary>
/// <remarks>
/// ⛔ До фікса <c>ExcelImporter.ApplyAsync</c> кликав <c>PatchCellsHandler</c>
/// на кожну таблицю окремо — кожну зі своєю транзакцією і своєю задачею
/// перерахунку. Конфлікт на другій означав: перша вже закомічена й уже стоїть у
/// черзі на перерахунок, клієнт отримує помилку БЕЗ переліку застосованого, а
/// документ лишається в стані, якого ніхто не замовляв. Головний аргумент не в
/// транзакціях: користувач щойно бачив diff ЦІЛОЇ книги і натиснув «Apply» на
/// нього.
///
/// ⚠ Книга не будується в тесті — вона ВИВАНТАЖУЄТЬСЯ з того самого документа
/// через <c>POST …/export</c> і повертається через <c>POST …/import/preview</c>.
/// Зібрана вручну книга перевіряла б наші уявлення про формат; вивантажена
/// проходить рівно той шлях, яким ходить користувач, і заразом не потребує
/// посилання на <c>Ecr.Adapters.Excel</c> (Правило 1, §3.2).
///
/// ⚠ Задачі перерахунку рахуються ПРЯМИМ запитом до <c>itg.JobProgress</c>:
/// HTTP-контракт не віддає числа поставлених задач за кодом, а саме воно тут і
/// є твердженням. Прецедент той самий, що в <c>RecalculationWriteScopeScenarios</c>
/// (<c>aud.CellChange</c>) — читання журналу, а не обхід обробників.
/// </remarks>
[Collection("SqlServer")]
public sealed class ImportAtomicityScenarios(SqlServerFixture sql)
{
    /// <summary>Код задачі в журналі — повне ім'я маркера (`QuartzJobScheduler`).</summary>
    private const string FormulaJobCode = "Ecr.Application.Ports.IFormulaRecalculationJob";

    private static readonly string[] TableCodes = ["TABLE1", "TABLE2", "TABLE3"];

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Requirement", "DAT-05")]
    public async Task Конфлікт_у_другій_таблиці_книги_не_лишає_ні_змін_ні_задач()
    {
        using var app = new EcrApiFactory(sql);

        var admin = await Provisioning.AdministratorAsync(
            app,
            "D14I",
            [
                "Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Template.Publish",
                "Document.Export", "Document.Import", "System.ViewHealth",
            ]);

        var doc = await ArrangeThreeTableDocumentAsync(app, admin);
        admin = doc.Admin;

        // 1. У кожній із трьох таблиць — число 1. Це стан, який потрапить у книгу.
        foreach (var (_, instanceId) in doc.Tables)
        {
            await WriteAsync(app, admin.Client, doc, instanceId, 1m);
        }

        var book = await ExportAsync(app, admin.Client, doc);

        // 2. Після вивантаження всі три числа стають 2. Тепер книга розходиться
        //    з базою в УСІХ трьох таблицях — саме це й робить diff книгою, а не
        //    правкою однієї таблиці.
        foreach (var (_, instanceId) in doc.Tables)
        {
            await WriteAsync(app, admin.Client, doc, instanceId, 2m);
        }

        var staleToken = await PreviewAsync(app, admin.Client, doc.DocumentId, book);

        // 3. Рівно ОДНА зміна — у ДРУГІЙ таблиці — після побудови перегляду:
        //    версія її рядка розходиться з тією, яку перегляд запам'ятав.
        //    Це і є «друга таблиця конфліктує».
        await WriteAsync(app, admin.Client, doc, doc.Tables["TABLE2"], 3m);

        var jobsBefore = await FormulaJobsAsync();

        var apply = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/import/apply", UriKind.Relative),
            new { previewToken = staleToken });

        var body = await apply.Content.ReadAsStringAsync();

        // (а) Застосування відхилено цілком.
        Assert.True(
            apply.StatusCode == HttpStatusCode.Conflict,
            $"застосування дало {apply.StatusCode} замість 409: {body}; {app.ErrorsText}");

        // (б) Відповідь НАЗИВАЄ таблицю-винуватця. Без адреси «все або нічого»
        //     гірше за часткове застосування: користувач бачить, що не
        //     змінилося нічого, і не знає, де шукати причину.
        var problem = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal("ECR-CELL-0409", problem.GetProperty("errorCode").GetString());
        Assert.Equal(
            doc.Tables["TABLE2"].ToString(CultureInfo.InvariantCulture),
            problem.GetProperty("tableInstanceId").GetString());

        // (в) У базі НУЛЬ змін. Перша таблиця йшла в тому самому батчі і до
        //     фікса була б уже закомічена значенням 1 — саме її число тут і є
        //     доказом, а не стан таблиці, до якої черга не дійшла.
        Assert.Equal(2m, await ReadAsync(admin.Client, doc, doc.Tables["TABLE1"]));
        Assert.Equal(3m, await ReadAsync(admin.Client, doc, doc.Tables["TABLE2"]));
        Assert.Equal(2m, await ReadAsync(admin.Client, doc, doc.Tables["TABLE3"]));

        // (г) Задач перерахунку поставлено НУЛЬ.
        Assert.Equal(jobsBefore, await FormulaJobsAsync());

        // 4. Та сама книга, перегляд побудований заново — застосовується цілком
        //    і ставить РІВНО ОДНУ задачу на документ, а не одну на таблицю.
        var freshToken = await PreviewAsync(app, admin.Client, doc.DocumentId, book);
        var jobsBeforeSuccess = await FormulaJobsAsync();

        var applied = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/import/apply", UriKind.Relative),
            new { previewToken = freshToken });

        Assert.True(
            applied.StatusCode == HttpStatusCode.OK,
            $"успішне застосування дало {applied.StatusCode}: "
            + $"{await applied.Content.ReadAsStringAsync()}; {app.ErrorsText}");

        foreach (var (_, instanceId) in doc.Tables)
        {
            Assert.Equal(1m, await ReadAsync(admin.Client, doc, instanceId));
        }

        Assert.Equal(jobsBeforeSuccess + 1, await FormulaJobsAsync());
    }

    /// <summary>Документ із ТРЬОМА таблицями на одному аркуші.</summary>
    /// <param name="Admin">Адміністратор із чинною сесією.</param>
    /// <param name="DocumentId">Документ.</param>
    /// <param name="PeriodKey">Відкритий період.</param>
    /// <param name="Tables">Код таблиці → її екземпляр у цьому документі.</param>
    private sealed record ThreeTableDocument(
        Provisioning.Administrator Admin,
        long DocumentId,
        int PeriodKey,
        IReadOnlyDictionary<string, long> Tables);

    /// <summary>
    /// Шаблон → аркуш → ТРИ таблиці (кожна з колонкою <c>A</c> і рядком
    /// <c>R1</c>) → публікація → проєкт → активація → документ.
    /// </summary>
    /// <remarks>
    /// ⚠ Власний будівник, а не <c>DataEntryScenarios.ArrangeRealDocumentAsync</c>:
    /// той заводить рівно ОДНУ таблицю, а весь предмет цього сценарію — книга,
    /// що складається з кількох таблиць.
    /// </remarks>
    private static async Task<ThreeTableDocument> ArrangeThreeTableDocumentAsync(
        EcrApiFactory app, Provisioning.Administrator admin)
    {
        const string Prefix = "D14I";
        var client = admin.Client;
        var versionId = await StructureScenarios.CreateEmptyDraftVersionAsync(client, Prefix);

        var addSheet = await client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/sheets/SHEET1", UriKind.Relative),
            new
            {
                nameL10n = new Dictionary<string, string> { ["en"] = "Import sheet" },
                ordinal = 1,
                sheetGroup = (string?)null,
                isMandatory = true,
                isVisible = true,
            });
        Assert.True(addSheet.StatusCode == HttpStatusCode.OK, $"аркуш: {addSheet.StatusCode}: {app.ErrorsText}");
        var sheetDefId = (await addSheet.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        for (var i = 0; i < TableCodes.Length; i++)
        {
            var code = TableCodes[i];

            var addTable = await client.PutAsJsonAsync(
                new Uri($"/api/v1/template-versions/{versionId}/sheets/SHEET1/tables/{code}", UriKind.Relative),
                new
                {
                    nameL10n = new Dictionary<string, string> { ["en"] = code },
                    ordinal = i + 1,
                    layoutKind = "PerPeriodInstance",
                    rowMode = "Fixed",
                    maxDynamicRows = (int?)null,
                });
            Assert.True(addTable.StatusCode == HttpStatusCode.OK, $"{code}: {addTable.StatusCode}: {app.ErrorsText}");
            var tableDefId = (await addTable.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

            var addColumn = await client.PutAsJsonAsync(
                new Uri($"/api/v1/template-versions/{versionId}/tables/{tableDefId}/columns/A", UriKind.Relative),
                new
                {
                    headerL10n = new Dictionary<string, string> { ["en"] = "A" },
                    ordinal = 1,
                    dataType = "Decimal",
                    isRequired = false,
                    isReadOnly = false,
                    isHidden = false,
                    precision = (byte?)null,
                    scale = (byte?)null,
                    defaultValue = (string?)null,
                    displayFormat = (string?)null,
                    styleId = (int?)null,
                    lookupRegistryDefId = (int?)null,
                    lookupFilter = (string?)null,
                    unitId = (int?)null,
                });
            Assert.True(
                addColumn.StatusCode == HttpStatusCode.OK, $"{code}.A: {addColumn.StatusCode}: {app.ErrorsText}");

            var addRow = await client.PutAsJsonAsync(
                new Uri($"/api/v1/template-versions/{versionId}/tables/{tableDefId}/rows/R1", UriKind.Relative),
                new
                {
                    labelL10n = new Dictionary<string, string> { ["en"] = "Row 1" },
                    ordinal = 1,
                    rowKind = "Item",
                    parentRowKey = (string?)null,
                    isReadOnly = false,
                });
            Assert.True(addRow.StatusCode == HttpStatusCode.OK, $"{code}.R1: {addRow.StatusCode}: {app.ErrorsText}");
        }

        var publish = await client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/publish", UriKind.Relative),
            new { reason = "Книга на три таблиці для DAT-05" });
        Assert.True(publish.StatusCode == HttpStatusCode.NoContent, $"публікація: {publish.StatusCode}: {app.ErrorsText}");

        var policies = await client.GetAsync(new Uri("/api/v1/projects/period-policies", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, policies.StatusCode);
        var policyId = (await policies.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("id").GetInt32();

        var createProject = await client.PostAsJsonAsync(
            new Uri("/api/v1/projects", UriKind.Relative),
            new
            {
                code = $"{Prefix}_{Guid.NewGuid():N}"[..20],
                nameL10n = new Dictionary<string, string> { ["en"] = "Import project" },
                timeZoneId = "Asia/Almaty",
                periodKind = "Monthly",
                year = DateTime.UtcNow.Year,
                templateVersionId = versionId,
                periodPolicyId = policyId,
            });
        Assert.True(createProject.StatusCode == HttpStatusCode.Created, $"проєкт: {createProject.StatusCode}: {app.ErrorsText}");
        var projectId = (await createProject.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("projectId").GetInt32();

        admin = await ProjectAndPeriodScenarios.ActivateProjectAsync(admin, projectId);

        var periodsResponse = await admin.Client.GetAsync(
            new Uri($"/api/v1/projects/{projectId}/periods", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, periodsResponse.StatusCode);
        var periods = (await periodsResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("periods").EnumerateArray().ToList();
        var open = periods.Find(p => string.Equals(p.GetProperty("state").GetString(), "Open", StringComparison.Ordinal));
        Assert.True(open.ValueKind != JsonValueKind.Undefined, "у проєкті немає відкритого періоду — писати нема куди.");
        var periodKey = open.GetProperty("periodKey").GetInt32();

        var createDoc = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/documents", UriKind.Relative),
            new { projectId, templateVersionId = versionId, sheetDefIds = new[] { sheetDefId } });
        Assert.True(createDoc.StatusCode == HttpStatusCode.Created, $"документ: {createDoc.StatusCode}: {app.ErrorsText}");
        var documentId = (await createDoc.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("documentId").GetInt64();

        var tablesResponse = await admin.Client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables?periodKey={periodKey}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, tablesResponse.StatusCode);

        var instances = (await tablesResponse.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray()
            .ToDictionary(
                t => t.GetProperty("tableCode").GetString()!,
                t => t.GetProperty("tableInstanceId").GetInt64(),
                StringComparer.Ordinal);

        Assert.Equal(TableCodes.Length, instances.Count);

        return new ThreeTableDocument(admin, documentId, periodKey, instances);
    }

    /// <summary>Пише <c>A</c> в рядок <c>R1</c> таблиці під її поточною версією.</summary>
    private static async Task WriteAsync(
        EcrApiFactory app, HttpClient client, ThreeTableDocument doc, long tableInstanceId, decimal value)
    {
        var row = await RowAsync(client, doc.DocumentId, tableInstanceId);

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
                        rowKey = "R1",
                        baseVersion = row.GetProperty("rowVersion").GetString(),
                        cells = new object[] { new { columnCode = "A", value } },
                    },
                },
            });

        Assert.True(
            patch.StatusCode == HttpStatusCode.OK,
            $"запис {value} у {tableInstanceId}: {patch.StatusCode}: "
            + $"{await patch.Content.ReadAsStringAsync()}; {app.ErrorsText}");
    }

    /// <summary>Поточне число в <c>A</c> рядка <c>R1</c>; <c>null</c> — комірка порожня.</summary>
    private static async Task<decimal?> ReadAsync(
        HttpClient client, ThreeTableDocument doc, long tableInstanceId)
    {
        var row = await RowAsync(client, doc.DocumentId, tableInstanceId);

        return row.GetProperty("cells").TryGetProperty("A", out var cell)
               && cell.ValueKind == JsonValueKind.Number
            ? cell.GetDecimal()
            : null;
    }

    private static async Task<JsonElement> RowAsync(HttpClient client, long documentId, long tableInstanceId)
    {
        var slice = await client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables/{tableInstanceId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, slice.StatusCode);

        var body = await slice.Content.ReadFromJsonAsync<JsonElement>();
        var row = body.GetProperty("rows").EnumerateArray().FirstOrDefault(
            r => string.Equals(r.GetProperty("rowKey").GetString(), "R1", StringComparison.Ordinal));

        Assert.True(row.ValueKind == JsonValueKind.Object, $"рядка R1 немає у зрізі: {body.GetRawText()}");

        return row;
    }

    /// <summary>Вивантажує книгу документа тим самим шляхом, що й користувач.</summary>
    private static async Task<byte[]> ExportAsync(
        EcrApiFactory app, HttpClient client, ThreeTableDocument doc)
    {
        var start = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/export", UriKind.Relative),
            new { includeFormulas = false, includeStyles = false, language = "en", periodKey = doc.PeriodKey });
        Assert.True(start.StatusCode == HttpStatusCode.Accepted, $"експорт: {start.StatusCode}: {app.ErrorsText}");
        var jobId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!;

        // ⚠ `exportId` приходить у ПОВІДОМЛЕННІ прогресу завершеної задачі —
        // саме так його читає й клієнт (`ExportButton.tsx`).
        var exportId = await AwaitExportIdAsync(app, client, jobId);

        var download = await client.GetAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/export/{exportId}", UriKind.Relative));
        Assert.True(download.StatusCode == HttpStatusCode.OK, $"завантаження книги: {download.StatusCode}: {app.ErrorsText}");

        return await download.Content.ReadAsByteArrayAsync();
    }

    private static async Task<string> AwaitExportIdAsync(EcrApiFactory app, HttpClient client, string jobId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        var last = "(жодної відповіді)";

        while (DateTime.UtcNow < deadline)
        {
            var status = await client.GetAsync(
                new Uri($"/api/v1/jobs/{Uri.EscapeDataString(jobId)}", UriKind.Relative));

            if (status.StatusCode == HttpStatusCode.OK)
            {
                var body = await status.Content.ReadFromJsonAsync<JsonElement>();
                last = body.GetRawText();
                var state = body.GetProperty("state").GetString();

                if (string.Equals(state, "Succeeded", StringComparison.Ordinal)
                    && body.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString()!;
                }

                Assert.False(
                    string.Equals(state, "Failed", StringComparison.Ordinal),
                    $"задача експорту впала: {last}; {app.ErrorsText}");
            }

            await Task.Delay(200);
        }

        Assert.Fail($"експорт не завершився за 60 с (останнє: {last}); {app.ErrorsText}");

        return string.Empty;
    }

    /// <summary>Будує перегляд імпорту книги і повертає його токен.</summary>
    private static async Task<string> PreviewAsync(
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

        var parsed = JsonSerializer.Deserialize<JsonElement>(body);

        // ⛔ Перегляд мусить бачити зміну в КОЖНІЙ із трьох таблиць. Книга, що
        // розходиться з базою лише в одній, зробила б увесь сценарій зеленим
        // із хибної причини: часткового застосування не буває там, де частина
        // одна.
        Assert.Equal(TableCodes.Length, parsed.GetProperty("changes").GetArrayLength());

        return parsed.GetProperty("previewToken").GetString()!;
    }

    /// <summary>Скільки задач перерахунку формул записано в журнал задач.</summary>
    private async Task<int> FormulaJobsAsync()
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM itg.JobProgress WHERE JobCode = @c;";
        command.Parameters.AddWithValue("@c", FormulaJobCode);

        return (int)(await command.ExecuteScalarAsync())!;
    }
}
