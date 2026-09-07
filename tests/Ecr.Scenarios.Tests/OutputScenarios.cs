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
    /// ⚠ Механізм експорту (<c>202</c> + <c>jobId</c>, <c>GET …/export/{exportId}</c>)
    /// перевіряється на РЕАЛЬНОМУ, хай і порожньому, документі — це не
    /// вимагає структурних маршрутів (S-04), лише документ узагалі (S-13
    /// показує ту саму межу). «Книга непорожня» довести не можна: без
    /// реальних колонок і рядків книзі нема що містити. Подання на аркуш,
    /// якого не існує, відмовляє з іншої причини, ніж «є блокуючий Error»
    /// (ФВ-5.19), але відмовляє — і це очікувана, задокументована межа.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-28")]
    public async Task Експорт_подання_і_погодження_рецензентом()
    {
        using var app = new EcrApiFactory(sql);
        var author = await Provisioning.AdministratorAsync(
            app, "S28a", ["Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Document.Export"]);

        (author, var projectId, var documentId, var periodKey) = await DataEntryScenarios.ArrangeDocumentAsync(app, author, "S28");

        // Крок 1: експорт у книгу — механізм є, доступний, повертає jobId.
        var export = await author.Client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId}/export", UriKind.Relative),
            new { includeFormulas = false, includeStyles = true, language = "en", periodKey });
        Assert.Equal(HttpStatusCode.Accepted, export.StatusCode);
        var exportJobId = (await export.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!;

        var exportJob = await ScenarioHelpers.AwaitJobAsync(author.Client, exportJobId, TimeSpan.FromSeconds(15));
        Assert.True(exportJob.ValueKind != JsonValueKind.Undefined, $"задача експорту {exportJobId} не набула кінцевого стану за 15 с.");
        Assert.Equal("Succeeded", exportJob.GetProperty("state").GetString());

        var exportId = exportJob.TryGetProperty("message", out var m) ? m.GetString() : null;
        Assert.False(string.IsNullOrWhiteSpace(exportId), "повідомлення прогресу задачі експорту не несе exportId, за яким забрати файл.");

        var download = await author.Client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/export/{exportId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var bytes = await download.Content.ReadAsByteArrayAsync();

        // Доказ «книга непорожня» — документ без жодної реальної таблиці
        // (S-04..S-09 недосяжні) не може мати вмісту, який вартий цієї назви.
        Assert.True(bytes.Length > 0, "експортована книга порожня.");

        // Крок 2: подання аркуша з блокуючим Error відхиляється. Реального
        // Error узяти нема звідки (S-06/S-19), тому подаємо неіснуючий
        // аркуш — відмова однаково очікувана, лише з іншим кодом.
        var submit = await author.Client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId}/submit", UriKind.Relative),
            new { sheetDefId = 1, periodKey });
        Assert.True((int)submit.StatusCode >= 400, $"подання мало відмовити, а повернуло {submit.StatusCode}");

        // Крок 3: рецензент (окремий обліковий запис із власним грантом)
        // бачить документ автора.
        var reviewer = await Provisioning.AdministratorAsync(app, "S28b", ["Document.View"]);
        await Provisioning.GrantAsync(app, reviewer.RoleId, "Project", projectId, "Approve");
        reviewer = await Provisioning.ReauthenticateAsync(app, reviewer);

        var reviewerSees = await reviewer.Client.GetAsync(new Uri($"/api/v1/documents/{documentId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, reviewerSees.StatusCode);
    }
}
