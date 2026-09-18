using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Scenarios.Tests;

/// <summary>
/// <c>DAT-02</c> директиви №14 частина 3 — наскрізно: перерахунок не пише
/// незміненого і не забирає чужих версій рядків.
/// </summary>
/// <remarks>
/// ⛔ <b>Це не сценарій про швидкість.</b> Колонкова формула рахується в
/// КОЖНОМУ рядку таблиці, а <c>RecalculationService</c> писав результат
/// безумовно — з аудитом і «дотиком» рядка. Наслідок бачив не адміністратор у
/// метриках, а оператор: він заповнив рядок, фонова задача підняла
/// <c>RowVersion</c> УСІХ рядків таблиці, і наступне автозбереження в тому
/// самому рядку діставало <c>ECR-CELL-0409</c> — «хтось змінив ці дані» — хоча
/// ніхто нічого не змінював. На таблиці в 500 рядків це 500 записів замість
/// одного і 500 чужих версій.
///
/// ⚠ Журнал читається ПРЯМИМ запитом до <c>aud.CellChange</c>: HTTP-контракт
/// не віддає числа записів аудиту за походженням, а саме воно тут і є
/// твердженням. Прецедент той самий, що в <c>StructureScenarios</c>
/// (<c>aud.PublicationEvent</c>) — читання журналу, а не обхід обробників.
/// </remarks>
[Collection("SqlServer")]
public sealed class RecalculationWriteScopeScenarios(SqlServerFixture sql)
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Requirement", "DAT-02")]
    public async Task Перерахунок_пише_лише_змінений_рядок_і_не_підміняє_версій_решти()
    {
        using var app = new EcrApiFactory(sql);

        var admin = await Provisioning.AdministratorAsync(
            app,
            "D14R",
            [
                "Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Template.Publish",
                "Calculation.View", "Calculation.EditFormula", "Calculation.Recalculate", "System.ViewHealth",
            ]);

        // 1. Таблиця з двома рядками і колонковою формулою `C = A + B`: та сама
        //    структура, що й у `S-21`, але ОБИДВА рядки заповнені. Це умова
        //    доказу, а не деталь: на порожньому другому рядку формула дала б
        //    `#REF`/`null` і не писалася б і до виправлення — тобто сценарій
        //    був би зеленим із хибної причини.
        var doc = await DataEntryScenarios.ArrangeRealDocumentAsync(
            app, admin, "D14R",
            extraNumericColumns: ["B"],
            formulaColumn: ("C", "[A] + [B]"));
        admin = doc.Admin;

        var tableInstanceId = await TableInstanceAsync(app, admin.Client, doc.DocumentId, doc.PeriodKey);
        var first = doc.RowKeys[0];
        var second = doc.RowKeys[1];

        await WriteAsync(app, admin.Client, doc, tableInstanceId, first, a: 4m, b: 2.5m);
        await WriteAsync(app, admin.Client, doc, tableInstanceId, second, a: 7m, b: 1m);

        Assert.Equal(6.5m, await AwaitCellAsync(app, admin.Client, doc.DocumentId, tableInstanceId, first, 6.5m));
        Assert.Equal(8m, await AwaitCellAsync(app, admin.Client, doc.DocumentId, tableInstanceId, second, 8m));

        // 2. Точка відліку: скільки записів `Recalculation` у журналі вже є і
        //    яку версію має другий рядок ЗАРАЗ.
        var auditBefore = await RecalculationChangesAsync(doc.DocumentId);
        var secondVersionBefore = await RowVersionAsync(admin.Client, doc.DocumentId, tableInstanceId, second);

        // 3. Правка ОДНОГО входу в ПЕРШОМУ рядку. Відповідь несе нову версію
        //    цього рядка — рівно ту, яку триматиме відкрита сітка оператора.
        var versionAfterPatch = await WriteAsync(
            app, admin.Client, doc, tableInstanceId, first, a: 10m, b: 2.5m);

        Assert.Equal(12.5m, await AwaitCellAsync(app, admin.Client, doc.DocumentId, tableInstanceId, first, 12.5m));

        // 4. (а) У журналі — РІВНО один новий запис `Recalculation`. Другий
        //    рядок формула теж порахувала (8 = 7 + 1), але це те саме число,
        //    що там і лежить: запис «8 → 8» стверджував би зміну, якої не було.
        var auditAfter = await RecalculationChangesAsync(doc.DocumentId);
        Assert.Equal(auditBefore + 1, auditAfter);

        // 5. (б) Версія другого рядка не змінилася: перерахунок його не
        //    торкався (`D14-07`, `TouchedRowIds: []`).
        Assert.Equal(
            secondVersionBefore,
            await RowVersionAsync(admin.Client, doc.DocumentId, tableInstanceId, second));

        // 6. (в) ГОЛОВНЕ для користувача: наступне збереження в тому самому
        //    рядку з версією З ВІДПОВІДІ попереднього `PATCH` проходить.
        //    Саме тут і був хибний `409`: перерахунок устигав підняти
        //    `RowVersion` між двома автозбереженнями оператора.
        var next = await PatchAsync(admin.Client, doc, tableInstanceId, first, versionAfterPatch, a: 11m, b: 2.5m);

        Assert.True(
            next.StatusCode == HttpStatusCode.OK,
            $"друге збереження того самого рядка дало {next.StatusCode} замість 200: "
            + $"{await next.Content.ReadAsStringAsync()}; {app.ErrorsText}");
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

    /// <summary>Пише <c>A</c> і <c>B</c> в рядок; повертає його нову версію з ВІДПОВІДІ.</summary>
    /// <remarks>
    /// ⚠ Версія береться саме з відповіді <c>PATCH</c>, а не з наступного
    /// читання зрізу: клієнт робить так само (<c>useCellPatch.ts</c> кладе
    /// <c>response.rowVersions</c> у <c>versions.current</c>), і весь сенс
    /// перевірки (в) — у тому, що ця версія лишається чинною.
    /// </remarks>
    private static async Task<string> WriteAsync(
        EcrApiFactory app, HttpClient client, DataEntryScenarios.RealDocument doc,
        long tableInstanceId, string rowKey, decimal a, decimal b)
    {
        var baseVersion = await RowVersionAsync(client, doc.DocumentId, tableInstanceId, rowKey);
        var patch = await PatchAsync(client, doc, tableInstanceId, rowKey, baseVersion, a, b);

        Assert.True(
            patch.StatusCode == HttpStatusCode.OK,
            $"запис A/B у {rowKey}: {patch.StatusCode}: {await patch.Content.ReadAsStringAsync()}; {app.ErrorsText}");

        var body = await patch.Content.ReadFromJsonAsync<JsonElement>();
        var versions = body.GetProperty("rowVersions");

        Assert.True(
            versions.TryGetProperty(rowKey, out var updated),
            $"відповідь PATCH не повернула нової версії рядка {rowKey}: {body.GetRawText()}");

        return updated.GetString()!;
    }

    /// <summary>Один <c>PATCH</c> двох комірок рядка під заявленою версією.</summary>
    private static Task<HttpResponseMessage> PatchAsync(
        HttpClient client, DataEntryScenarios.RealDocument doc, long tableInstanceId,
        string rowKey, string baseVersion, decimal a, decimal b)
        => client.PatchAsJsonAsync(
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
                        rowKey,
                        baseVersion,
                        cells = new object[]
                        {
                            new { columnCode = "A", value = a },
                            new { columnCode = "B", value = b },
                        },
                    },
                },
            });

    /// <summary>Версія рядка зі зрізу — те саме число, що тримає сітка.</summary>
    private static async Task<string> RowVersionAsync(
        HttpClient client, long documentId, long tableInstanceId, string rowKey)
    {
        var row = await RowAsync(client, documentId, tableInstanceId, rowKey);

        return row.GetProperty("rowVersion").GetString()!;
    }

    private static async Task<JsonElement> RowAsync(
        HttpClient client, long documentId, long tableInstanceId, string rowKey)
    {
        var slice = await client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables/{tableInstanceId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, slice.StatusCode);

        var body = await slice.Content.ReadFromJsonAsync<JsonElement>();
        var row = body.GetProperty("rows").EnumerateArray().FirstOrDefault(
            r => string.Equals(r.GetProperty("rowKey").GetString(), rowKey, StringComparison.Ordinal));

        Assert.True(row.ValueKind == JsonValueKind.Object, $"рядка {rowKey} у зрізі немає: {body.GetRawText()}");

        return row;
    }

    /// <summary>
    /// Опитує зріз, поки в колонці <c>C</c> рядка не з'явиться очікуване
    /// число; <c>null</c> — не з'явилось за 30 с.
    /// </summary>
    /// <remarks>
    /// ⚠ Перерахунок асинхронний (<c>FormulaRecalculationJob</c>): читання
    /// одразу після <c>PATCH</c> перевіряло б чергу, а не результат.
    /// </remarks>
    private static async Task<decimal?> AwaitCellAsync(
        EcrApiFactory app, HttpClient client, long documentId, long tableInstanceId,
        string rowKey, decimal expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        decimal? last = null;

        while (DateTime.UtcNow < deadline)
        {
            var row = await RowAsync(client, documentId, tableInstanceId, rowKey);
            if (row.GetProperty("cells").TryGetProperty("C", out var cell)
                && cell.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                last = cell.ValueKind == JsonValueKind.Number
                    ? cell.GetDecimal()
                    : decimal.Parse(cell.GetString()!, CultureInfo.InvariantCulture);

                if (last == expected)
                {
                    return last;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        Assert.Fail(
            $"комірка C рядка {rowKey} за 30 с не стала {expected} (останнє: "
            + $"{last?.ToString(CultureInfo.InvariantCulture) ?? "порожньо"}); {app.ErrorsText}");

        return last;
    }

    /// <summary>Скільки записів <c>Origin = 'Recalculation'</c> у журналі документа.</summary>
    private async Task<int> RecalculationChangesAsync(long documentId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = @d AND Origin = 'Recalculation';";
        command.Parameters.AddWithValue("@d", documentId);

        return (int)(await command.ExecuteScalarAsync())!;
    }
}
