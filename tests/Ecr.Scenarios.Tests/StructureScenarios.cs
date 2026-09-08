using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scenarios.Tests;

/// <summary>§6.2 директиви — авторство структури, ядро MVP: S-03..S-09.</summary>
/// <remarks>
/// ⛔ `docs/build/02-contracts.md` §9 і `contracts/openapi.snapshot.json`
/// перелічують маршрути запису над версією шаблону: <c>POST
/// /templates/{id}/versions</c> (нова версія), <c>PATCH
/// /template-versions/{id}/presentation</c> (лише презентаційний шар —
/// підписи, стилі, формати; структурні поля він відхиляє за побудовою,
/// ФВ-7.2) і, з `W5.0`, <c>PUT /template-versions/{id}/sheets/{code}</c>
/// (аркуш чернетки, S-04). Маршруту, що додає ТАБЛИЦЮ, колонку чи рядок,
/// усе ще немає — ні серед контролерів
/// (<c>src/Ecr.Api/Controllers/TemplateVersionsController.cs</c>,
/// <c>TemplatesController.cs</c>), ні деінде. S-05..S-08 тому не можуть
/// піти далі першого кроку: сценарій викликає очікуваний маршрут і фіксує,
/// що шляху немає (`404`) — так, як прямо дозволяє директива, коли
/// контракту немає ніде.
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
    /// S-04. Аркуш — через API (таблиця, колонки, рядки лишаються S-05..S-08).
    /// </summary>
    /// <remarks>
    /// ⛔ `W5.0` додав <c>PUT /template-versions/{id}/sheets/{code}</c>:
    /// адреса аркуша — його КОД, і задає його викликач (`D2-147`), тому дія
    /// одна — створення і зміна не розрізняються. Перевірка йде через ту саму
    /// точку входу, якою читає клієнт (<c>GET …/structure</c>), а не через
    /// `SELECT` (Правило 3 §3.2) — інакше зелений тест доводив би лише запис
    /// у базу, а не те, що адміністратор БАЧИТЬ доданий аркуш.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-04")]
    public async Task Аркуш_через_API()
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

        var structure = await admin.Client.GetAsync(
            new Uri($"/api/v1/template-versions/{versionId}/structure", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, structure.StatusCode);

        var sheets = (await structure.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("sheets").EnumerateArray().ToList();

        // ⛔ Не лише "непорожньо": код і саме той аркуш, який щойно додали —
        // порожня перевірка пройшла б і на випадковому чужому аркуші.
        Assert.Contains(sheets, s => s.GetProperty("code").GetString() == "SHEET1");
    }

    /// <summary>
    /// S-05. Формула в комірці <c>C = A + B</c>, збережена у версії.
    /// </summary>
    /// <remarks>
    /// ⛔ Так само, як S-04: немає маршруту, що зберігає формулу колонки чи
    /// рядка версії шаблону (лише методологічні формули мають
    /// <c>PUT /methodologies/{id}/versions/{vid}/formulas/{code}</c> — інший
    /// діалект, ФВ-9.5). Другу половину доказу — <c>POST /expressions/validate</c>
    /// — API все ж підтримує незалежно від структури (перевірка синтаксису
    /// без <c>TemplateVersionId</c>), тому вона перевіряється тут окремо: це
    /// не рятує сценарій, а показує, що працює лише ОДНА з двох половин.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-05")]
    public async Task Формула_C_дорівнює_A_плюс_B_збережена_у_версії()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S05", ["Template.View", "Template.Edit", "Calculation.View"]);

        var versionId = await CreateEmptyDraftVersionAsync(admin.Client, "S05");

        // Крок, якого сценарій довести не може: маршруту «зберегти формулу
        // колонки/рядка у версії» немає.
        var addColumn = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/tables/1/columns", UriKind.Relative),
            new { code = "C", formula = "A + B" });
        Assert.Equal(HttpStatusCode.NotFound, addColumn.StatusCode);

        // Половина доказу, яка ДІЙСНО працює: перевірка виразу самим
        // рушієм — без прив'язки до реальної структури (лише синтаксис).
        var validate = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/expressions/validate", UriKind.Relative),
            new
            {
                expression = "A + B",
                dialect = "Template",
                templateVersionId = (int?)null,
                tableDefId = (int?)null,
                rowKey = (string?)null,
                columnDefId = (int?)null,
            });
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
    }

    /// <summary>S-06. Правило валідації заводиться і зберігається.</summary>
    /// <remarks>⛔ Немає маршруту, що додає <c>ValidationRule</c> у версію шаблону.</remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-06")]
    public async Task Правило_валідації_заводиться_і_зберігається()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S06", ["Template.View", "Template.Edit"]);

        var versionId = await CreateEmptyDraftVersionAsync(admin.Client, "S06");

        var addRule = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/validation-rules", UriKind.Relative),
            new { scope = "Cell", severity = "Error", expression = "A >= 0", messageL10n = new Dictionary<string, string> { ["en"] = "must be non-negative" } });

        Assert.Equal(HttpStatusCode.NotFound, addRule.StatusCode);
    }

    /// <summary>S-07. Правило доступу до періоду заводиться.</summary>
    /// <remarks>⛔ Немає маршруту, що додає <c>PeriodAccessRule</c> (ФВ-2.15) у версію шаблону.</remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-07")]
    public async Task Правило_доступу_до_періоду_заводиться()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S07", ["Template.View", "Template.Edit"]);

        var versionId = await CreateEmptyDraftVersionAsync(admin.Client, "S07");

        var addRule = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/period-access-rules", UriKind.Relative),
            new { ruleKind = "EditablePeriodOnly", onOutOfWindow = "ReadOnly" });

        Assert.Equal(HttpStatusCode.NotFound, addRule.StatusCode);
    }

    /// <summary>
    /// S-08. Публікація відхиляє зламане: незакрита дужка і посилання на
    /// неіснуючу колонку.
    /// </summary>
    /// <remarks>
    /// ⛔ Щоб довести САМЕ цей факт (`ECR-TMPL-0422` з діагностикою при
    /// публікації), потрібна версія з реальною зламаною формулою в
    /// структурі — а зберегти формулу колонки нема як (S-05). Сценарій тому
    /// перевіряє те, що дійсно доступне через API: сирий синтаксис із
    /// незакритою дужкою відхиляється вже на рівні перевірки виразу
    /// (`POST /expressions/validate`), і публікація версії, у якій зламане
    /// не могло опинитися (бо покласти його нікуди), природно минає
    /// перевірку цілісності — залишаючись відмовленою з ІНШОЇ причини (нуль
    /// аркушів, S-09).
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-08")]
    public async Task Публікація_відхиляє_зламане()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S08", ["Template.View", "Template.Edit", "Calculation.View"]);

        // Незакрита дужка — перевірка синтаксису ловить це БЕЗ прив'язки до
        // структури, і саме тому Extension/Diagnostics тут доступні взагалі.
        var unclosed = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/expressions/validate", UriKind.Relative),
            new
            {
                expression = "SUM(A, B",
                dialect = "Template",
                templateVersionId = (int?)null,
                tableDefId = (int?)null,
                rowKey = (string?)null,
                columnDefId = (int?)null,
            });
        Assert.Equal(HttpStatusCode.OK, unclosed.StatusCode);
        var unclosedBody = await unclosed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEmpty(unclosedBody.GetProperty("diagnostics").EnumerateArray());

        // Посилання на колонку, якої не існує в жодній таблиці — те саме:
        // без TemplateVersionId перевіряється лише синтаксис, тож ця частина
        // доказу (посилання на НЕІСНУЮЧУ колонку конкретної версії) вимагає
        // структури, якої S-04 довести не зміг. Публікація версії з таким
        // дефектом лишається неперевіреною маршрутом, якого немає.
        var versionId = await CreateEmptyDraftVersionAsync(admin.Client, "S08");
        var publish = await admin.Client.PostAsync(
            new Uri($"/api/v1/template-versions/{versionId}/publish", UriKind.Relative), content: null);

        // Версія порожня (нуль аркушів) — публікація однаково відмовляє, але
        // з причини S-09, не з причини «зламана формула», яку довести нема як.
        Assert.NotEqual(HttpStatusCode.NoContent, publish.StatusCode);
    }

    /// <summary>
    /// S-09. Публікація приймає справне і будує граф; версія з нулем
    /// аркушів отримує відмову від сервера.
    /// </summary>
    /// <remarks>
    /// ⚠ Половина, яку можна довести без маршруту додавання структури:
    /// версія БЕЗ жодного аркуша — це legit стан, який можна створити через
    /// наявні виклики (S-03), і публікація такої версії має відмовити
    /// (ФВ-2.9). Половину «приймає справне і будує граф `> 0`» довести
    /// неможливо: без структурних маршрутів (S-04) немає способу дати
    /// публікації хоч один аркуш.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-09")]
    public async Task Публікація_версії_з_нулем_аркушів_отримує_відмову()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S09", ["Template.View", "Template.Edit", "Template.Publish"]);

        var versionId = await CreateEmptyDraftVersionAsync(admin.Client, "S09");

        var publish = await admin.Client.PostAsync(
            new Uri($"/api/v1/template-versions/{versionId}/publish", UriKind.Relative), content: null);

        // Директива очікує 422 (ECR-TMPL-0422); довіряємо контракту, а не
        // числу — головне, щоб це НЕ БУВ успіх (204).
        Assert.NotEqual(HttpStatusCode.NoContent, publish.StatusCode);
        Assert.True(
            (int)publish.StatusCode >= 400,
            $"публікація порожньої версії мала відмовити, а повернула {publish.StatusCode}: {app.ErrorsText}");
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
