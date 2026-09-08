using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scenarios.Tests;

/// <summary>§6.4 директиви — введення даних і права: S-13..S-20.</summary>
/// <remarks>
/// ⛔ Усі вісім спираються на реальну таблицю документа
/// (<c>TableInstanceId</c>), а таблиця існує лише в опублікованій версії
/// шаблону з реальними <c>TableDef</c>/<c>ColumnDef</c>/<c>RowDef</c>. Шляху
/// створити їх через API немає (S-04..S-09), і публікація версії без жодного
/// аркуша відмовляє (S-09). Кожен сценарій тут усе одно виконує РЕАЛЬНИЙ
/// ланцюжок викликів, який довів би факт, якби структура існувала, — і падає
/// на конкретному, поіменованому кроці, а не мовчки.
/// </remarks>
[Collection("SqlServer")]
public sealed class DataEntryScenarios(SqlServerFixture sql)
{
    /// <remarks>
    /// ⛔ Сценарій переписаний із «доказу межі» на доказ РЕАЛЬНОЇ поведінки
    /// (директива №09 `W8` п.2): `W5` дав шлях завести `RowDef` через API, а
    /// `W8` — те, чого бракувало далі. Бракувало двох речей одразу, і кожна
    /// сама по собі робила фіксовану таблицю непридатною:
    /// <list type="number">
    /// <item>`doc.TableRow` за описами `cfg.RowDef` не будував НІХТО —
    /// екземпляр таблиці створювався порожнім назавжди;</item>
    /// <item>зріз збирав перелік рядків із КОМІРОК, тому рядок без жодного
    /// значення в ньому не існував — навіть якби його створили.</item>
    /// </list>
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-13")]
    public async Task Fixed_таблиця_віддає_свої_рядки_без_POST_rows()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S13",
            ["Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Template.Publish"]);

        var doc = await ArrangeRealDocumentAsync(app, admin, "S13");
        admin = doc.Admin;

        var tables = await admin.Client.GetAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/tables?periodKey={doc.PeriodKey}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, tables.StatusCode);

        var tableArray = await tables.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(
            tableArray.GetArrayLength() > 0,
            $"документ {doc.DocumentId} проєкту {doc.ProjectId} не має жодної таблиці: {app.ErrorsText}");

        // ⚠ Таблиця саме `Fixed`: рядків у неї оператор не додає.
        var table = tableArray[0];
        Assert.False(table.GetProperty("allowsDynamicRows").GetBoolean());

        var tableInstanceId = table.GetProperty("tableInstanceId").GetInt64();
        var slice = await admin.Client.GetAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/tables/{tableInstanceId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, slice.StatusCode);

        var sliceBody = await slice.Content.ReadFromJsonAsync<JsonElement>();
        var rows = sliceBody.GetProperty("rows").EnumerateArray().ToList();

        // ⛔ Доказ сценарію: рядки прийшли БЕЗ жодного `POST …/rows` і
        // без жодного записаного значення — тобто саме з `RowDef`.
        Assert.Equal(doc.RowKeys.Count, rows.Count);
        foreach (var rowKey in doc.RowKeys)
        {
            Assert.Contains(rows, r => r.GetProperty("rowKey").GetString() == rowKey);
        }

        // ⚠ І з підписами: фіксований рядок упізнають за назвою, а не за
        // ключем. Тут стояло `Label: null` на кожному рядку — поле контракту
        // існувало й не несло нічого.
        Assert.All(rows, r => Assert.False(
            string.IsNullOrWhiteSpace(r.GetProperty("label").GetString()),
            "рядок фіксованої таблиці прийшов без підпису з RowDef."));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-14")]
    public async Task Оператор_додає_рядок_у_динамічну_таблицю()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S14", ["Project.Manage", "Document.View", "Document.Create", "Template.Edit"]);

        (admin, var projectId, var documentId, var periodKey) = await ArrangeDocumentAsync(app, admin, "S14");

        var tables = await admin.Client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables?periodKey={periodKey}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, tables.StatusCode);
        var tableArray = await tables.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(
            tableArray.EnumerateArray().Any(t => t.GetProperty("allowsDynamicRows").GetBoolean()),
            $"документ {documentId} проєкту {projectId} не має жодної динамічної таблиці: без S-04 нема звідки їй узятися.");

        var tableInstanceId = tableArray.EnumerateArray()
            .First(t => t.GetProperty("allowsDynamicRows").GetBoolean())
            .GetProperty("tableInstanceId").GetInt64();

        var createRow = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId}/rows", UriKind.Relative),
            new { tableInstanceId, rowKey = (string?)null });

        Assert.Equal(HttpStatusCode.Created, createRow.StatusCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-15")]
    public async Task Значення_читається_назад_числом_не_текстом()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S15", ["Project.Manage", "Document.View", "Document.Create", "Template.Edit"]);

        (admin, _, var documentId, var periodKey) = await ArrangeDocumentAsync(app, admin, "S15");

        var tables = await admin.Client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables?periodKey={periodKey}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, tables.StatusCode);
        var tableArray = await tables.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(tableArray.GetArrayLength() > 0, $"документ {documentId} не має жодної таблиці — нема куди писати число.");
        var tableInstanceId = tableArray[0].GetProperty("tableInstanceId").GetInt64();

        var slice = await admin.Client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables/{tableInstanceId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, slice.StatusCode);
        var sliceBody = await slice.Content.ReadFromJsonAsync<JsonElement>();
        var columns = sliceBody.GetProperty("columns");
        Assert.True(columns.GetArrayLength() > 0, $"таблиця {tableInstanceId} не має жодної колонки — нема куди писати число.");
        var numericColumn = columns.EnumerateArray()
            .FirstOrDefault(c => string.Equals(c.GetProperty("dataType").GetString(), "Decimal", StringComparison.Ordinal));
        Assert.True(numericColumn.ValueKind != JsonValueKind.Undefined, $"таблиця {tableInstanceId} не має числової колонки.");
        var columnCode = numericColumn.GetProperty("code").GetString()!;

        var rows = sliceBody.GetProperty("rows");
        Assert.True(rows.GetArrayLength() > 0, $"таблиця {tableInstanceId} не має жодного рядка — нема куди писати число.");
        var rowKey = rows[0].GetProperty("rowKey").GetString()!;
        var baseVersion = rows[0].GetProperty("rowVersion").GetString();

        var patch = await admin.Client.PatchAsJsonAsync(
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
                        cells = new object[] { new { columnCode, value = 42.5m } },
                    },
                },
            });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var reread = await admin.Client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables/{tableInstanceId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, reread.StatusCode);
        var rereadBody = await reread.Content.ReadFromJsonAsync<JsonElement>();
        var writtenRow = rereadBody.GetProperty("rows").EnumerateArray().First(r => r.GetProperty("rowKey").GetString() == rowKey);
        var cellValue = writtenRow.GetProperty("cells").GetProperty(columnCode);

        // Доказ сценарію: JSON-число, а не рядок.
        Assert.Equal(JsonValueKind.Number, cellValue.ValueKind);
        Assert.Equal(42.5m, cellValue.GetDecimal());
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-16")]
    public async Task Закритий_період_блокує_запис_навіть_власнику_Manage()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S16", ["Project.Manage", "Document.View", "Document.Create", "Template.Edit"]);

        // Рік у далекому минулому — усі 12 місячних періодів давно за межею
        // HardClose (45 днів), тому PeriodStateJob неминуче переведе їх у
        // Closed, і не треба чекати на реальний годинник.
        var projectId = await CreateOldProjectAsync(admin.Client, "S16");
        await ProjectAndPeriodScenarios.ActivateProjectAsync(admin.Client, projectId);
        await Provisioning.GrantAsync(app, admin.RoleId, "Project", projectId, "Manage");
        admin = await Provisioning.ReauthenticateAsync(app, admin);

        var closedPeriodKey = await AwaitPeriodStateAsync(admin.Client, projectId, "Closed", TimeSpan.FromSeconds(20));
        Assert.True(closedPeriodKey.HasValue, $"жоден період проєкту {projectId} (рік 2019) не перейшов у Closed за 20 с.");

        var versionId = await StructureScenarios.CreateEmptyDraftVersionAsync(admin.Client, "S16");
        var createDoc = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/documents", UriKind.Relative),
            new { projectId, templateVersionId = versionId, sheetDefIds = Array.Empty<int>() });
        Assert.Equal(HttpStatusCode.Created, createDoc.StatusCode);
        var documentId = (await createDoc.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("documentId").GetInt64();

        // ⛔ Доказ сценарію: запис у ЗАКРИТИЙ період відхиляється — і на
        // PATCH, і на створенні рядка, — навіть під роллю з рівнем Manage.
        var patch = await admin.Client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = 1L,
                periodKey = closedPeriodKey!.Value,
                origin = "UserEdit",
                rows = new[]
                {
                    new { rowKey = "R1", baseVersion = (string?)null, cells = new object[] { new { columnCode = "C1", value = 1m } } },
                },
            });
        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);

        var createRow = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId}/rows", UriKind.Relative),
            new { tableInstanceId = 1L, rowKey = (string?)null });
        Assert.Equal(HttpStatusCode.Forbidden, createRow.StatusCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-17")]
    public async Task Заархівований_проєкт_блокує_новий_рядок()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S17a", ["Project.Manage", "Document.View", "Document.Create", "Template.Edit"]);

        var projectId = await CreateOldProjectAsync(admin.Client, "S17a");
        await ProjectAndPeriodScenarios.ActivateProjectAsync(admin.Client, projectId);
        await Provisioning.GrantAsync(app, admin.RoleId, "Project", projectId, "Manage");
        admin = await Provisioning.ReauthenticateAsync(app, admin);

        var closedPeriodKey = await AwaitPeriodStateAsync(admin.Client, projectId, "Closed", TimeSpan.FromSeconds(20));
        Assert.True(closedPeriodKey.HasValue, $"жоден період проєкту {projectId} не закрився — архівацію (яка вимагає всіх закритих) перевірити нема на чому.");

        var archive = await admin.Client.PostAsync(
            new Uri($"/api/v1/projects/{projectId}/archive", UriKind.Relative), content: null);
        Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);

        var createRow = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/documents/1/rows", UriKind.Relative),
            new { tableInstanceId = 1L, rowKey = (string?)null });

        // Проєкт заархівований — новий рядок для нього неможливий у принципі.
        Assert.Equal(HttpStatusCode.Forbidden, createRow.StatusCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-17")]
    public async Task IsDeny_грант_блокує_новий_рядок()
    {
        using var app = new EcrApiFactory(sql);
        var owner = await Provisioning.AdministratorAsync(app, "S17bOwner", ["Project.Manage", "Template.Edit", "Document.View"]);
        var projectId = await ProjectAndPeriodScenarios.CreateProjectAsync(owner.Client, "S17b", "Asia/Almaty");
        await ProjectAndPeriodScenarios.ActivateProjectAsync(owner.Client, projectId);

        var denied = await Provisioning.AdministratorAsync(app, "S17bDeny", ["Document.View", "Document.Create"]);
        await Provisioning.GrantAsync(app, denied.RoleId, "Project", projectId, "Manage", isDeny: true);
        denied = await Provisioning.ReauthenticateAsync(app, denied);

        var createRow = await denied.Client.PostAsJsonAsync(
            new Uri("/api/v1/documents/1/rows", UriKind.Relative),
            new { tableInstanceId = 1L, rowKey = (string?)null });

        Assert.Equal(HttpStatusCode.Forbidden, createRow.StatusCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-17")]
    public async Task Симуляція_блокує_новий_рядок()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S17c", ["Security.Simulate", "Document.View", "Document.Create"]);
        var subject = await Provisioning.AdministratorAsync(app, "S17cSubject", ["Document.View", "Document.Create"]);

        var start = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/security/simulation", UriKind.Relative),
            new { subjectUserId = subject.UserId, reason = "S-17: перевірка read-only симуляції" });
        Assert.Equal(HttpStatusCode.Created, start.StatusCode);

        var createRow = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/documents/1/rows", UriKind.Relative),
            new { tableInstanceId = 1L, rowKey = (string?)null });

        Assert.Equal(HttpStatusCode.Forbidden, createRow.StatusCode);
    }

    /// <remarks>
    /// ⛔ ЗАМІР: <c>POST …/submit</c> на <c>sheetDefId</c>, якого в документі
    /// немає (S-04 не дав жодного реального аркуша), повертає <c>204</c>, а не
    /// відмову — реальний прогін підтвердив, що обробник не звіряє
    /// <c>sheetDefId</c> зі складом документа перед поданням. Це самостійна,
    /// варта уваги знахідка, а не привід підганяти очікування — тому асерція
    /// нижче лишається `>= 400` і має падати саме так.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-17")]
    public async Task Подання_аркуша_блокує_новий_рядок()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S17d", ["Project.Manage", "Document.View", "Document.Create", "Template.Edit"]);

        (admin, _, var documentId, var periodKey) = await ArrangeDocumentAsync(app, admin, "S17d");

        // Подати можна лише РЕАЛЬНИЙ аркуш документа — а їх немає (S-04).
        // Викликаємо submit найпершим правдоподібним SheetDefId, щоб дійти до
        // самої перевірки; природний результат тут — відмова про НЕІСНУЮЧИЙ
        // аркуш, а не про подання, і саме це фіксує асерція.
        var submit = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId}/submit", UriKind.Relative),
            new { sheetDefId = 1, periodKey });

        Assert.True(
            (int)submit.StatusCode >= 400,
            $"подання неіснуючого аркуша мало відмовити, а повернуло {submit.StatusCode}");

        var createRow = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId}/rows", UriKind.Relative),
            new { tableInstanceId = 1L, rowKey = (string?)null });

        Assert.Equal(HttpStatusCode.Forbidden, createRow.StatusCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-18")]
    public async Task Чужий_TableInstanceId_у_маршруті_документа()
    {
        using var app = new EcrApiFactory(sql);
        var first = await Provisioning.AdministratorAsync(
            app, "S18a", ["Project.Manage", "Document.View", "Document.Create", "Template.Edit"]);
        var second = await Provisioning.AdministratorAsync(
            app, "S18b", ["Project.Manage", "Document.View", "Document.Create", "Template.Edit"]);

        (first, _, var firstDocumentId, var periodKey) = await ArrangeDocumentAsync(app, first, "S18a");
        (second, _, var secondDocumentId, _) = await ArrangeDocumentAsync(app, second, "S18b");

        var secondTables = await second.Client.GetAsync(
            new Uri($"/api/v1/documents/{secondDocumentId}/tables?periodKey={periodKey}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, secondTables.StatusCode);
        var secondTableArray = await secondTables.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(secondTableArray.GetArrayLength() > 0, $"документ {secondDocumentId} не має жодної таблиці — нема чужого TableInstanceId для проби.");
        var foreignTableInstanceId = secondTableArray[0].GetProperty("tableInstanceId").GetInt64();

        // Чужий TableInstanceId у маршруті СВОГО документа — має бути 4xx,
        // а не запис у чужу таблицю.
        var patch = await first.Client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{firstDocumentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = foreignTableInstanceId,
                periodKey,
                origin = "UserEdit",
                rows = new[]
                {
                    new { rowKey = "R1", baseVersion = (string?)null, cells = new object[] { new { columnCode = "C1", value = 1m } } },
                },
            });

        Assert.True((int)patch.StatusCode is >= 400 and < 500, $"чужий TableInstanceId мав дати 4xx, а дав {patch.StatusCode}");
    }

    /// <remarks>
    /// ⛔ Сценарій довів дефект, глибший за той, який називала директива.
    /// Повідомлень не було не тому, що правил не було звідки взяти, — правило
    /// заводилося ще з `W5.4`. <c>MetadataCache</c> не вантажив
    /// <c>cfg.ValidationRule</c> ВЗАГАЛІ: <c>ValidateDocumentHandler</c> читає
    /// рівно <c>table.ValidationRules</c> цього знімка, тож
    /// <c>POST …/validate</c> відповідав «зауважень немає» на будь-яких даних
    /// при будь-яких заведених правилах. Другий шар — рівень рядка виконувався
    /// одним проходом і ставив <c>rowKey: null</c> завжди: список повідомляв,
    /// ЩО не так, і не повідомляв, ДЕ.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-19")]
    public async Task Валідація_віддає_повідомлення_а_не_лише_кількість()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S19",
            ["Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Template.Publish"]);

        // Правило рівня рядка: значення колонки `A` не більше за 100.
        var doc = await ArrangeRealDocumentAsync(app, admin, "S19", "[A] <= 100");
        admin = doc.Admin;

        var tables = await admin.Client.GetAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/tables?periodKey={doc.PeriodKey}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, tables.StatusCode);
        var tableInstanceId = (await tables.Content.ReadFromJsonAsync<JsonElement>())[0]
            .GetProperty("tableInstanceId").GetInt64();

        // Число, яке правило порушує.
        await PatchAsync(app, admin, doc, tableInstanceId, 101m);

        var validate = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/validate", UriKind.Relative),
            new { periodKey = doc.PeriodKey });
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);

        var messages = (await validate.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("messages").EnumerateArray().ToList();

        // ⛔ Доказ сценарію: непорожній список ІЗ адресою рядка, а не число.
        Assert.NotEmpty(messages);
        var violation = messages.Find(m => m.GetProperty("severity").GetString() == "Error");
        Assert.True(violation.ValueKind != JsonValueKind.Undefined, $"жодного Error серед повідомлень: {app.ErrorsText}");
        Assert.Equal(doc.RowKeys[0], violation.GetProperty("rowKey").GetString());

        // ⛔ І другий бік того самого: результат ЧИТАЄТЬСЯ окремим `GET`, тобто
        // переживає перезавантаження сторінки. Доти підсумок зберігався і не
        // мав жодного читача (`IValidationResultStore.GetLatestAsync`), а
        // перелік зауважень жив рівно до оновлення вкладки.
        var read = await admin.Client.GetAsync(new Uri(
            $"/api/v1/documents/{doc.DocumentId}/validation?periodKey={doc.PeriodKey}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var stored = (await read.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(messages.Count, stored.Count);
        Assert.Contains(stored, m => m.GetProperty("rowKey").GetString() == doc.RowKeys[0]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-19")]
    public async Task Непроведена_перевірка_це_404_а_не_порожній_перелік()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S19b",
            ["Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Template.Publish"]);

        var doc = await ArrangeRealDocumentAsync(app, admin, "S19b");
        admin = doc.Admin;

        var read = await admin.Client.GetAsync(new Uri(
            $"/api/v1/documents/{doc.DocumentId}/validation?periodKey={doc.PeriodKey}", UriKind.Relative));

        // ⚠ «Зауважень немає» і «ще не перевіряли» — різні відповіді. Показати
        // першу замість другої означає повідомити неправду про готовність
        // документа рівно тоді, коли на неї спираються, подаючи звітність.
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    }

    /// <remarks>
    /// ⛔ Сценарій довго не міг дійти до головного: без реальної таблиці
    /// (`S-04`…`S-09`) не було що змінювати. Тепер він змінює комірку ДВІЧІ —
    /// і саме друга правка доводить те, заради чого журнал ведуть: «було 7,
    /// стало 9». Перша правка старого значення не має чесно (комірки не
    /// існувало, `ФВ-3.8`), і саме тому одного запису тут замало.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-20")]
    public async Task Аудит_записує_старе_нове_значення_і_RowKey()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S20",
            [
                "Project.Manage", "Document.View", "Document.Create", "Template.Edit",
                "Template.Publish", "Security.ViewAudit",
            ]);

        var doc = await ArrangeRealDocumentAsync(app, admin, "S20");
        admin = doc.Admin;

        var tables = await admin.Client.GetAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/tables?periodKey={doc.PeriodKey}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, tables.StatusCode);
        var tableArray = await tables.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(tableArray.GetArrayLength() > 0, $"документ {doc.DocumentId} не має жодної таблиці: {app.ErrorsText}");
        var tableInstanceId = tableArray[0].GetProperty("tableInstanceId").GetInt64();

        var from = DateTime.UtcNow.AddMinutes(-1);

        // Перша правка: комірки ще не існувало — «було» лишається порожнім.
        await PatchAsync(app, admin, doc, tableInstanceId, 7m);

        // Друга: саме вона має лягти в журнал як «було 7, стало 9».
        await PatchAsync(app, admin, doc, tableInstanceId, 9m);

        var to = DateTime.UtcNow.AddMinutes(1);
        var audit = await admin.Client.GetAsync(new Uri(
            $"/api/v1/audit/cells?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}&documentId={doc.DocumentId}&limit=50",
            UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);

        var items = (await audit.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").EnumerateArray().ToList();
        Assert.True(items.Count >= 2, $"журнал аудиту не має двох записів для документа {doc.DocumentId}: {app.ErrorsText}");

        // ⛔ Доказ сценарію: запис із НОВИМ значенням 9 несе і адресу рядка, і
        // старе значення. Обидва поля писалися константами (`RowKey`
        // порожнім рядком, `OldValue` — `null`), тож журнал відповідав «стало
        // 9» і не міг сказати ні де, ні що було до того.
        var second = items.Find(i => i.GetProperty("newValue").GetString() == "9");
        Assert.True(second.ValueKind != JsonValueKind.Undefined, "у журналі немає запису про другу правку.");
        Assert.Equal(doc.RowKeys[0], second.GetProperty("rowKey").GetString());

        // ⚠ Порівняння ЧИСЛОМ, а не рядком: «нове» приходить із запиту
        // (`9`), а «старе» — з бази, де воно лежить із масштабом колонки
        // (`7.0000000000`). Це реальна поведінка, і підганяти під неї
        // очікування рядком означало б зафіксувати спосіб форматування
        // замість факту.
        Assert.Equal(
            7m,
            decimal.Parse(
                second.GetProperty("oldValue").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Пише число в першу комірку першого рядка, звіряючи версію.</summary>
    private static async Task PatchAsync(
        EcrApiFactory app, Provisioning.Administrator admin, RealDocument doc,
        long tableInstanceId, decimal value)
    {
        var slice = await admin.Client.GetAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/tables/{tableInstanceId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, slice.StatusCode);

        var row = (await slice.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("rows").EnumerateArray()
            .First(r => r.GetProperty("rowKey").GetString() == doc.RowKeys[0]);

        var patch = await admin.Client.PatchAsJsonAsync(
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
                        rowKey = doc.RowKeys[0],
                        baseVersion = row.GetProperty("rowVersion").GetString(),
                        cells = new object[] { new { columnCode = doc.ColumnCode, value } },
                    },
                },
            });

        Assert.True(patch.StatusCode == HttpStatusCode.OK, $"{patch.StatusCode}: {app.ErrorsText}");
    }

    /// <summary>
    /// Створює проєкт, чернеткову версію і документ; повертає перший ключ
    /// періоду й АДМІНІСТРАТОРА З ОНОВЛЕНОЮ сесією.
    /// </summary>
    /// <remarks>
    /// ⛔ Грант усередині (ФВ-6.7) застаріває клієнта, з яким прийшов
    /// <paramref name="admin"/>: повертається НОВИЙ <see cref="Provisioning.Administrator"/>
    /// з чинною cookie — виклики мають продовжувати роботу саме ним, а не
    /// оригінальним параметром.
    /// </remarks>
    internal static async Task<(Provisioning.Administrator Admin, int ProjectId, long DocumentId, int PeriodKey)> ArrangeDocumentAsync(
        EcrApiFactory app, Provisioning.Administrator admin, string prefix)
    {
        var projectId = await ProjectAndPeriodScenarios.CreateProjectAsync(admin.Client, prefix, "Asia/Almaty");
        await ProjectAndPeriodScenarios.ActivateProjectAsync(admin.Client, projectId);
        await Provisioning.GrantAsync(app, admin.RoleId, "Project", projectId, "Manage");
        admin = await Provisioning.ReauthenticateAsync(app, admin);

        var periodsResponse = await admin.Client.GetAsync(
            new Uri($"/api/v1/projects/{projectId}/periods", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, periodsResponse.StatusCode);
        var calendar = await periodsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var periods = calendar.GetProperty("periods");
        Assert.True(periods.GetArrayLength() > 0, $"календар проєкту {projectId} порожній.");
        var periodKey = periods[0].GetProperty("periodKey").GetInt32();

        var versionId = await StructureScenarios.CreateEmptyDraftVersionAsync(admin.Client, prefix);

        var createDoc = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/documents", UriKind.Relative),
            new { projectId, templateVersionId = versionId, sheetDefIds = Array.Empty<int>() });
        Assert.Equal(HttpStatusCode.Created, createDoc.StatusCode);
        var documentId = (await createDoc.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("documentId").GetInt64();

        return (admin, projectId, documentId, periodKey);
    }

    /// <summary>Реальний документ: аркуш, фіксована таблиця, колонка, два рядки.</summary>
    /// <param name="Admin">Адміністратор із чинною сесією ПІСЛЯ гранта.</param>
    /// <param name="ProjectId">Проєкт.</param>
    /// <param name="DocumentId">Документ.</param>
    /// <param name="PeriodKey">Період, у якому працює сценарій.</param>
    /// <param name="SheetDefId">Аркуш — ним подають і затверджують.</param>
    /// <param name="TableDefId">Опис таблиці.</param>
    /// <param name="ColumnCode">Код єдиної числової колонки.</param>
    /// <param name="RowKeys">Ключі рядків, які шаблон задає фіксованій таблиці.</param>
    internal sealed record RealDocument(
        Provisioning.Administrator Admin,
        int ProjectId,
        long DocumentId,
        int PeriodKey,
        int SheetDefId,
        int TableDefId,
        string ColumnCode,
        IReadOnlyList<string> RowKeys);

    /// <summary>
    /// Будує документ із РЕАЛЬНОЮ структурою через ті самі маршрути, якими це
    /// робить адміністратор.
    /// </summary>
    /// <param name="app">Піднятий застосунок.</param>
    /// <param name="admin">Адміністратор; повертається НОВИЙ, з чинною сесією.</param>
    /// <param name="prefix">Префікс кодів — свій у кожного сценарію.</param>
    /// <param name="validationExpression">
    /// Вираз правила валідації рівня РЯДКА (<c>Scope = 1</c>); <c>null</c> —
    /// правил не заводити.
    /// </param>
    /// <remarks>
    /// ⛔ Це не «фікстура зручності». До `W5` такого шляху не існувало в API
    /// взагалі, і саме тому `S-13`…`S-21`, `S-25`, `S-28` доводили
    /// відсутність маршруту замість поведінки. Тепер ланцюжок реальний і
    /// повний: шаблон → версія → аркуш → таблиця → колонка → рядки →
    /// (правило) → публікація → проєкт → активація → документ. Жодного
    /// <c>SELECT</c> і жодного обходу HTTP (Правило 1, §3.2).
    ///
    /// ⚠ Версія ПУБЛІКУЄТЬСЯ: проєкт у проді працює на опублікованій, і саме
    /// на ній перевіряються кеш метаданих, план перерахунку і зріз.
    /// </remarks>
    internal static async Task<RealDocument> ArrangeRealDocumentAsync(
        EcrApiFactory app, Provisioning.Administrator admin, string prefix,
        string? validationExpression = null)
    {
        var versionId = await StructureScenarios.CreateEmptyDraftVersionAsync(admin.Client, prefix);

        var addSheet = await admin.Client.PutAsJsonAsync(
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

        var addTable = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/sheets/SHEET1/tables/TABLE1", UriKind.Relative),
            new
            {
                nameL10n = new Dictionary<string, string> { ["en"] = $"{prefix} table" },
                ordinal = 1,
                layoutKind = "PerPeriodInstance",

                // ⛔ Саме `Fixed`: склад рядків задає шаблон, а не оператор —
                // це і є таблиця, яку `S-13` вимагає бачити з рядками.
                rowMode = "Fixed",
                maxDynamicRows = (int?)null,
            });
        Assert.True(addTable.StatusCode == HttpStatusCode.OK, $"{addTable.StatusCode}: {app.ErrorsText}");
        var tableDefId = (await addTable.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var addColumn = await admin.Client.PutAsJsonAsync(
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
        Assert.True(addColumn.StatusCode == HttpStatusCode.OK, $"{addColumn.StatusCode}: {app.ErrorsText}");

        string[] rowKeys = ["R1", "R2"];
        for (var i = 0; i < rowKeys.Length; i++)
        {
            var addRow = await admin.Client.PutAsJsonAsync(
                new Uri($"/api/v1/template-versions/{versionId}/tables/{tableDefId}/rows/{rowKeys[i]}", UriKind.Relative),
                new
                {
                    labelL10n = new Dictionary<string, string> { ["en"] = $"Row {i + 1}" },
                    ordinal = i + 1,
                    rowKind = "Item",
                    parentRowKey = (string?)null,
                    isReadOnly = false,
                });
            Assert.True(addRow.StatusCode == HttpStatusCode.OK, $"{addRow.StatusCode}: {app.ErrorsText}");
        }

        if (validationExpression is not null)
        {
            var addRule = await admin.Client.PutAsJsonAsync(
                new Uri($"/api/v1/template-versions/{versionId}/tables/{tableDefId}/validation-rules/{prefix}RULE", UriKind.Relative),
                new
                {
                    severity = "Error",

                    // ⚠ Рівень РЯДКА (1): саме він має назвати адресу — рядок,
                    // у якому порушення. Комірковий (0) блокує запис і сюди не
                    // дійшов би, а табличний (2) адреси рядка не має.
                    scope = (byte)1,
                    expression = validationExpression,
                    messageL10n = new Dictionary<string, string> { ["en"] = "A must stay under the cap" },
                    columnDefId = (int?)null,
                    isActive = true,
                });
            Assert.True(addRule.StatusCode == HttpStatusCode.OK, $"{addRule.StatusCode}: {app.ErrorsText}");
        }

        var publish = await admin.Client.PostAsync(
            new Uri($"/api/v1/template-versions/{versionId}/publish", UriKind.Relative), content: null);
        Assert.True(publish.StatusCode == HttpStatusCode.NoContent, $"{publish.StatusCode}: {app.ErrorsText}");

        var policiesResponse = await admin.Client.GetAsync(
            new Uri("/api/v1/projects/period-policies", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, policiesResponse.StatusCode);
        var policyId = (await policiesResponse.Content.ReadFromJsonAsync<JsonElement>())[0]
            .GetProperty("id").GetInt32();

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

        await ProjectAndPeriodScenarios.ActivateProjectAsync(admin.Client, projectId);
        await Provisioning.GrantAsync(app, admin.RoleId, "Project", projectId, "Manage");
        admin = await Provisioning.ReauthenticateAsync(app, admin);

        // ⛔ Період береться ВІДКРИТИЙ, а не «перший у календарі». Записувати
        // можна лише у відкритий (`ФВ-1.12`), і саме `W8` зробив його
        // відкритим одразу після активації (`S-11`) — доти цього періоду тут
        // просто не існувало б.
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
        var documentId = (await createDoc.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("documentId").GetInt64();

        return new RealDocument(
            admin, projectId, documentId, periodKey, sheetDefId, tableDefId, "A", rowKeys);
    }

    /// <summary>Проєкт зі звітним роком 2019 — усі періоди давно поза HardClose.</summary>
    private static async Task<int> CreateOldProjectAsync(HttpClient client, string prefix)
    {
        var policiesResponse = await client.GetAsync(new Uri("/api/v1/projects/period-policies", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, policiesResponse.StatusCode);
        var policies = await policiesResponse.Content.ReadFromJsonAsync<JsonElement>();
        var policyId = policies[0].GetProperty("id").GetInt32();

        // ⚠ Без версії шаблону обробник відмовляє з ECR-TMPL-0404 попри
        // номінально опційний тип поля (докладніше — ProjectAndPeriodScenarios.CreateProjectAsync).
        var versionId = await StructureScenarios.CreateEmptyDraftVersionAsync(client, prefix);

        var code = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var create = await client.PostAsJsonAsync(
            new Uri("/api/v1/projects", UriKind.Relative),
            new
            {
                code,
                nameL10n = new Dictionary<string, string> { ["en"] = $"{prefix} project" },
                timeZoneId = "Asia/Almaty",
                periodKind = "Monthly",
                year = 2019,
                templateVersionId = versionId,
                periodPolicyId = policyId,
            });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        return (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("projectId").GetInt32();
    }

    /// <summary>Опитує календар періодів, поки якийсь не набуде <paramref name="state"/>.</summary>
    private static async Task<int?> AwaitPeriodStateAsync(
        HttpClient client, int projectId, string state, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var response = await client.GetAsync(new Uri($"/api/v1/projects/{projectId}/periods", UriKind.Relative));
            if (response.IsSuccessStatusCode)
            {
                var calendar = await response.Content.ReadFromJsonAsync<JsonElement>();
                foreach (var period in calendar.GetProperty("periods").EnumerateArray())
                {
                    if (string.Equals(period.GetProperty("state").GetString(), state, StringComparison.Ordinal))
                    {
                        return period.GetProperty("periodKey").GetInt32();
                    }
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        return null;
    }
}
