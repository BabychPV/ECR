using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scenarios.Tests;

/// <summary>
/// Шапка документа: поле рівня версії шаблону, значення на документ,
/// <c>HDR.Code</c> у РЕАЛЬНОМУ перерахунку.
/// </summary>
/// <remarks>
/// ⛔ <b>Це не доказ маршруту.</b> До цієї роботи сховище значень шапки не
/// існувало взагалі, і всі три реалізації <c>GetHeader()</c>
/// (<c>RecalculationService</c>, <c>ValidationEvaluationContext</c>,
/// <c>MethodologyEvaluationContext</c>) були заглушками. Формула з
/// <c>HDR.Area</c> уже парситься граматикою й розкриває залежність
/// (<c>cfg.FormulaDependency.DependsOnKind = 1</c>) — увесь конвеєр ДО
/// читання значення вже існував і був би зеленим на заглушці теж, якби тест
/// перевіряв лише «формула не впала». Доказ тут — КОНКРЕТНЕ число: клітинка
/// формули дорівнює РІВНО тому, що записано в шапку, а не порожньому чи
/// нульовому значенню.
/// </remarks>
[Collection("SqlServer")]
public sealed class HeaderScenarios(SqlServerFixture sql)
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Requirement", "HDR-foundation")]
    public async Task HDR_у_формулі_шаблону_читає_реальне_значення_шапки_документа()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "HDR1",
            [
                "Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Template.Publish",
                "Calculation.Recalculate",
            ]);

        // 1. Документ РЕАЛЬНОЇ структури: поле шапки `Area` (String) на рівні
        //    версії, і колонка-формула `HdrEcho`, чий ЄДИНИЙ вираз — `HDR.Area`.
        //    Жодного посилання на комірку немає навмисно: якби формула читала
        //    ще й `[A]`, збіг результату можна було б списати на випадковість
        //    входу, а не на шапку.
        var doc = await DataEntryScenarios.ArrangeRealDocumentAsync(
            app, admin, "HDR1",
            formulaColumn: ("HdrEcho", "HDR.Area"),
            headerField: ("Area", "String"));
        admin = doc.Admin;

        // ⛔ Екземпляр таблиці МАТЕРІАЛІЗУЄТЬСЯ читанням (`GetDocumentTablesHandler`,
        // запис на шляху читання) — до першого GET .../tables його в
        // doc.TableInstance немає взагалі, і повний перерахунок нижче не мав
        // би що рахувати. Той самий порядок кроків, що в CalculationScenarios
        // (S-21): читання адреси таблиці — ПЕРШИЙ крок, до будь-якого запису.
        var tables0 = await admin.Client.GetAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/tables?periodKey={doc.PeriodKey}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, tables0.StatusCode);
        var tableInstanceId = (await tables0.Content.ReadFromJsonAsync<JsonElement>())[0]
            .GetProperty("tableInstanceId").GetInt64();

        // 2. ДО запису шапки: GET .../header повертає поле з порожнім Value —
        //    воно існує у версії шаблону, але жодного значення документ ще не має.
        var before = await admin.Client.GetAsync(new Uri($"/api/v1/documents/{doc.DocumentId}/header", UriKind.Relative));
        Assert.True(before.StatusCode == HttpStatusCode.OK, $"{before.StatusCode}: {app.ErrorsText}");
        var beforeBody = await before.Content.ReadFromJsonAsync<JsonElement>();
        var beforeField = beforeBody.GetProperty("fields").EnumerateArray()
            .First(f => f.GetProperty("code").GetString() == "Area");
        Assert.Equal(JsonValueKind.Null, beforeField.GetProperty("value").ValueKind);

        // 3. PATCH .../header записує значення шапки документа.
        var patchHeader = await admin.Client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/header", UriKind.Relative),
            new { fields = new[] { new { code = "Area", value = (object?)"Kashagan", isEmpty = false } } });
        Assert.True(
            patchHeader.StatusCode == HttpStatusCode.OK,
            $"{patchHeader.StatusCode}: {await patchHeader.Content.ReadAsStringAsync()}; {app.ErrorsText}");

        var patched = await patchHeader.Content.ReadFromJsonAsync<JsonElement>();
        var patchedField = patched.GetProperty("fields").EnumerateArray()
            .First(f => f.GetProperty("code").GetString() == "Area");
        Assert.Equal("Kashagan", patchedField.GetProperty("value").GetString());

        // 4. Перерахунок — ЯВНИЙ і ПОВНИЙ. Формула HdrEcho не має жодної
        //    коміркової залежності (лише HDR.Area), тому інкрементний прогін
        //    від правки комірки її не зачепив би — `POST …/recalculate`
        //    єдиний шлях, який тут рахує все.
        var recalc = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/recalculate", UriKind.Relative),
            new { periodKey = doc.PeriodKey, sheetDefId = (int?)null });
        Assert.True(recalc.StatusCode == HttpStatusCode.Accepted, $"{recalc.StatusCode}: {app.ErrorsText}");

        // 5. ГОЛОВНЕ ТВЕРДЖЕННЯ: клітинка HdrEcho першого рядка стає РІВНО
        //    "Kashagan" — те саме значення, що щойно записане в шапку, а не
        //    null/порожньо (заглушка) і не будь-який інший текст.
        var value = await AwaitTextCellAsync(
            app, admin.Client, doc.DocumentId, tableInstanceId, doc.RowKeys[0], "HdrEcho", "Kashagan");
        Assert.Equal("Kashagan", value);

        // 6. Симетрія з (2): GET .../header тепер віддає те саме значення, що
        //    щойно прочитала формула — той самий словник, що бачить
        //    IEvaluationContext.GetHeader, видно і клієнту.
        var after = await admin.Client.GetAsync(new Uri($"/api/v1/documents/{doc.DocumentId}/header", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        var afterField = (await after.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("fields").EnumerateArray().First(f => f.GetProperty("code").GetString() == "Area");
        Assert.Equal("Kashagan", afterField.GetProperty("value").GetString());
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Requirement", "HDR-foundation")]
    public async Task PATCH_header_невідомий_код_поля_дає_ECR_HDR_0404()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "HDR2",
            ["Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Template.Publish"]);

        var doc = await DataEntryScenarios.ArrangeRealDocumentAsync(app, admin, "HDR2");
        admin = doc.Admin;

        var patch = await admin.Client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/header", UriKind.Relative),
            new { fields = new[] { new { code = "NoSuchField", value = (object?)"x", isEmpty = false } } });

        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        var body = await patch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ECR-HDR-0404", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Requirement", "HDR-foundation")]
    public async Task Поле_шапки_дублюється_за_кодом_на_повторному_PUT()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "HDR3",
            ["Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Template.Publish", "Template.View"]);

        var versionId = await StructureScenarios.CreateEmptyDraftVersionAsync(admin.Client, "HDR3");

        var first = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/header-fields/Region", UriKind.Relative),
            new
            {
                labelL10n = new Dictionary<string, string> { ["en"] = "Region v1" },
                ordinal = 0,
                dataType = "String",
                isRequired = false,
                lookupRegistryDefId = (int?)null,
            });
        Assert.True(first.StatusCode == HttpStatusCode.OK, $"{first.StatusCode}: {app.ErrorsText}");

        // ⛔ Той самий контракт, що колонка (`D2-147`): повторний PUT тим самим
        // кодом ЗМІНЮЄ поле, а не заводить друге.
        var second = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/header-fields/Region", UriKind.Relative),
            new
            {
                labelL10n = new Dictionary<string, string> { ["en"] = "Region v2" },
                ordinal = 0,
                dataType = "String",
                isRequired = true,
                lookupRegistryDefId = (int?)null,
            });
        Assert.True(second.StatusCode == HttpStatusCode.OK, $"{second.StatusCode}: {app.ErrorsText}");

        var list = await admin.Client.GetAsync(
            new Uri($"/api/v1/template-versions/{versionId}/header-fields", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var fields = (await list.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        var region = Assert.Single(fields, f => f.GetProperty("code").GetString() == "Region");
        Assert.True(region.GetProperty("isRequired").GetBoolean());
    }

    /// <summary>Опитує зріз, поки текстова клітинка не стане очікуваним значенням.</summary>
    private static async Task<string?> AwaitTextCellAsync(
        EcrApiFactory app, HttpClient client, long documentId, long tableInstanceId,
        string rowKey, string columnCode, string expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        string? last = null;

        while (DateTime.UtcNow < deadline)
        {
            var slice = await client.GetAsync(
                new Uri($"/api/v1/documents/{documentId}/tables/{tableInstanceId}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, slice.StatusCode);

            var body = await slice.Content.ReadFromJsonAsync<JsonElement>();
            var row = body.GetProperty("rows").EnumerateArray()
                .FirstOrDefault(r => r.GetProperty("rowKey").GetString() == rowKey);

            if (row.ValueKind == JsonValueKind.Object
                && row.GetProperty("cells").TryGetProperty(columnCode, out var cell)
                && cell.ValueKind == JsonValueKind.String)
            {
                last = cell.GetString();
                if (last == expected)
                {
                    return last;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        Assert.Fail(
            $"клітинка {columnCode} рядка {rowKey} за 30 с не стала \"{expected}\" (останнє: "
            + $"{last ?? "порожньо"}); {app.ErrorsText}");

        return last;
    }
}
