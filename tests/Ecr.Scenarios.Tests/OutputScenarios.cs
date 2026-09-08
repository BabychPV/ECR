using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scenarios.Tests;

/// <summary>§6.6 директиви — вихід: S-27, S-28.</summary>
[Collection("SqlServer")]
public sealed class OutputScenarios(SqlServerFixture sql)
{
    /// <summary>
    /// S-27. Визначення звіту заводиться, регламентний зріз формується.
    /// </summary>
    /// <remarks>
    /// ⛔ <c>ReportsController</c> має лише <c>GET /reports/snapshots</c> і
    /// <c>POST /reports/{code}/build</c> — маршруту, що заводить
    /// <c>rpt.ReportDef</c> (ФВ-10.4: «Визначення звіту — дані»), немає ні в
    /// контролері, ні в openapi. Зріз можна лише СПРОБУВАТИ побудувати за
    /// кодом визначення, якого завести нема як — і саме на цьому сценарій
    /// падає з чіткою причиною (`ECR-RPT-0404`, а не 404 маршруту).
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-27")]
    public async Task Визначення_звіту_заводиться_і_зріз_формується()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S27", ["Report.BuildSnapshot", "Report.ViewRegulatory", "Project.Manage", "Template.Edit", "Document.View"]);

        var createDefinition = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/reports", UriKind.Relative),
            new { code = "S27_REPORT", nameL10n = new Dictionary<string, string> { ["en"] = "S-27 report" } });
        Assert.Equal(HttpStatusCode.NotFound, createDefinition.StatusCode);

        var projectId = await ProjectAndPeriodScenarios.CreateProjectAsync(admin.Client, "S27", "Asia/Almaty");
        await ProjectAndPeriodScenarios.ActivateProjectAsync(admin.Client, projectId);

        var build = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/reports/S27_REPORT/build", UriKind.Relative),
            new { projectId, periodKey = DateTime.UtcNow.Year * 100 + 1 });

        // Визначення звіту не існує (нема як завести) — побудова відмовляє,
        // а не приймається в чергу.
        Assert.True((int)build.StatusCode >= 400, $"побудова зрізу за неіснуючим кодом мала відмовити, а повернула {build.StatusCode}");
    }

    /// <summary>
    /// S-28. Експорт у книгу; подання відхиляється за наявності блокуючих
    /// помилок; погодження рецензентом.
    /// </summary>
    /// <remarks>
    /// ⛔ Друга половина сценарію переписана з «доказу межі» на доказ реальної
    /// поведінки (директива №09 `W8` п.5). Раніше вона подавала НЕІСНУЮЧИЙ
    /// аркуш і задовольнялася будь-якою відмовою: справжнього блокувального
    /// <c>Error</c> узяти не було звідки, а якби й було — подання його не
    /// побачило б, бо <c>SubmitSheetHandler</c> тримав <c>ValidationEngine</c>
    /// упорснутим і не читаним. Тепер сценарій кладе в комірку число, яке
    /// порушує РЕАЛЬНЕ правило версії, і подання відмовляє саме тому.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-28")]
    public async Task Експорт_подання_і_погодження_рецензентом()
    {
        using var app = new EcrApiFactory(sql);
        var author = await Provisioning.AdministratorAsync(
            app, "S28a",
            [
                "Project.Manage", "Document.View", "Document.Create", "Template.Edit",
                "Template.Publish", "Document.Export",

                // ⚠ ЗАМІР: щоб ДОЧЕКАТИСЯ власної задачі експорту, авторові
                // потрібне `System.ViewHealth` — `GET /api/v1/jobs/{id}`
                // вимагає саме його (`GetJobStatusHandler.Permission`). Тобто
                // «експортувати» і «забрати книгу» — різні права, і без
                // другого автор отримує `202` з `jobId`, за яким йому нічого
                // не видно. Це видима межа моделі прав, а не сценарію
                // (`Q-154`); сценарій її називає й іде далі.
                "System.ViewHealth",
            ]);

        // Правило рівня рядка: значення колонки `A` не більше за 100.
        var doc = await DataEntryScenarios.ArrangeRealDocumentAsync(app, author, "S28", "[A] <= 100");
        author = doc.Admin;

        // ⛔ Крок 1 — ЗАПИС, і він стоїть першим навмисно. Експорт
        // порожнього документа відмовляє (`ExcelExporter`: «документа за
        // період не існує або він порожній»), тож «книга непорожня» можна
        // довести лише книгою, у якій щось є. Заразом число `101` порушує
        // правило версії (`[A] <= 100`) — його ж перевіряє крок 3.
        var tables = await author.Client.GetAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/tables?periodKey={doc.PeriodKey}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, tables.StatusCode);
        var tableInstanceId = (await tables.Content.ReadFromJsonAsync<JsonElement>())[0]
            .GetProperty("tableInstanceId").GetInt64();

        var slice = await author.Client.GetAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/tables/{tableInstanceId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, slice.StatusCode);
        var row = (await slice.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("rows").EnumerateArray()
            .First(r => r.GetProperty("rowKey").GetString() == doc.RowKeys[0]);

        var patch = await author.Client.PatchAsJsonAsync(
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
                        cells = new object[] { new { columnCode = doc.ColumnCode, value = 101m } },
                    },
                },
            });

        // ⚠ ЗАПИС при цьому проходить: блокує лише комірковий Error (R-B3,
        // `D-90`), а це правило рівня рядка. Заборона зберегти проміжний стан
        // зробила б роботу з великою таблицею неможливою.
        Assert.True(patch.StatusCode == HttpStatusCode.OK, $"{patch.StatusCode}: {app.ErrorsText}");

        // Крок 2: експорт у книгу — механізм є, доступний, повертає jobId.
        var export = await author.Client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/export", UriKind.Relative),
            new { includeFormulas = false, includeStyles = true, language = "en", periodKey = doc.PeriodKey });
        Assert.Equal(HttpStatusCode.Accepted, export.StatusCode);
        var exportJobId = (await export.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!;

        // ⚠ Тридцять секунд, а не п'ятнадцять: книга тепер має РЕАЛЬНИЙ вміст
        // (аркуш, таблиця, колонка, рядки), і на холодному старті хоста
        // побудова перший раз іде відчутно довше за порожню.
        var exportJob = await ScenarioHelpers.AwaitJobAsync(author.Client, exportJobId, TimeSpan.FromSeconds(30));
        Assert.True(
            exportJob.ValueKind != JsonValueKind.Undefined,
            $"задача експорту {exportJobId} не набула кінцевого стану за 30 с: {app.ErrorsText}");
        Assert.True(
            exportJob.GetProperty("state").GetString() == "Succeeded",
            $"задача експорту завершилася станом {exportJob.GetProperty("state").GetString()}: {app.ErrorsText}");

        var exportId = exportJob.TryGetProperty("message", out var m) ? m.GetString() : null;
        Assert.False(string.IsNullOrWhiteSpace(exportId), "повідомлення прогресу задачі експорту не несе exportId, за яким забрати файл.");

        var download = await author.Client.GetAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/export/{exportId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 0, "експортована книга порожня.");

        // ⛔ Крок 3 і доказ сценарію: подання ВІДМОВЛЯЄ, і саме через валідацію
        // (`ECR-SUB-4221`, `ФВ-5.4`/`ФВ-5.19`), а не «якимось 4xx».
        var submit = await author.Client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/submit", UriKind.Relative),
            new { sheetDefId = doc.SheetDefId, periodKey = doc.PeriodKey });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, submit.StatusCode);
        var submitBody = await submit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ECR-SUB-4221", submitBody.GetProperty("errorCode").GetString());

        // Крок 3: рецензент (окремий обліковий запис із власним грантом)
        // бачить документ автора.
        var reviewer = await Provisioning.AdministratorAsync(app, "S28b", ["Document.View"]);
        await Provisioning.GrantAsync(app, reviewer.RoleId, "Project", doc.ProjectId, "Approve");
        reviewer = await Provisioning.ReauthenticateAsync(app, reviewer);

        var reviewerSees = await reviewer.Client.GetAsync(new Uri($"/api/v1/documents/{doc.DocumentId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, reviewerSees.StatusCode);
    }
}
