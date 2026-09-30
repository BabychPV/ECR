using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scenarios.Tests;

/// <summary>
/// Експорт документа, який ще НІХТО не відкривав (UX-прохід 2026-09-24, живий стенд).
/// </summary>
/// <remarks>
/// ⛔ Екземпляри таблиць створювалися лише при першому відкритті документа
/// (<c>GetDocumentTablesHandler</c>, <c>A7-30</c>). Документ, створений і
/// одразу вивантажений, їх не мав, і задача експорту падала «документа не
/// існує або він порожній» — хоча документ є і шаблон дає йому таблицю. На
/// стенді це був документ 6: після першого ж відкриття той самий експорт
/// проходив.
///
/// ⚠ <see cref="DataEntryScenarios.ArrangeRealDocumentAsync"/> навмисно
/// документа не відкриває — саме тому він тут і взятий.
/// </remarks>
[Collection("SqlServer")]
public sealed class UnopenedDocumentExportScenarios(SqlServerFixture sql)
{
    private const string Prefix = "UNOP";

    private static readonly string[] Permissions =
    [
        "Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Template.Publish",
        "Document.Export", "Document.Import", "System.ViewHealth",
    ];

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Finding", "V-10")]
    public async Task Невідкритий_документ_вивантажується_в_xlsx_і_книга_повертається_без_змін()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(app, Prefix, Permissions);
        var doc = await DataEntryScenarios.ArrangeRealDocumentAsync(app, admin, Prefix);

        // ⛔ Одразу після створення, без GET …/tables. На старому коді падає
        // вже тут: задача експорту Failed.
        var book = await ImportRoundTripScenarios.ExportAsync(app, doc.Admin.Client, doc, includeFormulas: false);

        var preview = await ImportRoundTripScenarios.PreviewAsync(app, doc.Admin.Client, doc.DocumentId, book);
        Assert.True(
            preview.GetProperty("rejected").GetArrayLength() == 0 && preview.GetProperty("changes").GetArrayLength() == 0,
            $"книга щойно створеного документа не збіглася з ним самим: {preview.GetRawText()}");
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Finding", "V-10")]
    public async Task Невідкритий_документ_вивантажується_в_csv()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(app, Prefix + "C", Permissions);
        var doc = await DataEntryScenarios.ArrangeRealDocumentAsync(app, admin, Prefix + "C");

        var start = await doc.Admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/export", UriKind.Relative),
            new { includeFormulas = false, includeStyles = false, language = "en", periodKey = doc.PeriodKey, format = "csv" });
        Assert.True(start.StatusCode == HttpStatusCode.Accepted, $"експорт: {start.StatusCode}: {app.ErrorsText}");
        var jobId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!;

        var job = await ScenarioHelpers.AwaitJobAsync(doc.Admin.Client, jobId, TimeSpan.FromSeconds(60));
        Assert.True(
            string.Equals(job.GetProperty("state").GetString(), "Succeeded", StringComparison.Ordinal),
            $"задача CSV-експорту: {job.GetRawText()}; {app.ErrorsText}");
    }
}
