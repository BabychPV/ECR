using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
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

    /// <summary>Документів під навантажувальним сценарієм: × 2 таблиці × 2 рядки = 32 незалежні рядки.</summary>
    private const int LoadDocuments = 8;

    /// <summary>Скільки разів пустити всі 32 записи одночасно.</summary>
    private const int LoadRounds = 5;

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

    /// <summary>
    /// X8-03 (R6): 32 одночасні <c>PATCH</c> у різні рядки восьми документів через увесь
    /// конвеєр — і кожна підтверджена (<c>200</c>) правка лежить у зрізі та в журналі.
    /// </summary>
    /// <remarks>
    /// ⛔ Доти найбільший паралельний тест запису комірок мав ДВА одночасні запити й доводив
    /// лише коди відповідей. Вимога — 100 одночасних користувачів (НФ-8.1), і саме під
    /// навантаженням ламаються речі, яких послідовний прогін не бачить: дедлок між «дотиком»
    /// документа і постановкою перерахунку (після вичерпання повторів — 503), рядок аудиту,
    /// записаний поза транзакцією значення (200 без сліду в <c>aud.CellChange</c>).
    ///
    /// ⚠ Доказ — ДАНІ, а не коди: після всіх раундів кожен рядок перечитується через API й
    /// звіряється з останнім підтвердженим значенням, а журнал має рівно стільки змін
    /// <c>UserEdit</c>, скільки підтверджено.
    ///
    /// ⛔ Мутація: прибрати <c>documents.TouchAsync</c> з транзакції
    /// (<c>PatchCellsHandler.PersistChangesAsync</c>) або писати аудит власним підключенням —
    /// червоніє перечитування чи лічильник журналу. Конкурентний тест — правило N/20 (CI).
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Directive", "DAT-01")]
    [Trait("Requirement", "НФ-8.1")]
    public async Task Тридцять_два_одночасні_записи_в_різні_рядки_всі_збережені_і_в_журналі()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "L32",
            ["Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Template.Publish"]);

        var (versionId, sheetDefId) = await BuildTwoTableVersionAsync(app, admin.Client, "L32");
        var (projectId, firstDocumentId, periodKey) = await CreateDocumentAsync(app, admin, versionId, sheetDefId, "L32");

        var documentIds = new List<long> { firstDocumentId };
        for (var d = 1; d < LoadDocuments; d++)
        {
            var created = await admin.Client.PostAsJsonAsync(
                new Uri("/api/v1/documents", UriKind.Relative),
                new { projectId, templateVersionId = versionId, sheetDefIds = new[] { sheetDefId } });
            Assert.True(created.StatusCode == HttpStatusCode.Created, $"{created.StatusCode}: {app.ErrorsText}");
            documentIds.Add((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("documentId").GetInt64());
        }

        var targets = new List<(long DocumentId, long Table, string RowKey)>();
        foreach (var documentId in documentIds)
        {
            var tables = await admin.Client.GetAsync(
                new Uri($"/api/v1/documents/{documentId}/tables?periodKey={periodKey}", UriKind.Relative));
            Assert.True(tables.StatusCode == HttpStatusCode.OK, $"{tables.StatusCode}: {app.ErrorsText}");
            var instances = (await tables.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
                .Select(t => t.GetProperty("tableInstanceId").GetInt64())
                .ToList();
            Assert.True(instances.Count == 2, $"документ {documentId} має {instances.Count} таблиць(і) замість двох.");

            foreach (var table in instances)
            {
                foreach (var rowKey in new[] { "R1", "R2" })
                {
                    targets.Add((documentId, table, rowKey));
                }
            }
        }

        Assert.Equal(LoadDocuments * 4, targets.Count);

        var acknowledged = new Dictionary<(long Table, string RowKey), int>();
        for (var round = 1; round <= LoadRounds; round++)
        {
            // Версії — ПЕРЕД раундом: кожен запис піднімає `RowVersion`, і стара версія
            // дала б законний 409, тобто сховала б предмет перевірки.
            var versions = new List<string>();
            foreach (var target in targets)
            {
                versions.Add(await RowVersionAsync(app, admin.Client, target.DocumentId, target.Table, target.RowKey));
            }

            // ⛔ Усі 32 запити стартують ДО першого завершення.
            var sends = targets
                .Select((t, i) => PatchAsync(
                    admin.Client, t.DocumentId, periodKey, t.Table, t.RowKey, versions[i], (round * 1000) + i))
                .ToList();
            var replies = await Task.WhenAll(sends);

            for (var i = 0; i < replies.Length; i++)
            {
                if (replies[i].StatusCode != HttpStatusCode.OK)
                {
                    var body = await replies[i].Content.ReadAsStringAsync();
                    Assert.Fail(
                        $"раунд {round}, ціль {i} (документ {targets[i].DocumentId}, {targets[i].RowKey}): "
                        + $"{(int)replies[i].StatusCode} {body}{Environment.NewLine}{app.ErrorsText}");
                }

                acknowledged[(targets[i].Table, targets[i].RowKey)] = (round * 1000) + i;
            }
        }

        // ⛔ Доказ — дані: кожна підтверджена правка лежить у зрізі…
        foreach (var target in targets)
        {
            var slice = await admin.Client.GetAsync(
                new Uri($"/api/v1/documents/{target.DocumentId}/tables/{target.Table}", UriKind.Relative));
            Assert.True(slice.StatusCode == HttpStatusCode.OK, $"{slice.StatusCode}: {app.ErrorsText}");
            var row = (await slice.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("rows").EnumerateArray()
                .Single(r => string.Equals(r.GetProperty("rowKey").GetString(), target.RowKey, StringComparison.Ordinal));
            Assert.True(row.GetProperty("cells").TryGetProperty("A", out var cell), row.GetRawText());
            Assert.Equal((decimal)acknowledged[(target.Table, target.RowKey)], JsonNumber.AsDecimal(cell));
        }

        // …і в журналі рівно стільки змін, скільки підтверджено.
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var count = connection.CreateCommand();
        count.CommandText =
            "SELECT COUNT(*) FROM aud.CellChange WHERE Origin = N'UserEdit' AND DocumentId IN ("
            + string.Join(',', documentIds) + ");";
        Assert.Equal(targets.Count * LoadRounds, (int)(await count.ExecuteScalarAsync())!);
    }

    /// <summary>Поточна версія рядка <paramref name="rowKey"/> зрізу таблиці.</summary>
    private static async Task<string> RowVersionAsync(
        EcrApiFactory app, HttpClient client, long documentId, long tableInstanceId, string rowKey)
    {
        var slice = await client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables/{tableInstanceId}", UriKind.Relative));
        Assert.True(slice.StatusCode == HttpStatusCode.OK, $"{slice.StatusCode}: {app.ErrorsText}");

        var row = (await slice.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("rows").EnumerateArray()
            .Single(r => string.Equals(r.GetProperty("rowKey").GetString(), rowKey, StringComparison.Ordinal));

        return row.GetProperty("rowVersion").GetString()!;
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
                timeZoneId = "Asia/Atyrau",
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
