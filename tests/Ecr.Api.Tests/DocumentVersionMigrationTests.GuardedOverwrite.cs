// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.GuardedOverwrite.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Documents.VersionMigration;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ACC-OW: прапор <c>acceptGuardedOverwrite</c> у <c>POST migrate-version</c> знімає <c>guardedWithData</c> ЛИШЕ для
/// нової комірочної формули на комірці з введеним значенням (WW-02 / похідні підсумки зі старої системи). Значення
/// переїздить, перерахунок після переносу (LP-41) замінює його результатом формули.
/// </summary>
/// <remarks>
/// ⚠ Справжнім HTTP на справжньому SQL Server. Мутаційний доказ: у <c>VersionMigrationPlan.RefusalsFor</c> зробити
/// відмову безумовною (прибрати віднімання <c>GuardedOverwritableValues</c>) — червоніють «з прапором переносить» і
/// «dry-run з прапором»; зробити віднімання ВСІХ <c>GuardedValues</c> при прапорі — червоніє «інший клас guarded».
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    private static Task<HttpResponseMessage> PostWithOverwriteAsync(
        HttpClient client, Scenario s, bool dryRun, bool acceptGuardedOverwrite, bool async = false)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId.ToString(CultureInfo.InvariantCulture)}/migrate-version", UriKind.Relative),
            new
            {
                targetVersionId = s.TargetVersionId,
                mode = "Safe",
                dryRun,
                async,
                acceptGuardedOverwrite,
            });

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ACC-OW")]
    public async Task Без_прапора_формула_комірки_на_значенні_лишається_guardedWithData_а_звіт_каже_що_перезапишеться()
    {
        var s = await ArrangeAsync(Target.AddsCellFormulaOnInput).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = await PostWithOverwriteAsync(client, s, dryRun: true, acceptGuardedOverwrite: false).ConfigureAwait(true);
        var dryBody = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {dryBody} {app.ErrorsText}");

        var report = JsonDocument.Parse(dryBody).RootElement;
        Assert.False(report.GetProperty("canApply").GetBoolean(), dryBody);
        Assert.Contains(
            report.GetProperty("refusals").EnumerateArray(), r => r.GetString() == "guardedWithData");
        Assert.Equal(1, report.GetProperty("guardedValues").GetInt64());
        Assert.Equal(1, report.GetProperty("guardedOverwritable").GetInt64());
        Assert.False(report.GetProperty("guardedOverwriteAccepted").GetBoolean());

        var item = Assert.Single(report.GetProperty("guardedOverwrittenItems").EnumerateArray());
        Assert.Equal(s.Doc.SheetCode, item.GetProperty("sheetCode").GetString());
        Assert.Equal(s.SourceTableCode, item.GetProperty("tableCode").GetString());
        Assert.Equal($"R1_{s.Tag}", item.GetProperty("rowKey").GetString());
        Assert.Equal($"C2_{s.Tag}", item.GetProperty("columnCode").GetString());
        Assert.Equal(1, item.GetProperty("values").GetInt64());

        // ⛔ Без значень: лише адреса комірки шаблону й кількість.
        Assert.Equal(
            ["columnCode", "rowKey", "sheetCode", "tableCode", "values"],
            item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList());

        // Застосування без прапора — як і раніше 422.
        var response = await PostWithOverwriteAsync(client, s, dryRun: false, acceptGuardedOverwrite: false).ConfigureAwait(true);
        var body = await AssertProblemAsync(
            response, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateDataLoss").ConfigureAwait(true);
        Assert.Contains("guardedWithData", body, StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ACC-OW")]
    public async Task Dry_run_з_прапором_показує_ті_самі_лічильники_дозволяє_і_нічого_не_змінює()
    {
        var s = await ArrangeAsync(Target.AddsCellFormulaOnInput).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = await PostWithOverwriteAsync(client, s, dryRun: true, acceptGuardedOverwrite: true).ConfigureAwait(true);
        var body = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {body} {app.ErrorsText}");

        var report = JsonDocument.Parse(body).RootElement;
        Assert.True(report.GetProperty("canApply").GetBoolean(), body);
        Assert.Empty(report.GetProperty("refusals").EnumerateArray());
        Assert.True(report.GetProperty("guardedOverwriteAccepted").GetBoolean());
        Assert.Equal(1, report.GetProperty("guardedOverwritable").GetInt64());
        Assert.Single(report.GetProperty("guardedOverwrittenItems").EnumerateArray());
        Assert.False(report.GetProperty("applied").GetBoolean());

        await Task.Delay(1500).ConfigureAwait(true);
        Assert.Equal(0, await ProjectRecalcJobsAsync(s, systemOnly: false).ConfigureAwait(true));
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ACC-OW")]
    public async Task З_прапором_значення_переносяться_перерахунок_ставиться_а_результат_формули_замінює_введене()
    {
        var s = await ArrangeAsync(Target.AddsCellFormulaOnInput).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostWithOverwriteAsync(client, s, dryRun: false, acceptGuardedOverwrite: true).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {app.ErrorsText}");

        var report = JsonDocument.Parse(body).RootElement;
        Assert.True(report.GetProperty("applied").GetBoolean());
        Assert.Equal(1, report.GetProperty("guardedOverwritable").GetInt64());
        Assert.True(report.GetProperty("guardedOverwriteAccepted").GetBoolean());

        // Аудит: скільки значень перезаписано прапором і скільки таблиць дали перерахунок.
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var details = await db.Database
                .SqlQuery<string>($"SELECT ISNULL(DetailsJson, N'') AS Value FROM aud.SecurityEvent WHERE EventType = N'DocumentVersionMigrated' AND ChangedByUserId = {s.UserId}")
                .SingleAsync().ConfigureAwait(true);
            var audit = JsonDocument.Parse(details).RootElement;
            Assert.Equal(1, audit.GetProperty("guardedOverwritten").GetInt64());
            Assert.Equal(1, audit.GetProperty("guardedOverwrittenCells").GetInt32());
            Assert.Equal(1, audit.GetProperty("formulaTables").GetInt32());
        }

        // Значення комірки переїхало на цільову колонку (до перерахунку — старе, після — результат формули).
        var c2 = s.TargetColumns["C2"];
        await using var read = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var deadline = DateTime.UtcNow.AddSeconds(90);
        decimal? value = null;
        while (DateTime.UtcNow < deadline && value != 100m)
        {
            value = await read.CellValues.AsNoTracking()
                .Where(c => c.PeriodKeyValue == s.Doc.PeriodKey.Value && c.ColumnDefId == c2 && s.Doc.RowIds.Contains(c.TableRowId))
                .OrderBy(c => c.TableRowId)
                .Select(c => c.ValueNumeric)
                .FirstOrDefaultAsync().ConfigureAwait(true);
            if (value != 100m)
            {
                await Task.Delay(250).ConfigureAwait(true);
            }
        }

        Assert.True(value == 100m, $"значення комірки після перерахунку: {value}; {app.ErrorsText}");
        Assert.Equal(1, await ProjectRecalcJobsAsync(s, systemOnly: true).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ACC-OW")]
    public async Task Async_з_прапором_проходить_фоновою_задачею()
    {
        var s = await ArrangeAsync(Target.AddsCellFormulaOnInput).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostWithOverwriteAsync(client, s, dryRun: false, acceptGuardedOverwrite: true, async: true)
            .ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{response.StatusCode}: {body} {app.ErrorsText}");

        var jobId = JsonDocument.Parse(body).RootElement.GetProperty("jobId").GetString()!;
        var job = await WaitJobAsync(client, jobId, app).ConfigureAwait(true);
        Assert.True(job.GetProperty("state").GetString() == "Succeeded", $"задача: {job.GetRawText()}\n{app.ErrorsText}");

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var project = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == s.Doc.ProjectId).ConfigureAwait(true);
        Assert.Equal(s.TargetVersionId, project.TemplateVersionId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ACC-OW")]
    public async Task З_прапором_інший_клас_guarded_лишається_guardedWithData_і_нічого_не_змінюється()
    {
        var s = await ArrangeAsync(Target.AddsCellFormulaAndNarrowsScale).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = await PostWithOverwriteAsync(client, s, dryRun: true, acceptGuardedOverwrite: true).ConfigureAwait(true);
        var dryBody = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {dryBody} {app.ErrorsText}");
        var report = JsonDocument.Parse(dryBody).RootElement;
        Assert.False(report.GetProperty("canApply").GetBoolean(), dryBody);
        Assert.Contains(report.GetProperty("refusals").EnumerateArray(), r => r.GetString() == "guardedWithData");

        // Прапор покриває лише формулу комірки (1 значення), звуження Scale (ще 1) — ні.
        Assert.Equal(1, report.GetProperty("guardedOverwritable").GetInt64());
        Assert.Equal(2, report.GetProperty("guardedValues").GetInt64());

        var response = await PostWithOverwriteAsync(client, s, dryRun: false, acceptGuardedOverwrite: true).ConfigureAwait(true);
        var body = await AssertProblemAsync(
            response, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateDataLoss").ConfigureAwait(true);
        Assert.Contains("guardedWithData", body, StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ACC-OW")]
    public async Task Поданий_аркуш_з_такою_міграцією_і_прапором_дає_409_sheetsLocked()
    {
        var s = await ArrangeAsync(Target.AddsCellFormulaOnInput, submitted: true).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostWithOverwriteAsync(client, s, dryRun: false, acceptGuardedOverwrite: true).ConfigureAwait(true);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "ECR-DOC-0409", "err.ECR-DOC-0409.migrateSheetsLocked")
            .ConfigureAwait(true);
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }
}
