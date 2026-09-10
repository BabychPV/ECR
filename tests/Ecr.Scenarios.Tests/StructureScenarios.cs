using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Scenarios.Tests;

/// <summary>§6.2 директиви — авторство структури, ядро MVP: S-03..S-09.</summary>
/// <remarks>
/// ⛔ `W5` (директива №09, зрізи `W5.0`…`W5.4`) закрив авторство ВСІЄЇ
/// структури шаблону через API — аркуш, таблиця, колонка, рядок, формула,
/// правило валідації, правило доступу до періоду — усі маршрути перелічені
/// в `docs/build/02-contracts.md` §9. `S-04`…`S-08` тепер доводять РЕАЛЬНУ
/// поведінку через реальну структуру, а не «маршруту немає» (404): де
/// `GET .../structure` (Правило 3 §3.2) ще не показує якоїсь сутності
/// (формули, правила — задокументований пропуск відповідних `W5.x`),
/// доказ персистентності — ПОВТОРНИЙ запис за тією самою адресою, а не
/// `SELECT`.
/// </remarks>
[Collection("SqlServer")]
public sealed class StructureScenarios(SqlServerFixture sql)
{
    /// <summary>
    /// S-03. Створити шаблон і ПЕРШУ версію (без клону), і мати змогу з нею
    /// працювати.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-03")]
    public async Task Створення_шаблону_і_першої_версії_без_клону()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S03", ["Template.View", "Template.Edit"]);

        var templateCode = $"S03_{Guid.NewGuid():N}"[..20];
        var createTemplate = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/templates", UriKind.Relative),
            new { code = templateCode, nameL10n = new Dictionary<string, string> { ["en"] = "S-03 template" } });
        Assert.Equal(HttpStatusCode.Created, createTemplate.StatusCode);

        var templateId = (await createTemplate.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("templateId").GetInt32();

        // Перша версія — БЕЗ клону (CloneFromVersionId = null).
        var createVersion = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/templates/{templateId}/versions", UriKind.Relative),
            new { versionNumber = "1.0.0.0", cloneFromVersionId = (int?)null });
        Assert.Equal(HttpStatusCode.Created, createVersion.StatusCode);

        var versionId = (await createVersion.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("versionId").GetInt32();

        // «Мати змогу з нею працювати» — версія читається тим самим API, яким
        // її щойно створили: чернетка бачить свою (порожню) структуру.
        var structure = await admin.Client.GetAsync(
            new Uri($"/api/v1/template-versions/{versionId}/structure", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, structure.StatusCode);
    }

    /// <summary>
    /// S-04. Аркуш і таблиця — через API (колонки, рядки лишаються S-05..S-07,
    /// коли `W5.2` додасть свої маршрути).
    /// </summary>
    /// <remarks>
    /// ⛔ `W5.0` додав <c>PUT /template-versions/{id}/sheets/{code}</c>,
    /// `W5.1` — <c>PUT …/sheets/{sheetCode}/tables/{code}</c>: адреса — КОД
    /// (аркуша, потім таблиці в межах аркуша), і задає його викликач
    /// (`D2-147`), тому дія одна — створення і зміна не розрізняються.
    /// Перевірка йде через ту саму точку входу, якою читає клієнт
    /// (<c>GET …/structure</c>), а не через `SELECT` (Правило 3 §3.2) —
    /// інакше зелений тест доводив би лише запис у базу, а не те, що
    /// адміністратор БАЧИТЬ додані аркуш і таблицю.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-04")]
    public async Task Аркуш_і_таблиця_через_API()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S04", ["Template.View", "Template.Edit"]);

        var versionId = await CreateEmptyDraftVersionAsync(admin.Client, "S04");

        var addSheet = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/sheets/SHEET1", UriKind.Relative),
            new
            {
                nameL10n = new Dictionary<string, string> { ["en"] = "Sheet 1" },
                ordinal = 1,
                sheetGroup = (string?)null,
                isMandatory = true,
                isVisible = true,
            });
        Assert.Equal(HttpStatusCode.OK, addSheet.StatusCode);

        var addTable = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/sheets/SHEET1/tables/TABLE1", UriKind.Relative),
            new
            {
                nameL10n = new Dictionary<string, string> { ["en"] = "Table 1" },
                ordinal = 1,
                layoutKind = "PerPeriodInstance",
                rowMode = "Fixed",
                maxDynamicRows = (int?)null,
            });
        Assert.True(
            addTable.StatusCode == HttpStatusCode.OK, $"{addTable.StatusCode}: {app.ErrorsText}");

        var structure = await admin.Client.GetAsync(
            new Uri($"/api/v1/template-versions/{versionId}/structure", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, structure.StatusCode);

        var sheets = (await structure.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("sheets").EnumerateArray().ToList();

        // ⛔ Не лише "непорожньо": код і саме той аркуш/таблиця, які щойно
        // додали — порожня перевірка пройшла б і на випадковому чужому.
        Assert.Contains(sheets, s => s.GetProperty("code").GetString() == "SHEET1");
        var sheet1 = sheets.Single(s => s.GetProperty("code").GetString() == "SHEET1");
        var tables = sheet1.GetProperty("tables").EnumerateArray().ToList();
        Assert.Contains(tables, tb => tb.GetProperty("code").GetString() == "TABLE1");
    }

    /// <summary>
    /// S-05. Формула колонки, збережена у версії (<c>W5.3</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ `GET .../structure` (Правило 3 §3.2 — верифікація через ту саму
    /// точку входу, якою читає клієнт) ще не показує наявних формул —
    /// задокументований пропуск `W5.3` (`TemplateColumnDto` не несе поля
    /// формули; додати його означало б правити
    /// `TemplateStructureDto.cs`/`GetTemplateStructureHandler.cs`, поза
    /// межами того зрізу). Тому доказ персистентності — ПОВТОРНИЙ `PUT` за
    /// тією самою адресою (`tableDefId`/`scope`/`target`): якщо перший запис
    /// не зберігся, другий запис на ту саму ціль поводився б як створення, а
    /// не як заміна, і повернув би інше значення `Id`.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-05")]
    public async Task Формула_колонки_збережена_у_версії()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S05", ["Template.View", "Template.Edit", "Calculation.View"]);

        var versionId = await CreateEmptyDraftVersionAsync(admin.Client, "S05");

        var addSheet = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/sheets/SHEET1", UriKind.Relative),
            new
            {
                nameL10n = new Dictionary<string, string> { ["en"] = "Sheet 1" },
                ordinal = 1,
                sheetGroup = (string?)null,
                isMandatory = true,
                isVisible = true,
            });
        Assert.Equal(HttpStatusCode.OK, addSheet.StatusCode);

        var addTable = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/sheets/SHEET1/tables/TABLE1", UriKind.Relative),
            new
            {
                nameL10n = new Dictionary<string, string> { ["en"] = "Table 1" },
                ordinal = 1,
                layoutKind = "PerPeriodInstance",
                rowMode = "Fixed",
                maxDynamicRows = (int?)null,
            });
        Assert.True(addTable.StatusCode == HttpStatusCode.OK, $"{addTable.StatusCode}: {app.ErrorsText}");
        var tableId = (await addTable.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var addColumn = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/tables/{tableId}/columns/C", UriKind.Relative),
            new
            {
                headerL10n = new Dictionary<string, string> { ["en"] = "C" },
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
        var columnId = (await addColumn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var saveFormula = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/tables/{tableId}/formulas/column/{columnId}", UriKind.Relative),
            new { dialect = "Template", expression = "A + B" });
        Assert.True(saveFormula.StatusCode == HttpStatusCode.OK, $"{saveFormula.StatusCode}: {app.ErrorsText}");
        var formulaId = (await saveFormula.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        // ⛔ Головне твердження: ПОВТОРНИЙ запис на ту саму ціль — той самий
        // `Id` формули. `PUT` за адресою `(tableDefId, scope, target)`, а не
        // за кодом самої формули (вона його не має), тому ідентичність
        // доводить саме це — інакше другий виклик завів би ДРУГУ формулу на
        // тій самій колонці, а Rule 3 забороняє перевіряти це `SELECT`-ом.
        var resaveFormula = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/tables/{tableId}/formulas/column/{columnId}", UriKind.Relative),
            new { dialect = "Template", expression = "A + B + 1" });
        Assert.True(resaveFormula.StatusCode == HttpStatusCode.OK, $"{resaveFormula.StatusCode}: {app.ErrorsText}");
        var resavedBody = await resaveFormula.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(formulaId, resavedBody.GetProperty("id").GetInt32());
        Assert.Equal("A + B + 1", resavedBody.GetProperty("expression").GetString());
    }

    /// <summary>Правило валідації таблиці (<c>W5.4</c>): те саме твердження, що й S-05 — синтаксично.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-06")]
    public async Task Правило_валідації_заводиться_і_зберігається()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S06", ["Template.View", "Template.Edit"]);

        var versionId = await CreateEmptyDraftVersionAsync(admin.Client, "S06");

        var addSheet = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/sheets/SHEET1", UriKind.Relative),
            new
            {
                nameL10n = new Dictionary<string, string> { ["en"] = "Sheet 1" },
                ordinal = 1,
                sheetGroup = (string?)null,
                isMandatory = true,
                isVisible = true,
            });
        Assert.Equal(HttpStatusCode.OK, addSheet.StatusCode);

        var addTable = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/sheets/SHEET1/tables/TABLE1", UriKind.Relative),
            new
            {
                nameL10n = new Dictionary<string, string> { ["en"] = "Table 1" },
                ordinal = 1,
                layoutKind = "PerPeriodInstance",
                rowMode = "Fixed",
                maxDynamicRows = (int?)null,
            });
        Assert.True(addTable.StatusCode == HttpStatusCode.OK, $"{addTable.StatusCode}: {app.ErrorsText}");
        var tableId = (await addTable.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var addRule = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/tables/{tableId}/validation-rules/RULE1", UriKind.Relative),
            new
            {
                severity = "Error",
                scope = (byte)0,
                expression = "A >= 0",
                messageL10n = new Dictionary<string, string> { ["en"] = "must be non-negative" },
                columnDefId = (int?)null,
                isActive = true,
            });
        Assert.True(addRule.StatusCode == HttpStatusCode.OK, $"{addRule.StatusCode}: {app.ErrorsText}");

        // ⛔ Той самий довід, що й S-05: `GET .../structure` не носить правил
        // валідації (`TableDto` їх не виставляє), тому персистентність
        // доводить ПОВТОРНИЙ `PUT` за тим самим кодом — ідемпотентна заміна,
        // а не друге правило з тим самим кодом (яке `UQ_ValidationRule`
        // взагалі заборонив би на рівні бази).
        var resaveRule = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/tables/{tableId}/validation-rules/RULE1", UriKind.Relative),
            new
            {
                severity = "Warning",
                scope = (byte)0,
                expression = "A >= 0",
                messageL10n = new Dictionary<string, string> { ["en"] = "must be non-negative" },
                columnDefId = (int?)null,
                isActive = true,
            });
        Assert.True(resaveRule.StatusCode == HttpStatusCode.OK, $"{resaveRule.StatusCode}: {app.ErrorsText}");
        var resavedRule = await resaveRule.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Warning", resavedRule.GetProperty("severity").GetString());
    }

    /// <summary>Правило доступу до періоду (<c>W5.4</c>): та сама зв'язка сутностей, що й S-06.</summary>
    /// <remarks>
    /// ⛔ `POST`, а не `PUT` за кодом: `PeriodAccessRuleDef` не має природного
    /// коду взагалі, лише `Id`, призначений базою ПІСЛЯ створення
    /// (`PeriodAccessRuleHandlers.cs`). Доказ персистентності тому інший, ніж
    /// у S-05/S-06: не повторний запис за тією самою адресою (бо адреси до
    /// створення не існує), а `PUT` за щойно отриманим `Id` — якщо `Id` не
    /// був реальним, звернення до нього дало б `404`, а не `200`.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-07")]
    public async Task Правило_доступу_до_періоду_заводиться()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S07", ["Template.View", "Template.Edit"]);

        var versionId = await CreateEmptyDraftVersionAsync(admin.Client, "S07");

        var addSheet = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/sheets/SHEET1", UriKind.Relative),
            new
            {
                nameL10n = new Dictionary<string, string> { ["en"] = "Sheet 1" },
                ordinal = 1,
                sheetGroup = (string?)null,
                isMandatory = true,
                isVisible = true,
            });
        Assert.Equal(HttpStatusCode.OK, addSheet.StatusCode);

        var addTable = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/sheets/SHEET1/tables/TABLE1", UriKind.Relative),
            new
            {
                nameL10n = new Dictionary<string, string> { ["en"] = "Table 1" },
                ordinal = 1,
                layoutKind = "PerPeriodInstance",
                rowMode = "Fixed",
                maxDynamicRows = (int?)null,
            });
        Assert.True(addTable.StatusCode == HttpStatusCode.OK, $"{addTable.StatusCode}: {app.ErrorsText}");
        var tableId = (await addTable.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var addRule = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/period-access-rules", UriKind.Relative),
            new
            {
                ruleKind = "AlwaysReadOnly",
                onOutOfWindow = "ReadOnly",
                sheetDefId = (int?)null,
                tableDefId = tableId,
                roleId = (int?)null,
                rowKind = (string?)null,
                fromSequence = (byte?)null,
                toSequence = (byte?)null,
                sourceColumnDefId = (int?)null,
                relativeOffset = (short?)null,
                conditionExpr = (string?)null,
            });
        Assert.True(addRule.StatusCode == HttpStatusCode.Created, $"{addRule.StatusCode}: {app.ErrorsText}");
        var ruleId = (await addRule.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var confirmRule = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/period-access-rules/{ruleId}", UriKind.Relative),
            new
            {
                onOutOfWindow = "Warn",
                sheetDefId = (int?)null,
                tableDefId = tableId,
                roleId = (int?)null,
                rowKind = (string?)null,
            });
        Assert.True(confirmRule.StatusCode == HttpStatusCode.OK, $"{confirmRule.StatusCode}: {app.ErrorsText}");
        var confirmedRule = await confirmRule.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ruleId, confirmedRule.GetProperty("id").GetInt32());
        Assert.Equal("Warn", confirmedRule.GetProperty("onOutOfWindow").GetString());
    }

    /// <summary>
    /// S-08. Публікація відхиляє зламане: незакрита дужка в РЕАЛЬНІЙ формулі
    /// збереженої структури (<c>W5.3</c> зробив цю формулу можливою).
    /// </summary>
    /// <remarks>
    /// ⛔ `SaveFormulaDefHandler` не перевіряє синтаксис на запис — вираз
    /// зберігається сирим текстом, і саме тому цей сценарій може ПОКЛАСТИ
    /// зламане в структуру: перевірка (`FormulaEngine.Parse`, `PublishChecks.
    /// CheckExpression`) настає лише на публікації, будуючи граф залежностей
    /// (`PublishChecks.cs`). Раніше (до `W5.3`) довести це можна було тільки
    /// синтаксисом БЕЗ прив'язки до версії — тепер `SUM(A, B` лежить у
    /// справжній колонці справжньої таблиці, і публікація відмовляє РІВНО
    /// тому, чому має.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-08")]
    public async Task Публікація_відхиляє_зламане()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S08", ["Template.View", "Template.Edit", "Template.Publish", "Calculation.View"]);

        var versionId = await CreateEmptyDraftVersionAsync(admin.Client, "S08");

        var addSheet = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/sheets/SHEET1", UriKind.Relative),
            new
            {
                nameL10n = new Dictionary<string, string> { ["en"] = "Sheet 1" },
                ordinal = 1,
                sheetGroup = (string?)null,
                isMandatory = true,
                isVisible = true,
            });
        Assert.Equal(HttpStatusCode.OK, addSheet.StatusCode);

        var addTable = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/sheets/SHEET1/tables/TABLE1", UriKind.Relative),
            new
            {
                nameL10n = new Dictionary<string, string> { ["en"] = "Table 1" },
                ordinal = 1,
                layoutKind = "PerPeriodInstance",
                rowMode = "Fixed",
                maxDynamicRows = (int?)null,
            });
        Assert.True(addTable.StatusCode == HttpStatusCode.OK, $"{addTable.StatusCode}: {app.ErrorsText}");
        var tableId = (await addTable.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var addColumn = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/tables/{tableId}/columns/C", UriKind.Relative),
            new
            {
                headerL10n = new Dictionary<string, string> { ["en"] = "C" },
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
        var columnId = (await addColumn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        // ⛔ Незакрита дужка. `PUT` приймає її мовчки (запис не валідує
        // синтаксис) — саме це і є доказом того, ЩО перевіряє публікація, а
        // не запис.
        var saveFormula = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/tables/{tableId}/formulas/column/{columnId}", UriKind.Relative),
            new { dialect = "Template", expression = "SUM(A, B" });
        Assert.True(saveFormula.StatusCode == HttpStatusCode.OK, $"{saveFormula.StatusCode}: {app.ErrorsText}");

        var publish = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/publish", UriKind.Relative),
            new { reason = "S-08 звірка" });

        // ⛔ Не лише «не 204»: код має бути САМЕ той, що каталог відводить
        // непридатній структурі публікації (`ECR-TMPL-0422`,
        // `ErrorCodes.TemplateInvalid`) — `ECR-TMPL-4221`
        // (`ErrorCodes.FormulaCycle`) окремий і вужчий: він лише для ЦИКЛУ в
        // графі залежностей, а незакрита дужка не будує графа взагалі
        // (`FormulaEngine.Parse` падає раніше). Довірити «якийсь 4xx» —
        // означало б довести лише «щось відмовило», а не «відмовило через це».
        Assert.Equal(HttpStatusCode.UnprocessableEntity, publish.StatusCode);
        var publishBody = await publish.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ECR-TMPL-0422", publishBody.GetProperty("errorCode").GetString());
    }

    /// <summary>
    /// S-09. Публікація приймає справне і будує граф; версія з нулем
    /// аркушів отримує відмову від сервера.
    /// </summary>
    /// <remarks>
    /// ⚠ Друга половина («приймає справне і будує граф `&gt; 0`») стала
    /// доказовою лише з `W5`: до нього дати публікації хоч один аркуш не
    /// було звідки. Тепер обидві половини — в одному сценарії, бо це та сама
    /// перевірка з двох боків: порожнє відмовляє, непорожнє проходить.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-09")]
    public async Task Публікація_версії_з_нулем_аркушів_відмовляє_а_зі_структурою_проходить()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S09", ["Template.View", "Template.Edit", "Template.Publish"]);

        var emptyVersionId = await CreateEmptyDraftVersionAsync(admin.Client, "S09empty");

        var publishEmpty = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{emptyVersionId}/publish", UriKind.Relative),
            new { reason = "S-09 звірка: порожня версія" });

        // Директива очікує 422 (ECR-TMPL-0422); довіряємо контракту, а не
        // числу — головне, щоб це НЕ БУВ успіх (204).
        Assert.NotEqual(HttpStatusCode.NoContent, publishEmpty.StatusCode);
        Assert.True(
            (int)publishEmpty.StatusCode >= 400,
            $"публікація порожньої версії мала відмовити, а повернула {publishEmpty.StatusCode}: {app.ErrorsText}");

        var versionId = await CreateEmptyDraftVersionAsync(admin.Client, "S09full");

        var addSheet = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/sheets/SHEET1", UriKind.Relative),
            new
            {
                nameL10n = new Dictionary<string, string> { ["en"] = "Sheet 1" },
                ordinal = 1,
                sheetGroup = (string?)null,
                isMandatory = true,
                isVisible = true,
            });
        Assert.Equal(HttpStatusCode.OK, addSheet.StatusCode);

        var addTable = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/sheets/SHEET1/tables/TABLE1", UriKind.Relative),
            new
            {
                nameL10n = new Dictionary<string, string> { ["en"] = "Table 1" },
                ordinal = 1,
                layoutKind = "PerPeriodInstance",
                rowMode = "Fixed",
                maxDynamicRows = (int?)null,
            });
        Assert.True(addTable.StatusCode == HttpStatusCode.OK, $"{addTable.StatusCode}: {app.ErrorsText}");
        var tableId = (await addTable.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var addColumn = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/tables/{tableId}/columns/A", UriKind.Relative),
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

        var addRow = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/tables/{tableId}/rows/ROW1", UriKind.Relative),
            new
            {
                labelL10n = new Dictionary<string, string> { ["en"] = "Row 1" },
                ordinal = 1,
                rowKind = "Item",
                parentRowKey = (string?)null,
                isReadOnly = false,
            });
        Assert.True(addRow.StatusCode == HttpStatusCode.OK, $"{addRow.StatusCode}: {app.ErrorsText}");

        var publishFull = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/publish", UriKind.Relative),
            new { reason = "S-09 звірка: справна структура" });

        Assert.True(
            publishFull.StatusCode == HttpStatusCode.NoContent,
            $"публікація версії зі справною структурою мала пройти, а повернула {publishFull.StatusCode}: {app.ErrorsText}");
    }

    /// <summary>
    /// T5 (директива №11, `#30`). Публікація без причини відхиляється, а
    /// справжня причина — не літерал <c>"Publish"</c> — потрапляє в журнал.
    /// </summary>
    /// <remarks>
    /// ⛔ До цього ендпоінт не мав поля <c>reason</c> взагалі, а
    /// <c>PublishTemplateVersionHandler</c> писав у <c>aud.PublicationEvent</c>
    /// однаковий літерал <c>"Publish"</c> на кожен виклик — рядок, що ВИГЛЯДАЄ
    /// як причина, але нею не є. Тест доводить обидві половини виправлення на
    /// СПРАВНІЙ структурі (та сама версія, що й вище): порожня причина
    /// відхиляється до діагностик структури, а прийнята причина доходить до
    /// журналу дослівно.
    ///
    /// ⚠ Структура версії СПРАВНА (той самий `versionId`, щойно з аркушем,
    /// таблицею, колонкою й рядком): відмова публікації з порожньою причиною
    /// тут не може пояснюватися нічим іншим, окрім самої причини.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-09")]
    public async Task Публікація_без_причини_відхиляється_а_причина_потрапляє_в_журнал()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S09reason", ["Template.View", "Template.Edit", "Template.Publish"]);

        var versionId = await CreateEmptyDraftVersionAsync(admin.Client, "S09reason");

        await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/sheets/SHEET1", UriKind.Relative),
            new
            {
                nameL10n = new Dictionary<string, string> { ["en"] = "Sheet 1" },
                ordinal = 1,
                sheetGroup = (string?)null,
                isMandatory = true,
                isVisible = true,
            });

        var addTable = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/sheets/SHEET1/tables/TABLE1", UriKind.Relative),
            new
            {
                nameL10n = new Dictionary<string, string> { ["en"] = "Table 1" },
                ordinal = 1,
                layoutKind = "PerPeriodInstance",
                rowMode = "Fixed",
                maxDynamicRows = (int?)null,
            });
        var tableId = (await addTable.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/tables/{tableId}/columns/A", UriKind.Relative),
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

        await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/tables/{tableId}/rows/ROW1", UriKind.Relative),
            new
            {
                labelL10n = new Dictionary<string, string> { ["en"] = "Row 1" },
                ordinal = 1,
                rowKind = "Item",
                parentRowKey = (string?)null,
                isReadOnly = false,
            });

        // ⛔ Причина — порожній рядок, а не пропущене поле: `Reason` в
        // `PublishVersionRequest` незаперечно required (без нього модель узагалі
        // не зв'яжеться), і саме порожній/пробільний рядок — той випадок, який
        // раніше не перевіряв ніхто.
        var publishBlank = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/publish", UriKind.Relative),
            new { reason = "   " });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, publishBlank.StatusCode);
        var blankBody = await publishBlank.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ECR-TMPL-0422", blankBody.GetProperty("errorCode").GetString());

        const string realReason = "T5 звірка: перша публікація версії S09reason";

        var publishReal = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/publish", UriKind.Relative),
            new { reason = realReason });

        Assert.True(
            publishReal.StatusCode == HttpStatusCode.NoContent,
            $"справна структура з непорожньою причиною мала опублікуватися, а повернула "
            + $"{publishReal.StatusCode}: {app.ErrorsText}");

        // ⛔ Головний доказ: журнал несе СПРАВЖНЮ причину, а не літерал
        // "Publish". Якби виклик перестав передавати `reason` далі (мутація
        // D-134), тут був би саме він.
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ChangeReason
            FROM   aud.PublicationEvent
            WHERE  EntityType = 'TemplateVersion' AND EntityId = @v;
            """;
        command.Parameters.AddWithValue("@v", versionId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "публікація мала лишити подію в aud.PublicationEvent");
        Assert.Equal(realReason, reader.GetString(0));
        Assert.False(await reader.ReadAsync(), "відхилена публікація без причини не мала писати другий запис");
    }

    /// <summary>Створює шаблон і чернеткову версію без клону — спільний перший крок S-04..S-09.</summary>
    internal static async Task<int> CreateEmptyDraftVersionAsync(HttpClient client, string prefix)
    {
        var templateCode = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var createTemplate = await client.PostAsJsonAsync(
            new Uri("/api/v1/templates", UriKind.Relative),
            new { code = templateCode, nameL10n = new Dictionary<string, string> { ["en"] = $"{prefix} template" } });
        Assert.Equal(HttpStatusCode.Created, createTemplate.StatusCode);
        var templateId = (await createTemplate.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("templateId").GetInt32();

        var createVersion = await client.PostAsJsonAsync(
            new Uri($"/api/v1/templates/{templateId}/versions", UriKind.Relative),
            new { versionNumber = "1.0.0.0", cloneFromVersionId = (int?)null });
        Assert.Equal(HttpStatusCode.Created, createVersion.StatusCode);

        return (await createVersion.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("versionId").GetInt32();
    }
}
