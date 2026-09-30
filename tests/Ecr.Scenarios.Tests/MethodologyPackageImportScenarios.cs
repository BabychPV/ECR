using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scenarios.Tests;

/// <summary>
/// Крок V (FEATURE-HSE301-VIEW §11.6): малий синтетичний пакет <c>ecr-methodology-package</c>
/// створює версію-чернетку через <c>POST /api/v1/methodologies/import</c>, і ця версія
/// публікується ЗВИЧАЙНОЮ дією публікації іншої людини — без обходу правил.
/// </summary>
/// <remarks>
/// ⛔ Імпорт пише лише чернетку. Золотий набір, правило відбору, вихід і прив'язку методолог
/// додає звичайними діями конфігуратора, а публікує інший користувач (<c>D-40</c>). Тест
/// доводить, що імпортований вміст (формула з <c>@A</c> і <c>CST.K</c>, константа з вікном
/// чинності) проходить усі перевірки публікації і рахує правильне число на золотому наборі.
/// <para>
/// Мутаційний доказ: зберігати в імпорті вираз без <c>CST.K</c> (напр. <c>f.Expression</c> →
/// <c>"@A"</c> у <c>ImportMethodologyPackageHandler.WriteAsync</c>) — золотий набір
/// розходиться (4 ≠ 8), публікація 422 — червоний.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyPackageImportScenarios(SqlServerFixture sql)
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "MIMP")]
    public async Task Імпортований_пакет_створює_чернетку_яку_публікує_інша_людина()
    {
        using var app = new EcrApiFactory(sql);

        var admin = await Provisioning.AdministratorAsync(
            app,
            "MIMP",
            [
                "Project.Manage", "Document.View", "Document.Create", "Template.View", "Template.Edit",
                "Template.Publish", "Calculation.View", "Calculation.EditFormula", "Calculation.EditConstant",
                "Calculation.EditRule", "Calculation.Recalculate", "Calculation.Publish", "System.ViewHealth",
            ]);
        var publisher = await Provisioning.AdministratorAsync(
            app, "MIMPPub", ["Calculation.Publish", "Calculation.View"]);

        var doc = await DataEntryScenarios.ArrangeRealDocumentAsync(app, admin, "MIMP", calculatedColumns: ["OUT1"]);
        admin = doc.Admin;

        var tableInstanceId = await TableInstanceAsync(admin.Client, doc.DocumentId, doc.PeriodKey);
        var code = $"MIMP{Guid.NewGuid():N}"[..16];

        var from = new DateOnly((doc.PeriodKey / 100) - 1, 1, 1);
        var package = new
        {
            format = "ecr-methodology-package",
            version = 1,
            library = "Common",
            methodologies = new[]
            {
                new
                {
                    name = code,
                    versions = new[]
                    {
                        new
                        {
                            version = "V1",
                            formulas = new[]
                            {
                                new
                                {
                                    name = "OUT1RESULT",
                                    version = "1",
                                    arguments = "@A;CST.K",
                                    text = "@A * CST.K",
                                    startDate = "2023-12-31T19:00:00Z",
                                    endDate = "9999-02-19T19:00:00Z",
                                    isAvailable = true,
                                    report = "",
                                },
                            },
                            constants = new[]
                            {
                                new
                                {
                                    name = "K",
                                    parameter = "K",
                                    unit = "one",
                                    values = new[]
                                    {
                                        new
                                        {
                                            category = "",
                                            version = "1",
                                            value = "2",

                                            // Опівніч Asia/Atyrau (UTC+5) — перший день року перед періодом.
                                            startDate = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5))
                                                .UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                                            endDate = "9999-02-19T19:00:00Z",
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
            blockers = Array.Empty<string>(),
        };

        var imported = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/methodologies/import?dryRun=false", UriKind.Relative), package);
        var importBody = await imported.Content.ReadAsStringAsync();
        Assert.True(imported.StatusCode == HttpStatusCode.OK, $"імпорт: {imported.StatusCode}: {importBody}");

        var report = JsonDocument.Parse(importBody).RootElement;
        var methodology = report.GetProperty("methodologies")[0];
        var methodologyId = methodology.GetProperty("methodologyId").GetInt32();
        var versionId = methodology.GetProperty("versions")[0].GetProperty("versionId").GetInt32();

        var unitId = await UnitIdAsync(admin.Client, "t");
        var columnId = await ResultColumnIdAsync(admin.Client, doc.VersionId, "OUT1");

        await PutAsync(admin.Client, $"/api/v1/methodologies/{methodologyId}/versions/{versionId}/outputs/OUT1RESULT", new { unitId, ordinal = 1 });
        await PutAsync(admin.Client, $"/api/v1/methodologies/{methodologyId}/versions/{versionId}/rules/ALL_ROWS", new { matchJson = "{}", priority = 1, isActive = true });
        await PutAsync(
            admin.Client,
            $"/api/v1/methodologies/{methodologyId}/versions/{versionId}/tests/GOLDEN",
            new { inputJson = Input(doc, tableInstanceId, 4m), expectedJson = """{"OUT1RESULT":8}""", tolerance = 0.0001m });
        await PutAsync(admin.Client, $"/api/v1/methodologies/{methodologyId}/bindings/{columnId}/OUT1RESULT", new { matchJson = "{}", isActive = true });

        // ⛔ Автор імпорту опублікувати не може — чотири ока діють і для імпортованого.
        var own = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/methodologies/{methodologyId}/versions/{versionId}/publish", UriKind.Relative),
            new { changeReason = "MIMP", effectiveFrom = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        Assert.Equal(HttpStatusCode.Conflict, own.StatusCode);

        var published = await publisher.Client.PostAsJsonAsync(
            new Uri($"/api/v1/methodologies/{methodologyId}/versions/{versionId}/publish", UriKind.Relative),
            new { changeReason = "MIMP: імпорт з AF", effectiveFrom = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        Assert.True(
            published.StatusCode == HttpStatusCode.OK,
            $"публікація: {published.StatusCode}: {await published.Content.ReadAsStringAsync()}; {app.ErrorsText}");

        var versions = await admin.Client.GetFromJsonAsync<JsonElement>(
            new Uri($"/api/v1/methodologies/{methodologyId}/versions", UriKind.Relative));
        Assert.Equal("Published", versions.EnumerateArray().Single().GetProperty("status").GetString());

        // Повтор того самого пакета після публікації — «без змін», а не конфлікт і не дубль.
        var again = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/methodologies/import?dryRun=false", UriKind.Relative), package);
        var againBody = await again.Content.ReadAsStringAsync();
        Assert.True(again.StatusCode == HttpStatusCode.OK, $"повтор: {again.StatusCode}: {againBody}");
        Assert.Equal("unchanged", JsonDocument.Parse(againBody).RootElement.GetProperty("outcome").GetString());
    }

    private static async Task PutAsync(HttpClient client, string url, object body)
    {
        var response = await client.PutAsJsonAsync(new Uri(url, UriKind.Relative), body);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<int> UnitIdAsync(HttpClient client, string code)
    {
        var units = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/units", UriKind.Relative));

        return units.EnumerateArray()
            .First(u => string.Equals(u.GetProperty("code").GetString(), code, StringComparison.Ordinal))
            .GetProperty("id").GetInt32();
    }

    private static async Task<int> ResultColumnIdAsync(HttpClient client, int versionId, string code)
    {
        var structure = await client.GetFromJsonAsync<JsonElement>(
            new Uri($"/api/v1/template-versions/{versionId}/structure", UriKind.Relative));

        return structure.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray())
            .SelectMany(t => t.GetProperty("columns").EnumerateArray())
            .First(c => string.Equals(c.GetProperty("code").GetString(), code, StringComparison.Ordinal))
            .GetProperty("id").GetInt32();
    }

    private static async Task<long> TableInstanceAsync(HttpClient client, long documentId, int periodKey)
    {
        var tables = await client.GetFromJsonAsync<JsonElement>(
            new Uri($"/api/v1/documents/{documentId}/tables?periodKey={periodKey}", UriKind.Relative));

        return tables[0].GetProperty("tableInstanceId").GetInt64();
    }

    private static string Input(DataEntryScenarios.RealDocument doc, long tableInstanceId, decimal argument)
        => string.Create(
            CultureInfo.InvariantCulture,
            $$"""
              {"documentId":{{doc.DocumentId}},"tableInstanceId":{{tableInstanceId}},
               "periodKey":{"value":{{doc.PeriodKey}}},"sourceRowKey":"{{doc.RowKeys[0]}}",
               "arguments":[
                 {"argumentCode":"A","value":{{argument}},"valueString":null,"unitId":null}]}
              """);
}
