using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scenarios.Tests;

/// <summary>
/// `DAT-01` і `DAT-09` — два оператори в одному документі та два одночасні
/// створення документа, обидва через HTTP і без жодного <c>SELECT</c> в обхід
/// застосунку.
/// </summary>
/// <remarks>
/// ⛔ Ці два сценарії — саме та перевірка, якої не робив ніхто: решта
/// сценаріїв послідовна, а обидва дефекти видно ЛИШЕ тоді, коли два запити
/// живуть одночасно. `DAT-01` («хибний 409 на рівні документа») і `DAT-09`
/// («подвійне створення документа») мають спільну природу — спільний рядок,
/// якого учасники не називали й не хотіли ділити.
///
/// ⚠ Тут паралелізм СПРАВЖНІЙ, на відміну від
/// <c>RegistryEntryDuplicateRaceTests</c>, де досить відтворити сам конфлікт.
/// Причина: предмет `DAT-01` — не конфлікт, а його ВІДСУТНІСТЬ, а «конфлікту
/// немає» послідовним прогоном не доводиться взагалі.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentConcurrencyScenarios(SqlServerFixture sql)
{
    /// <summary>Скільки разів повторити пару одночасних <c>PATCH</c>.</summary>
    private const int PatchRounds = 20;

    /// <summary>Скільки документів створювати одночасно.</summary>
    private const int ParallelCreations = 10;

    /// <summary>
    /// `DAT-01`: два оператори пишуть у РІЗНІ таблиці одного документа
    /// одночасно — і обидва отримують <c>200</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ До фікса другий отримував <c>409 ECR-CELL-0409</c> («дані змінилися
    /// після того, як ви їх прочитали») і втрачав УВЕСЬ свій батч — хоча
    /// ніяких спільних даних у двох операторів не було: різні таблиці, різні
    /// рядки, різні комірки. Спільним був один-єдиний рядок
    /// <c>doc.Document</c>, який «дотик» оновлював за предикатом
    /// <c>RowVersion</c>.
    ///
    /// ⚠ Різні ТАБЛИЦІ, а не різні рядки однієї: так у відмові не лишається
    /// жодного іншого підозрюваного, крім рядка документа.
    ///
    /// ⛔ Мутація: повернути в <c>DocumentStore.TouchAsync</c> читання
    /// відстежуваної сутності — і котрийсь із раундів дасть <c>409</c>.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Directive", "DAT-01")]
    public async Task Два_оператори_пишуть_у_різні_таблиці_одного_документа_одночасно()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "D01",
            ["Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Template.Publish"]);

        var (versionId, sheetDefId) = await BuildTwoTableVersionAsync(app, admin.Client, "D01");
        var (_, documentId, periodKey) = await CreateDocumentAsync(app, admin, versionId, sheetDefId, "D01");

        var tables = await admin.Client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables?periodKey={periodKey}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, tables.StatusCode);

        var tableArray = (await tables.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.True(
            tableArray.Count == 2,
            $"документ {documentId} має {tableArray.Count} таблиць(і) замість двох — "
            + $"перевіряти конфлікт між ними нема на чому: {app.ErrorsText}");

        var firstTable = tableArray[0].GetProperty("tableInstanceId").GetInt64();
        var secondTable = tableArray[1].GetProperty("tableInstanceId").GetInt64();

        for (var round = 1; round <= PatchRounds; round++)
        {
            // Версії читаються ПЕРЕД парою: кожен запис піднімає `RowVersion`
            // рядка, і `baseVersion` з попереднього раунду був би законним
            // конфліктом — тобто ховав би предмет перевірки.
            var (firstRow, firstVersion) = await FirstRowAsync(app, admin.Client, documentId, firstTable);
            var (secondRow, secondVersion) = await FirstRowAsync(app, admin.Client, documentId, secondTable);

            // ⛔ Обидва запити стартують ДО того, як хтось із них завершився:
            // послідовна пара не доводить нічого, бо саме одночасність і
            // створювала хибний конфлікт.
            var firstPatch = PatchAsync(
                admin.Client, documentId, periodKey, firstTable, firstRow, firstVersion, round);
            var secondPatch = PatchAsync(
                admin.Client, documentId, periodKey, secondTable, secondRow, secondVersion, round + 100);

            var responses = await Task.WhenAll(firstPatch, secondPatch);

            foreach (var response in responses)
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync();
                Assert.Fail(
                    $"раунд {round}: одночасний PATCH у другу таблицю того самого документа "
                    + $"дав {(int)response.StatusCode}: {body}{Environment.NewLine}{app.ErrorsText}");
            }
        }
    }

    /// <summary>
    /// `DAT-09`: десять одночасних <c>POST /documents</c> дають десять
    /// документів із різними ключами — і жодного <c>500</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ До фікса <c>BusinessKey</c> підбирався запитом <c>COUNT</c> плюс
    /// перевіркою «чи вільний», тобто знімком, який усі одночасні запити
    /// бачать однаковим. Переможений <c>UQ_Document</c> падав НЕОБРОБЛЕНИМ
    /// <c>500 ECR-SYS-0500</c>: людина бачила «внутрішню помилку» на дії, яка
    /// не мала жодної причини не спрацювати.
    ///
    /// ⛔ Мутація: прибрати арм <c>case Document</c> з
    /// <c>UnitOfWork.TryMapDuplicateKey</c> — повтор у
    /// <c>CreateDocumentHandler</c> не спрацює (він ловить саме цей виняток), і
    /// частина відповідей стане <c>500</c>.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Directive", "DAT-09")]
    public async Task Десять_одночасних_створень_документа_дають_десять_різних_ключів()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "D09",
            ["Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Template.Publish"]);

        var (versionId, sheetDefId) = await BuildTwoTableVersionAsync(app, admin.Client, "D09");
        var (projectId, _, _) = await CreateDocumentAsync(app, admin, versionId, sheetDefId, "D09");

        var payload = new { projectId, templateVersionId = versionId, sheetDefIds = new[] { sheetDefId } };

        // ⛔ Усі запити стартують ДО першого завершення: саме так два оператори
        // й отримують той самий номер із `COUNT`.
        var creations = Enumerable.Range(0, ParallelCreations)
            .Select(_ => admin.Client.PostAsJsonAsync(new Uri("/api/v1/documents", UriKind.Relative), payload))
            .ToList();

        var responses = await Task.WhenAll(creations);

        var ids = new List<long>();
        foreach (var response in responses)
        {
            if (response.StatusCode != HttpStatusCode.Created)
            {
                var body = await response.Content.ReadAsStringAsync();
                Assert.Fail(
                    $"одночасне створення документа дало {(int)response.StatusCode}: "
                    + $"{body}{Environment.NewLine}{app.ErrorsText}");
            }

            ids.Add((await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("documentId").GetInt64());
        }

        Assert.Equal(ParallelCreations, ids.Distinct().Count());

        // І ключі різні — доказ не в тому, що запити не впали, а в тому, що
        // документів рівно стільки, скільки просили, і кожен зі своїм ключем.
        var list = await admin.Client.GetAsync(
            new Uri($"/api/v1/documents?projectId={projectId}&limit=50", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var keys = (await list.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").EnumerateArray()
            .Select(d => d.GetProperty("businessKey").GetString()!)
            .ToList();

        // Один документ завела сама підготовка, решту — паралельний блок.
        Assert.Equal(ParallelCreations + 1, keys.Count);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Перший рядок зрізу таблиці: ключ і поточна версія.</summary>
    private static async Task<(string RowKey, string Version)> FirstRowAsync(
        EcrApiFactory app, HttpClient client, long documentId, long tableInstanceId)
    {
        var slice = await client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables/{tableInstanceId}", UriKind.Relative));
        Assert.True(slice.StatusCode == HttpStatusCode.OK, $"{slice.StatusCode}: {app.ErrorsText}");

        var rows = (await slice.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("rows").EnumerateArray().ToList();
        Assert.True(rows.Count > 0, $"таблиця {tableInstanceId} не має жодного рядка: {app.ErrorsText}");

        return (rows[0].GetProperty("rowKey").GetString()!, rows[0].GetProperty("rowVersion").GetString()!);
    }

    /// <summary>Один <c>PATCH</c> однієї комірки — рівно те, що робить сітка.</summary>
    private static Task<HttpResponseMessage> PatchAsync(
        HttpClient client, long documentId, int periodKey, long tableInstanceId,
        string rowKey, string baseVersion, int value)
        => client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId,
                periodKey,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey,
                        baseVersion,
                        cells = new object[] { new { columnCode = "A", value = (decimal)value } },
                    },
                },
            });

    /// <summary>
    /// Версія шаблону з ОДНИМ аркушем і ДВОМА фіксованими таблицями — по
    /// числовій колонці <c>A</c> і двох рядках у кожній.
    /// </summary>
    /// <remarks>
    /// ⚠ Власна побудова, а не <c>DataEntryScenarios.ArrangeRealDocumentAsync</c>:
    /// та заводить рівно одну таблицю, а весь сенс `DAT-01` — у ДРУГІЙ, у яку
    /// пише другий оператор.
    /// </remarks>
    private static async Task<(int VersionId, int SheetDefId)> BuildTwoTableVersionAsync(
        EcrApiFactory app, HttpClient client, string prefix)
    {
        var versionId = await StructureScenarios.CreateEmptyDraftVersionAsync(client, prefix);

        var addSheet = await client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/sheets/SHEET1", UriKind.Relative),
            new
            {
                nameL10n = new Dictionary<string, string> { ["en"] = $"{prefix} sheet" },
                ordinal = 1,
                sheetGroup = (string?)null,
                isMandatory = true,
                isVisible = true,
            });
        Assert.True(addSheet.StatusCode == HttpStatusCode.OK, $"{addSheet.StatusCode}: {app.ErrorsText}");
        var sheetDefId = (await addSheet.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var ordinal = 0;
        foreach (var tableCode in new[] { "TABLE1", "TABLE2" })
        {
            ordinal++;
            var addTable = await client.PutAsJsonAsync(
                new Uri($"/api/v1/template-versions/{versionId}/sheets/SHEET1/tables/{tableCode}", UriKind.Relative),
                new
                {
                    nameL10n = new Dictionary<string, string> { ["en"] = $"{prefix} {tableCode}" },
                    ordinal,
                    layoutKind = "PerPeriodInstance",
                    rowMode = "Fixed",
                    maxDynamicRows = (int?)null,
                });
            Assert.True(addTable.StatusCode == HttpStatusCode.OK, $"{tableCode}: {addTable.StatusCode}: {app.ErrorsText}");
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
            Assert.True(addColumn.StatusCode == HttpStatusCode.OK, $"{tableCode}.A: {addColumn.StatusCode}: {app.ErrorsText}");

            foreach (var (rowKey, index) in new[] { ("R1", 1), ("R2", 2) })
            {
                var addRow = await client.PutAsJsonAsync(
                    new Uri($"/api/v1/template-versions/{versionId}/tables/{tableDefId}/rows/{rowKey}", UriKind.Relative),
                    new
                    {
                        labelL10n = new Dictionary<string, string> { ["en"] = $"Row {index}" },
                        ordinal = index,
                        rowKind = "Item",
                        parentRowKey = (string?)null,
                        isReadOnly = false,
                    });
                Assert.True(addRow.StatusCode == HttpStatusCode.OK, $"{tableCode}.{rowKey}: {addRow.StatusCode}: {app.ErrorsText}");
            }
        }

        var publish = await client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/publish", UriKind.Relative),
            new { reason = "Побудова структури для сценарію одночасності" });
        Assert.True(publish.StatusCode == HttpStatusCode.NoContent, $"{publish.StatusCode}: {app.ErrorsText}");

        return (versionId, sheetDefId);
    }

    /// <summary>Проєкт на цій версії, активація, відкритий період і документ у ньому.</summary>
    private static async Task<(int ProjectId, long DocumentId, int PeriodKey)> CreateDocumentAsync(
        EcrApiFactory app, Provisioning.Administrator admin, int versionId, int sheetDefId, string prefix)
    {
        var policies = await admin.Client.GetAsync(new Uri("/api/v1/projects/period-policies", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, policies.StatusCode);
        var policyId = (await policies.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("id").GetInt32();

        var code = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var createProject = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/projects", UriKind.Relative),
            new
            {
                code,
                nameL10n = new Dictionary<string, string> { ["en"] = $"{prefix} project" },
                timeZoneId = "Asia/Almaty",
                periodKind = "Monthly",
                year = DateTime.UtcNow.Year,
                templateVersionId = versionId,
                periodPolicyId = policyId,
            });
        Assert.True(createProject.StatusCode == HttpStatusCode.Created, $"{createProject.StatusCode}: {app.ErrorsText}");
        var projectId = (await createProject.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("projectId").GetInt32();

        await ProjectAndPeriodScenarios.ActivateProjectAsync(admin, projectId);

        var periodsResponse = await admin.Client.GetAsync(
            new Uri($"/api/v1/projects/{projectId}/periods", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, periodsResponse.StatusCode);
        var periods = (await periodsResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("periods").EnumerateArray().ToList();

        var open = periods.Find(p => string.Equals(p.GetProperty("state").GetString(), "Open", StringComparison.Ordinal));
        Assert.True(
            open.ValueKind != JsonValueKind.Undefined,
            $"у проєкті {projectId} немає жодного відкритого періоду — писати нема куди.");
        var periodKey = open.GetProperty("periodKey").GetInt32();

        var createDoc = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/documents", UriKind.Relative),
            new { projectId, templateVersionId = versionId, sheetDefIds = new[] { sheetDefId } });
        Assert.True(createDoc.StatusCode == HttpStatusCode.Created, $"{createDoc.StatusCode}: {app.ErrorsText}");

        var documentId = (await createDoc.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("documentId").GetInt64();

        return (projectId, documentId, periodKey);
    }
}
