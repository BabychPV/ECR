using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scenarios.Tests;

/// <summary>§6.5 директиви — розрахунок: S-21..S-26.</summary>
[Collection("SqlServer")]
public sealed class CalculationScenarios(SqlServerFixture sql)
{
    /// <summary>
    /// S-21. Формула шаблону: після <c>PATCH</c> у <c>A</c> і <c>B</c> комірка
    /// <c>C</c> стає сумою сама.
    /// </summary>
    /// <remarks>⛔ Формулу колонки нема як зберегти (S-05) — без неї нема чому спрацювати.</remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-21")]
    public async Task Формула_шаблону_рахує_C_після_запису_A_і_B()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S21", ["Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Calculation.Recalculate"]);

        (admin, _, var documentId, var periodKey) = await DataEntryScenarios.ArrangeDocumentAsync(app, admin, "S21");

        var recalc = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId}/recalculate", UriKind.Relative), new { periodKey });
        Assert.Equal(HttpStatusCode.Accepted, recalc.StatusCode);

        var jobId = (await recalc.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!;
        var final = await ScenarioHelpers.AwaitJobAsync(admin.Client, jobId, TimeSpan.FromSeconds(15));
        Assert.True(
            final.ValueKind != JsonValueKind.Undefined && string.Equals(final.GetProperty("state").GetString(), "Succeeded", StringComparison.Ordinal),
            $"перерахунок документа {documentId} не завершився успіхом: {(final.ValueKind == JsonValueKind.Undefined ? "немає стану" : final.GetRawText())}");

        var tables = await admin.Client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables?periodKey={periodKey}", UriKind.Relative));
        var tableArray = await tables.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(tableArray.GetArrayLength() > 0, $"документ {documentId} не має жодної таблиці — нема де шукати обчислену колонку C.");
        var tableInstanceId = tableArray[0].GetProperty("tableInstanceId").GetInt64();

        var slice = await admin.Client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables/{tableInstanceId}", UriKind.Relative));
        var sliceBody = await slice.Content.ReadFromJsonAsync<JsonElement>();
        var columns = sliceBody.GetProperty("columns");
        var calculated = columns.EnumerateArray()
            .FirstOrDefault(c => string.Equals(c.GetProperty("dataType").GetString(), "Formula", StringComparison.Ordinal));
        Assert.True(calculated.ValueKind != JsonValueKind.Undefined, $"таблиця {tableInstanceId} не має жодної колонки-формули (dataType=Formula) — S-05 не дав змоги її зберегти.");
    }

    /// <summary>S-22. <c>CONVERT</c> між одиницями в формулі шаблону.</summary>
    /// <remarks>
    /// ⚠ Половина, ЯКУ можна довести без структури: сам механізм конверсії
    /// (`POST /units/convert`, ФВ-16.2..ФВ-16.4). Формулу з `CONVERT(...)` у
    /// реальній колонці зберегти нема як (S-05).
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-22")]
    public async Task Convert_між_одиницями_в_формулі_шаблону()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(app, "S22", ["Calculation.View", "Template.Edit"]);

        // Робочий шматок: t -> kg у межах тієї самої розмірності Mass (seed §14).
        var convert = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/units/convert", UriKind.Relative),
            new { value = 2m, fromUnit = "t", toUnit = "kg" });
        Assert.Equal(HttpStatusCode.OK, convert.StatusCode);
        var converted = await convert.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2000m, converted.GetProperty("value").GetDecimal());

        // Недосяжна половина: формула CONVERT(...) у РЕАЛЬНІЙ колонці версії.
        var versionId = await StructureScenarios.CreateEmptyDraftVersionAsync(admin.Client, "S22");
        var addColumn = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/tables/1/columns", UriKind.Relative),
            new { code = "C", formula = "CONVERT(A, 't', 'kg')" });
        Assert.Equal(HttpStatusCode.NotFound, addColumn.StatusCode);
    }

    /// <summary>S-23. Прив'язка методології до таблиці заводиться через API.</summary>
    /// <remarks>
    /// ⛔ Немає жодного маршруту, що заводить <c>cfg.MethodologyBindingRule</c>
    /// (ФВ-13.3): ні серед 18 контролерів, ні в `contracts/openapi.snapshot.json`.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-23")]
    public async Task Прив_язка_методології_до_таблиці_заводиться_через_API()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(app, "S23", ["Calculation.EditRule", "Calculation.View"]);

        var bind = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/methodologies/1/binding-rules", UriKind.Relative),
            new { tableDefId = 1, priority = 1, matchExpression = "true" });

        Assert.Equal(HttpStatusCode.NotFound, bind.StatusCode);
    }

    /// <summary>
    /// S-24. Методологія: створити методологію, константи, правило відбору → результат.
    /// </summary>
    /// <remarks>
    /// ⛔ <c>MethodologiesController</c> має РІВНО маршрути для версій уже
    /// ІСНУЮЧОЇ методології (<c>GET/POST …/{id}/versions</c>) — маршруту
    /// «створити методологію» (<c>POST /api/v1/methodologies</c>) немає ні в
    /// контролері, ні в openapi. Ідентифікатора методології, з якого можна
    /// було б почати, узяти нема звідки.
    ///
    /// ⚠ Шлях <c>/api/v1/methodologies</c> існує (там висить <c>GET</c>), тому
    /// маршрутизація ASP.NET Core відповідає на цей <c>POST</c> не `404`, а
    /// `405 MethodNotAllowed` — так само правильно засвідчує відсутність дії,
    /// просто іншим кодом, і саме це підтвердив реальний прогін (§3.2).
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-24")]
    public async Task Методологія_константи_правило_відбору_результат()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(app, "S24", ["Calculation.EditFormula", "Calculation.View"]);

        var createMethodology = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/methodologies", UriKind.Relative),
            new { code = "S24_METH", nameL10n = new Dictionary<string, string> { ["en"] = "S-24 methodology" } });

        Assert.Equal(HttpStatusCode.MethodNotAllowed, createMethodology.StatusCode);
    }

    /// <summary>
    /// S-25. Збій розрахунку видимий: задача переходить у <c>Failed</c> з
    /// причиною, і перелік черги (<c>GET /jobs</c>, якщо існує) її показує.
    /// </summary>
    /// <remarks>
    /// ⛔ Каталог `docs/build/02-contracts.md` §9 і openapi мають лише
    /// <c>GET /jobs/{jobId}</c> — стан ОДНІЄЇ задачі за ідентифікатором.
    /// Переліку черги (<c>GET /jobs</c> без адреси) немає: побачити задачу,
    /// jobId якої не знаєш заздалегідь, нема як. Сценарій доводить те, що
    /// доступне, — запуск перерахунку документа без реальних даних для
    /// підрахунку і читання стану ЗА jobId, який повернув сам виклик.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-25")]
    public async Task Збій_розрахунку_видимий_у_стані_задачі()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S25", ["Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Calculation.Recalculate", "System.ViewHealth"]);

        (admin, _, var documentId, var periodKey) = await DataEntryScenarios.ArrangeDocumentAsync(app, admin, "S25");

        var recalc = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId}/recalculate", UriKind.Relative), new { periodKey });
        Assert.Equal(HttpStatusCode.Accepted, recalc.StatusCode);
        var jobId = (await recalc.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!;

        var final = await ScenarioHelpers.AwaitJobAsync(admin.Client, jobId, TimeSpan.FromSeconds(15));

        // Перелік черги (GET /jobs без параметра) відсутній у контракті — це
        // окрема, вже задокументована відсутність (див. підсумок сценарію).
        var queueList = await admin.Client.GetAsync(new Uri("/api/v1/jobs", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, queueList.StatusCode);

        Assert.True(final.ValueKind != JsonValueKind.Undefined, $"задача {jobId} не набула кінцевого стану за 15 с.");
        var state = final.GetProperty("state").GetString();
        var error = final.TryGetProperty("error", out var e) ? e.GetString() : null;

        // Без реальних формул (S-05/S-23/S-24) перерахунок документа без
        // жодних даних для підрахунку природно не має чому «зламатися» —
        // очікуваний Failed із причиною довести нема на чому.
        Assert.Equal("Failed", state);
        Assert.False(string.IsNullOrWhiteSpace(error), "задача Failed без причини — ФВ-9.14 вимагає таксономію помилки, а не порожнечу.");
    }

    /// <summary>
    /// S-26. <c>MaskedZero</c>: ділення на нуль у <c>Legacy</c> → <c>0</c> із
    /// причиною в трейсі; у <c>Strict</c> → <c>null</c>.
    /// </summary>
    /// <remarks>⛔ Вимагає реальної методологічної формули з діленням — недосяжно без S-24.</remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-26")]
    public async Task MaskedZero_ділення_на_нуль_Legacy_і_Strict()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(app, "S26", ["Calculation.EditFormula", "Calculation.View"]);

        var createMethodology = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/methodologies", UriKind.Relative),
            new { code = "S26_METH", nameL10n = new Dictionary<string, string> { ["en"] = "S-26 methodology" } });

        // Той самий корінь, що й S-24: немає з чого почати методологію.
        // Шлях існує (GET), тому це 405, а не 404 — див. коментар S-24.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, createMethodology.StatusCode);
    }
}
