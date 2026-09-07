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
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-13")]
    public async Task Fixed_таблиця_віддає_свої_рядки_без_POST_rows()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S13", ["Project.Manage", "Document.View", "Document.Create", "Template.Edit"]);

        (admin, var projectId, var documentId, var periodKey) = await ArrangeDocumentAsync(app, admin, "S13");

        var tables = await admin.Client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables?periodKey={periodKey}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, tables.StatusCode);

        var tableArray = await tables.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(
            tableArray.GetArrayLength() > 0,
            $"документ {documentId} проєкту {projectId} не має жодної таблиці: без маршруту додавання таблиці " +
            "(S-04) неможливо дати документу Fixed-таблицю з рядками.");

        var tableInstanceId = tableArray[0].GetProperty("tableInstanceId").GetInt64();
        var slice = await admin.Client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables/{tableInstanceId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, slice.StatusCode);

        var sliceBody = await slice.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(
            sliceBody.GetProperty("rows").GetArrayLength() > 0,
            "Fixed-таблиця мала віддати власні рядки з RowDef без жодного POST /rows, а рядків немає.");
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

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-19")]
    public async Task Валідація_віддає_повідомлення_а_не_лише_кількість()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S19", ["Project.Manage", "Document.View", "Document.Create", "Template.Edit"]);

        (admin, _, var documentId, var periodKey) = await ArrangeDocumentAsync(app, admin, "S19");

        var validate = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId}/validate", UriKind.Relative),
            new { periodKey });
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);

        var body = await validate.Content.ReadFromJsonAsync<JsonElement>();
        var messages = body.GetProperty("messages");

        // Доказ: непорожній список ІЗ адресою комірки (rowKey/columnCode), а
        // не просто число. Без правил валідації (S-06 недосяжний) список
        // природно порожній — це і є видима межа продукту.
        Assert.True(messages.GetArrayLength() > 0, $"валідація документа {documentId} не повернула жодного повідомлення: без S-06 правил взяти нема звідки.");
        Assert.True(
            messages.EnumerateArray().Any(m => m.GetProperty("rowKey").ValueKind != JsonValueKind.Null),
            "жодне повідомлення валідації не містить адреси рядка.");
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-20")]
    public async Task Аудит_записує_старе_нове_значення_і_RowKey()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S20", ["Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Security.ViewAudit"]);

        (admin, _, var documentId, var periodKey) = await ArrangeDocumentAsync(app, admin, "S20");

        var tables = await admin.Client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables?periodKey={periodKey}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, tables.StatusCode);
        var tableArray = await tables.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(tableArray.GetArrayLength() > 0, $"документ {documentId} не має жодної таблиці — нема що змінювати для аудиту.");
        var tableInstanceId = tableArray[0].GetProperty("tableInstanceId").GetInt64();

        var slice = await admin.Client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables/{tableInstanceId}", UriKind.Relative));
        var sliceBody = await slice.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(sliceBody.GetProperty("rows").GetArrayLength() > 0, $"таблиця {tableInstanceId} не має жодного рядка.");
        var row = sliceBody.GetProperty("rows")[0];
        var rowKey = row.GetProperty("rowKey").GetString()!;
        var columnCode = sliceBody.GetProperty("columns")[0].GetProperty("code").GetString()!;

        var from = DateTime.UtcNow.AddMinutes(-1);
        var patch = await admin.Client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId,
                periodKey,
                origin = "UserEdit",
                rows = new[]
                {
                    new { rowKey, baseVersion = row.GetProperty("rowVersion").GetString(), cells = new object[] { new { columnCode, value = 7m } } },
                },
            });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var to = DateTime.UtcNow.AddMinutes(1);
        var audit = await admin.Client.GetAsync(new Uri(
            $"/api/v1/audit/cells?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}&documentId={documentId}&limit=50",
            UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);

        var auditBody = await audit.Content.ReadFromJsonAsync<JsonElement>();
        var items = auditBody.GetProperty("items");
        Assert.True(items.GetArrayLength() > 0, $"журнал аудиту порожній для щойно зміненої комірки документа {documentId}.");

        var entry = items[0];
        Assert.Equal(rowKey, entry.GetProperty("rowKey").GetString());
        Assert.True(entry.TryGetProperty("newValue", out _), "запис аудиту не несе нового значення.");
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
