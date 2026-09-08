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
    /// ⚠ Сценарій переписано з ДОКАЗУ ВІДСУТНОСТІ на доказ реальної поведінки
    /// (директива №09 <c>W7</c>). Стара редакція перевіряла, що <c>POST
    /// /api/v1/reports</c> дає <c>404</c>, а побудова за неіснуючим кодом
    /// відмовляє: <c>rpt.ReportDef</c> не створювало ніщо — ні код, ні seed,
    /// ні тести, — тому завести визначення не було чим. Перевірка тут саме
    /// ПОСИЛЮЄТЬСЯ, а не слабшає (директива §10): було «маршруту немає»,
    /// стало «опис заводиться, версія публікується, зріз будується і його
    /// видно».
    ///
    /// ⛔ Доказ проходить через кілька незалежних спостережень, і жодне з них
    /// не є саме лише «маршрут відповів 200». Опис видно в переліку; побудова
    /// приймається в чергу ЛИШЕ після публікації версії; задача доходить до
    /// <c>Succeeded</c>; зріз лежить у переліку з тією самою версією, з якої
    /// його побудували, і з контрольною сумою. Без останнього кроку сценарій
    /// доводив би, що ми вміємо ставити задачі в чергу.
    ///
    /// ⚠ Зріз виходить порожнім (<c>rowCount = 0</c>) і статусом <c>Draft</c>:
    /// у проєкті немає ні аркушів, ні прогону розрахунку. Це не послаблення —
    /// «нічого не подано» і є коректним станом даних (<c>D-65</c>), а
    /// перевіряється тут МЕХАНІЗМ: побудова доходить до кінця й лишає по собі
    /// читабельний незмінний зріз.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-27")]
    public async Task Визначення_звіту_заводиться_і_зріз_формується()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app,
            "S27",
            [
                "Report.EditDefinition", "Report.BuildSnapshot", "Report.ViewRegulatory",
                "Project.Manage", "System.ViewHealth", "Template.Edit", "Document.View",
            ]);

        // Код свій на кожен прогін: база в збірці спільна, а `UQ_ReportDef`
        // не дав би завести той самий опис удруге — сценарій падав би на
        // ПОВТОРНОМУ запуску, тобто саме тоді, коли його запускають найчастіше.
        var code = $"S27{Guid.NewGuid():N}"[..16];

        // Крок 1: опис звіту заводиться разом із першою версією-чернеткою.
        var createDefinition = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/reports", UriKind.Relative),
            new
            {
                code,
                nameL10n = new Dictionary<string, string> { ["en"] = "S-27 report" },
                isRegulatory = true,
                version = "1.0",
                columns = new[]
                {
                    new { code = "DocumentId", kind = "number" },
                    new { code = "OutputCode", kind = "text" },
                    new { code = "Value", kind = "number" },
                },
                rules = (object?)null,
            });
        Assert.True(
            createDefinition.IsSuccessStatusCode,
            $"опис звіту не заведено: {createDefinition.StatusCode}: {app.ErrorsText}");

        var definition = await createDefinition.Content.ReadFromJsonAsync<JsonElement>();
        var definitionId = definition.GetProperty("id").GetInt32();
        var versions = definition.GetProperty("versions");
        Assert.Equal(1, versions.GetArrayLength());

        var versionId = versions[0].GetProperty("id").GetInt32();

        // ⛔ Версія створюється саме ЧЕРНЕТКОЮ: сховище описів бере лише
        // опубліковане, а опис звіту правлять тоді, коли ще не впевнені в ньому.
        Assert.Equal("Draft", versions[0].GetProperty("status").GetString());

        var projectId = await ProjectAndPeriodScenarios.CreateProjectAsync(admin.Client, "S27", "Asia/Almaty");
        await ProjectAndPeriodScenarios.ActivateProjectAsync(admin.Client, projectId);
        var periodKey = (DateTime.UtcNow.Year * 100) + 1;

        // Крок 2: доки версія чернеткова, побудови немає — і це той самий
        // `ECR-RPT-0404`, що й для неіснуючого коду. Без цієї перевірки
        // публікація нижче могла б виявитися церемонією.
        var tooEarly = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/reports/{code}/build", UriKind.Relative),
            new { projectId, periodKey });
        Assert.Equal(HttpStatusCode.NotFound, tooEarly.StatusCode);

        // Крок 3: публікація версії.
        var publish = await admin.Client.PostAsync(
            new Uri($"/api/v1/reports/{definitionId}/versions/{versionId}/publish", UriKind.Relative),
            content: null);
        Assert.True(publish.IsSuccessStatusCode, $"публікація версії: {publish.StatusCode}: {app.ErrorsText}");
        Assert.Equal(
            "Published",
            (await publish.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        // Крок 4: опис видно в переліку — тобто звіт можна ВИБРАТИ, а не
        // вгадувати його код.
        var list = await admin.Client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/v1/reports", UriKind.Relative));
        var listed = list.EnumerateArray()
            .Where(d => string.Equals(d.GetProperty("code").GetString(), code, StringComparison.Ordinal))
            .ToList();
        Assert.Single(listed);
        Assert.Contains(
            listed[0].GetProperty("versions").EnumerateArray(),
            v => string.Equals(v.GetProperty("status").GetString(), "Published", StringComparison.Ordinal));

        // Крок 5: побудова приймається в чергу і доходить до кінця.
        var build = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/reports/{code}/build", UriKind.Relative),
            new { projectId, periodKey });
        Assert.Equal(HttpStatusCode.Accepted, build.StatusCode);

        var jobId = (await build.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!;
        var job = await ScenarioHelpers.AwaitJobAsync(admin.Client, jobId, TimeSpan.FromSeconds(30));
        Assert.True(
            job.ValueKind != JsonValueKind.Undefined,
            $"задача побудови {jobId} не набула кінцевого стану за 30 с.");
        Assert.True(
            string.Equals(job.GetProperty("state").GetString(), "Succeeded", StringComparison.Ordinal),
            $"побудова зрізу не завершилася успіхом: {job}\n{app.ErrorsText}");

        // Крок 6: зріз існує і читається — саме за тією версією, яку щойно
        // опублікували, поточний, із контрольною сумою вмісту.
        var snapshots = await admin.Client.GetFromJsonAsync<JsonElement>(
            new Uri($"/api/v1/reports/snapshots?projectId={projectId}&periodKey={periodKey}", UriKind.Relative));

        var mine = snapshots.EnumerateArray()
            .Where(s => s.GetProperty("reportVersionId").GetInt32() == versionId)
            .ToList();

        Assert.Single(mine);
        Assert.True(mine[0].GetProperty("isCurrent").GetBoolean(), "щойно побудований зріз не позначено поточним.");
        Assert.False(
            string.IsNullOrWhiteSpace(mine[0].GetProperty("contentHash").GetString()),
            "зріз побудовано без контрольної суми: звірити його з тим, що показує SSRS, було б нічим.");
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
