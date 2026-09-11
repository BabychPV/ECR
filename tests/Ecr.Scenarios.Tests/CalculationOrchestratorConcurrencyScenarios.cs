using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scenarios.Tests;

/// <summary>
/// Q-249 (Хвиля 3, лінія 2): мутаційний доказ на РЕАЛЬНІЙ базі для гонки
/// <c>EcrDbContext</c> у <c>CalculationOrchestrator.RunAsync</c>.
/// </summary>
/// <remarks>
/// ⛔ <b>Що ламалося.</b> Пакет із ≥2 незалежних методологій рахується
/// ПАРАЛЕЛЬНО (`Parallel.ForEachAsync`, `MaxDegreeOfParallelism = 4`,
/// `CalculationOrchestrator.cs`) заради бюджету ≤10 хв (ПРД-13). До фіксу
/// кожна паралельна гілка викликала `resolver.MatchRowsAsync` /
/// `inputBuilder.BuildAsync` / `outputWriter.WriteAsync` — усі троє Scoped
/// (`Ecr.Calculations/DependencyInjection.cs`) і зрештою обгортають ОДИН
/// `EcrDbContext`, який дає `QuartzJobAdapter` єдиним на всю задачу
/// (`using var scope = services.CreateScope();`). Дві гілки, що одночасно
/// звертаються до одного `DbContext`, дають `InvalidOperationException: A
/// second operation started on this context before a previous operation
/// completed` — рівно в тому сценарії, заради якого паралелізм існує.
///
/// ⛔ <b>Чому не юніт-тест.</b> `CalculationOrchestratorTests` (`Ecr.Application.Tests`)
/// перевіряє лише побудову графа `CalculationPlan.Build` — усі залежності
/// там NSubstitute-моки, і жоден реальний `DbContext` між гілками не
/// ділиться, тож гонка там не могла спрацювати НІКОЛИ, хоч би скільки
/// незалежних гілок було в тесті. Довести саме ЦЕЙ дефект можна лише
/// реальним `EcrDbContext` під реальним паралелізмом — тому тест тут,
/// end-to-end, через реальний HTTP і реальну чергу Quartz (`QuartzJobAdapter`
/// створює РІВНО ОДИН scope на задачу — той самий шлях, що й у проді).
///
/// ⛔ <b>Чому саме дві колонки, дві методології.</b> `CalculationPlan.Build`
/// кладе прив'язки в ОДИН пакет, поки в графі немає залежностей між
/// методологіями (а сьогодні їх немає — `CalculationOrchestrator.cs`, п.2).
/// Отже ДВІ незалежні методології, прив'язані до РІЗНИХ колонок ОДНОГО
/// документа, гарантовано потрапляють в один пакет — і `Parallel.ForEachAsync`
/// з `MaxDegreeOfParallelism = 4` запускає обидві гілки одночасно.
///
/// ⚠ Мутаційний доказ (протокол директиви §5.3.6): цей тест ЗАПУСКАЛИ на
/// оригінальному (до фіксу) `CalculationOrchestrator.cs` — ЧЕРВОНИЙ
/// (`Failed` задачі, `InvalidOperationException: A second operation started
/// on this context before a previous operation completed` у лозі сервера) —
/// і на виправленому — ЗЕЛЕНИЙ (`Succeeded`, обидва виходи мають правильне
/// число). Обидва результати записані в `docs/build/questions/Q-249.md`.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationOrchestratorConcurrencyScenarios(SqlServerFixture sql)
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "Q-249")]
    public async Task Два_незалежні_методології_одного_документа_рахуються_без_гонки_DbContext()
    {
        using var app = new EcrApiFactory(sql);

        var admin = await Provisioning.AdministratorAsync(
            app,
            "Q249",
            [
                "Project.Manage", "Document.View", "Document.Create", "Template.View", "Template.Edit",
                "Template.Publish", "Calculation.View", "Calculation.EditFormula", "Calculation.EditConstant",
                "Calculation.EditRule", "Calculation.Recalculate", "System.ViewHealth",
            ]);

        // ⛔ Публікує ІНШИЙ користувач — правило чотирьох очей (`D-40`)
        // забороняє публікувати власну правку.
        var publisher = await Provisioning.AdministratorAsync(
            app, "Q249Pub", ["Calculation.Publish", "Calculation.View"]);

        var doc = await DataEntryScenarios.ArrangeRealDocumentAsync(
            app, admin, "Q249", extraNumericColumns: ["OUT1", "OUT2"]);
        admin = doc.Admin;

        var tableInstanceId = await TableInstanceAsync(app, admin.Client, doc.DocumentId, doc.PeriodKey);

        // A записується в ОБИДВА фіксовані рядки: правило відбору нижче
        // (ALL_ROWS, matchJson "{}") бере обидва, і рядок без A дав би
        // відмову формули з причини, яка тут не перевіряється.
        foreach (var rowKey in doc.RowKeys)
        {
            await WriteAAsync(app, admin.Client, doc.DocumentId, tableInstanceId, doc.PeriodKey, rowKey, 4m);
        }

        var units = await admin.Client.GetAsync(new Uri("/api/v1/units", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, units.StatusCode);
        var unitId = (await units.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .First(u => string.Equals(u.GetProperty("code").GetString(), "t", StringComparison.Ordinal))
            .GetProperty("id").GetInt32();

        var (out1ColumnId, out2ColumnId) = await ResultColumnIdsAsync(admin.Client, doc.VersionId);

        // ⚠ ДВІ незалежні методології на ДВОХ різних колонках того самого
        // документа — саме той пакет, на якому гілки `RunAsync` рахувалися
        // паралельно і до фіксу ділили один `EcrDbContext`.
        await ArrangeMethodologyAsync(
            app, admin, publisher, "Q249m1", "OUT1RESULT", out1ColumnId, doc, tableInstanceId, unitId);
        await ArrangeMethodologyAsync(
            app, admin, publisher, "Q249m2", "OUT2RESULT", out2ColumnId, doc, tableInstanceId, unitId);

        var recalc = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/recalculate", UriKind.Relative),
            new { periodKey = doc.PeriodKey });
        Assert.Equal(HttpStatusCode.Accepted, recalc.StatusCode);
        var jobId = (await recalc.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!;

        var final = await ScenarioHelpers.AwaitJobAsync(admin.Client, jobId, TimeSpan.FromSeconds(60));
        Assert.True(final.ValueKind != JsonValueKind.Undefined, $"стан задачі {jobId} не прочитався: {app.ErrorsText}");
        var state = final.GetProperty("state").GetString();

        // ⛔ ГОЛОВНЕ ТВЕРДЖЕННЯ (Q-249). До фіксу тут — `Failed`, і лог
        // сервера (`app.ErrorsText`) несе `InvalidOperationException: A
        // second operation started on this context before a previous
        // operation completed`: доказ зафіксовано текстом у
        // `docs/build/questions/Q-249.md` (RED), отриманим саме з цього
        // тесту на оригінальному коді.
        Assert.True(
            string.Equals(state, "Succeeded", StringComparison.Ordinal),
            "перерахунок документа з ДВОМА незалежними методологіями в одному пакеті "
            + $"завершився станом {state} (Q-249: очікувана форма гонки DbContext у "
            + $"паралельних гілках CalculationOrchestrator.RunAsync): {final.GetRawText()}; "
            + $"лог сервера: {app.ErrorsText}");

        var results = await admin.Client.GetAsync(
            new Uri(
                $"/api/v1/documents/{doc.DocumentId}/calculation-results?periodKey={doc.PeriodKey}",
                UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, results.StatusCode);
        var resultsBody = await results.Content.ReadFromJsonAsync<JsonElement>();

        var out1 = resultsBody.EnumerateArray().FirstOrDefault(
            r => string.Equals(r.GetProperty("outputCode").GetString(), "OUT1RESULT", StringComparison.Ordinal));
        var out2 = resultsBody.EnumerateArray().FirstOrDefault(
            r => string.Equals(r.GetProperty("outputCode").GetString(), "OUT2RESULT", StringComparison.Ordinal));

        Assert.True(
            out1.ValueKind != JsonValueKind.Undefined,
            $"немає результату OUT1RESULT після перерахунку: {resultsBody.GetRawText()}; {app.ErrorsText}");
        Assert.True(
            out2.ValueKind != JsonValueKind.Undefined,
            $"немає результату OUT2RESULT після перерахунку: {resultsBody.GetRawText()}; {app.ErrorsText}");

        // ⚠ Обидва числа — те саме A (тотожна формула `@A`), тому що обидві
        // методології незалежні й рахують ОДНАКОВИЙ вхід: збіг довів би, що
        // жодна гілка не отримала «чужий» чи пошкоджений результат через
        // спільний DbContext іншої гілки.
        Assert.Equal(4m, out1.GetProperty("value").GetDecimal());
        Assert.Equal(4m, out2.GetProperty("value").GetDecimal());
    }

    /// <summary>Заводить методологію з тотожною формулою <c>@A</c>, публікує і прив'язує до колонки.</summary>
    private static async Task ArrangeMethodologyAsync(
        EcrApiFactory app, Provisioning.Administrator admin, Provisioning.Administrator publisher,
        string prefix, string outputCode, int columnDefId, DataEntryScenarios.RealDocument doc,
        long tableInstanceId, int unitId)
    {
        var methodologyId = await CreateMethodologyAsync(app, admin.Client, prefix);
        var versionId = await CreateDraftVersionAsync(app, admin.Client, methodologyId, "1.0.0");

        await SaveFormulaAsync(app, admin.Client, methodologyId, versionId, outputCode, "@A", "A", unitId);
        await SaveOutputAsync(app, admin.Client, methodologyId, versionId, outputCode, unitId);
        await SaveRuleAsync(app, admin.Client, methodologyId, versionId, "ALL_ROWS", "{}", priority: 1);
        await SaveTestCaseAsync(
            app, admin.Client, methodologyId, versionId, "GOLDEN",
            Input(doc, tableInstanceId, argument: 4m), $$"""{"{{outputCode}}":4}""");

        await SaveBindingAsync(app, admin.Client, methodologyId, columnDefId, outputCode);

        await PublishAsync(app, publisher.Client, methodologyId, versionId, EffectiveFrom(doc.PeriodKey, yearsBack: 1));
    }

    /// <summary>Ідентифікатори колонок <c>OUT1</c>/<c>OUT2</c> зі структури версії.</summary>
    private static async Task<(int Out1, int Out2)> ResultColumnIdsAsync(HttpClient client, int versionId)
    {
        var structure = await client.GetAsync(
            new Uri($"/api/v1/template-versions/{versionId}/structure", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, structure.StatusCode);

        var sheets = (await structure.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("sheets").EnumerateArray().ToList();
        var columns = sheets
            .SelectMany(s => s.GetProperty("tables").EnumerateArray())
            .SelectMany(t => t.GetProperty("columns").EnumerateArray())
            .ToList();

        int ColumnId(string code) => columns
            .First(c => string.Equals(c.GetProperty("code").GetString(), code, StringComparison.Ordinal))
            .GetProperty("id").GetInt32();

        return (ColumnId("OUT1"), ColumnId("OUT2"));
    }

    /// <summary>Єдиний екземпляр таблиці документа за період.</summary>
    private static async Task<long> TableInstanceAsync(
        EcrApiFactory app, HttpClient client, long documentId, int periodKey)
    {
        var tables = await client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables?periodKey={periodKey}", UriKind.Relative));
        Assert.True(tables.StatusCode == HttpStatusCode.OK, $"таблиці: {tables.StatusCode}: {app.ErrorsText}");
        var tableArray = await tables.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(tableArray.GetArrayLength() > 0, $"документ {documentId} не має жодної таблиці: {app.ErrorsText}");

        return tableArray[0].GetProperty("tableInstanceId").GetInt64();
    }

    /// <summary>Пише значення <c>A</c> у заданий рядок.</summary>
    private static async Task WriteAAsync(
        EcrApiFactory app, HttpClient client, long documentId, long tableInstanceId, int periodKey,
        string rowKey, decimal value)
    {
        var slice = await client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables/{tableInstanceId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, slice.StatusCode);
        var sliceBody = await slice.Content.ReadFromJsonAsync<JsonElement>();

        var row = sliceBody.GetProperty("rows").EnumerateArray()
            .FirstOrDefault(r => string.Equals(r.GetProperty("rowKey").GetString(), rowKey, StringComparison.Ordinal));
        Assert.True(row.ValueKind == JsonValueKind.Object, $"рядка {rowKey} у зрізі {tableInstanceId} немає.");
        var baseVersion = row.GetProperty("rowVersion").GetString();

        var patch = await client.PatchAsJsonAsync(
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
                        cells = new object[] { new { columnCode = "A", value } },
                    },
                },
            });

        Assert.True(patch.StatusCode == HttpStatusCode.OK, $"запис A у {rowKey}: {patch.StatusCode}: {app.ErrorsText}");
    }

    private static async Task<int> CreateMethodologyAsync(EcrApiFactory app, HttpClient client, string prefix)
    {
        var code = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/methodologies", UriKind.Relative),
            new
            {
                code,
                nameL10n = new Dictionary<string, string> { ["en"] = $"{prefix} methodology" },
                kind = "DataDriven",
                group = (string?)null,
            });

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"створення методології: {response.StatusCode}: {app.ErrorsText}");

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static async Task<int> CreateDraftVersionAsync(
        EcrApiFactory app, HttpClient client, int methodologyId, string versionNumber)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/methodologies/{methodologyId}/versions", UriKind.Relative),
            new { versionNumber, copyFromVersionId = (int?)null, level = "Configuration" });

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"версія {versionNumber}: {response.StatusCode}: {app.ErrorsText}");

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static async Task SaveFormulaAsync(
        EcrApiFactory app, HttpClient client, int methodologyId, int versionId,
        string code, string expression, string argumentsCsv, int unitId)
    {
        var response = await client.PutAsJsonAsync(
            new Uri($"/api/v1/methodologies/{methodologyId}/versions/{versionId}/formulas/{code}", UriKind.Relative),
            new { expression, resultType = "Number", outputUnitId = unitId, argumentsCsv });

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"формула {code}: {response.StatusCode}: {app.ErrorsText}");
    }

    private static async Task SaveOutputAsync(
        EcrApiFactory app, HttpClient client, int methodologyId, int versionId, string code, int unitId)
    {
        var response = await client.PutAsJsonAsync(
            new Uri($"/api/v1/methodologies/{methodologyId}/versions/{versionId}/outputs/{code}", UriKind.Relative),
            new { unitId, ordinal = 1 });

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"вихід {code}: {response.StatusCode}: {app.ErrorsText}");
    }

    private static async Task SaveRuleAsync(
        EcrApiFactory app, HttpClient client, int methodologyId, int versionId,
        string code, string matchJson, int priority)
    {
        var response = await client.PutAsJsonAsync(
            new Uri($"/api/v1/methodologies/{methodologyId}/versions/{versionId}/rules/{code}", UriKind.Relative),
            new { matchJson, priority, isActive = true });

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"правило {code}: {response.StatusCode}: {app.ErrorsText}");
    }

    private static async Task SaveTestCaseAsync(
        EcrApiFactory app, HttpClient client, int methodologyId, int versionId,
        string code, string inputJson, string expectedJson)
    {
        var response = await client.PutAsJsonAsync(
            new Uri($"/api/v1/methodologies/{methodologyId}/versions/{versionId}/tests/{code}", UriKind.Relative),
            new { inputJson, expectedJson, tolerance = 0.0001m });

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"тест {code}: {response.StatusCode}: {app.ErrorsText}");
    }

    private static async Task SaveBindingAsync(
        EcrApiFactory app, HttpClient client, int methodologyId, int columnDefId, string outputCode)
    {
        var response = await client.PutAsJsonAsync(
            new Uri($"/api/v1/methodologies/{methodologyId}/bindings/{columnDefId}/{outputCode}", UriKind.Relative),
            new { matchJson = "{}", isActive = true });

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"прив'язка {outputCode}: {response.StatusCode}: {app.ErrorsText}");
    }

    private static async Task PublishAsync(
        EcrApiFactory app, HttpClient publisherClient, int methodologyId, int versionId, string effectiveFrom)
    {
        var response = await publisherClient.PostAsJsonAsync(
            new Uri($"/api/v1/methodologies/{methodologyId}/versions/{versionId}/publish", UriKind.Relative),
            new { changeReason = "Q-249: мутаційний доказ гонки DbContext", effectiveFrom });

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"публікація версії {versionId}: {response.StatusCode}: {app.ErrorsText}");
    }

    /// <summary>Вхід золотого набору у формі <c>CalculationInput</c> — лише аргумент <c>A</c>.</summary>
    private static string Input(DataEntryScenarios.RealDocument doc, long tableInstanceId, decimal argument)
        => string.Create(
            CultureInfo.InvariantCulture,
            $$"""
              {"documentId":{{doc.DocumentId}},"tableInstanceId":{{tableInstanceId}},
               "periodKey":{"value":{{doc.PeriodKey}}},"sourceRowKey":"{{doc.RowKeys[0]}}",
               "arguments":[
                 {"argumentCode":"A","value":{{argument}},"valueString":null,"unitId":null}]}
              """);

    private static string EffectiveFrom(int periodKey, int yearsBack)
        => new DateOnly((periodKey / 100) - yearsBack, 1, 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
