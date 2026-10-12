// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.TableRenames.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Мапа «було → стало» кодів таблиць/колонок у <c>migrate-version</c> (<c>tableRenames</c>): дані таблиці зі СТАРИМ
/// кодом переїжджають у таблицю з НОВИМ кодом цільової версії (як <c>T9a_03 → T9a_17a</c>) без міграції схеми.
/// </summary>
/// <remarks>
/// ⚠ Справжнім HTTP на справжньому SQL Server. Мутаційні докази: прибрати застосування мапи в
/// <c>VersionMigrationPlanner</c> — червоніють «з мапою», «async» і «колонка»; не передавати
/// <c>TableRenames</c> із запиту в <c>MigrateDocumentVersionTask</c>/<c>RunQueuedAsync</c> — червоніє «async».
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Без_мапи_перейменована_таблиця_блокує_перенос_як_втрата_даних()
    {
        var s = await ArrangeAsync(Target.TableRenamed).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostWithRenamesAsync(client, s, dryRun: false, renames: null).ConfigureAwait(true);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateDataLoss").ConfigureAwait(true);
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task З_мапою_сухий_прогін_каже_що_буде_перейменовано_і_нічого_не_змінює()
    {
        var s = await ArrangeAsync(Target.TableRenamed).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostWithRenamesAsync(client, s, dryRun: true, Renames(s)).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {app.ErrorsText}");

        var report = JsonDocument.Parse(body).RootElement;
        Assert.True(report.GetProperty("canApply").GetBoolean(), body);
        Assert.Equal(0, report.GetProperty("lostValues").GetInt64());
        Assert.Equal(2, report.GetProperty("transferredValues").GetInt64());

        var renamed = report.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("kind").GetString() == "Renamed");
        Assert.EndsWith("." + s.SourceTableCode, renamed.GetProperty("path").GetString(), StringComparison.Ordinal);
        Assert.Equal(s.SourceTableCode, renamed.GetProperty("oldValue").GetString());
        Assert.Equal(s.TargetTableCode, renamed.GetProperty("newValue").GetString());

        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task З_мапою_значення_переїжджають_у_таблицю_з_новим_кодом_і_версія_перемикається()
    {
        var s = await ArrangeAsync(Target.TableRenamed).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostWithRenamesAsync(client, s, dryRun: false, Renames(s)).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {app.ErrorsText}");
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("applied").GetBoolean());

        await AssertMovedAsync(s, expectedColumnForC2: s.TargetColumns["C2"]).ConfigureAwait(true);

        // Журнал безпеки: скільки перейменувань застосовано (без значень і без документів).
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var events = await db.Database
            .SqlQuery<string>($"SELECT ISNULL(DetailsJson, N'') AS Value FROM aud.SecurityEvent WHERE EventType = N'DocumentVersionMigrated' AND ChangedByUserId = {s.UserId}")
            .ToListAsync().ConfigureAwait(true);
        Assert.Equal(1, JsonDocument.Parse(Assert.Single(events)).RootElement.GetProperty("renames").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task З_мапою_колонок_значення_переїжджають_у_колонку_з_новим_кодом()
    {
        var s = await ArrangeAsync(Target.TableAndColumnRenamed).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var renames = new[]
        {
            new
            {
                fromTableCode = s.SourceTableCode,
                toTableCode = s.TargetTableCode,
                sheetCode = (string?)null,
                columns = new[] { new { fromColumnCode = $"C2_{s.Tag}", toColumnCode = $"C2X_{s.Tag}" } },
            },
        };

        var response = await PostWithRenamesAsync(client, s, dryRun: false, renames).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {app.ErrorsText}");

        await AssertMovedAsync(s, expectedColumnForC2: s.TargetColumns["C2"]).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Async_з_мапою_фонова_задача_теж_переносить_значення_у_таблицю_з_новим_кодом()
    {
        var s = await ArrangeAsync(Target.TableRenamed).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId.ToString(CultureInfo.InvariantCulture)}/migrate-version", UriKind.Relative),
            new { targetVersionId = s.TargetVersionId, mode = "Safe", dryRun = false, async = true, tableRenames = Renames(s) })
            .ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{response.StatusCode}: {body} {app.ErrorsText}");

        var job = await WaitJobAsync(client, JsonDocument.Parse(body).RootElement.GetProperty("jobId").GetString()!, app)
            .ConfigureAwait(true);
        Assert.True(job.GetProperty("state").GetString() == "Succeeded", $"задача: {job.GetRawText()}\n{app.ErrorsText}");

        await AssertMovedAsync(s, expectedColumnForC2: s.TargetColumns["C2"]).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Мапа_що_веде_в_колонку_з_відповідником_за_кодом_422_renameConflict_і_нічого_не_змінюється()
    {
        // DropsC3AddsC4AndRow: C3 у цілі немає. Мапа C3 → C1 веде в колонку, що в джерелі вже має відповідника C1.
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var renames = new[]
        {
            new
            {
                fromTableCode = s.SourceTableCode,
                toTableCode = s.SourceTableCode,
                sheetCode = (string?)null,
                columns = new[] { new { fromColumnCode = $"C3_{s.Tag}", toColumnCode = $"C1_{s.Tag}" } },
            },
        };

        var dry = await PostWithRenamesAsync(client, s, dryRun: true, renames).ConfigureAwait(true);
        var report = JsonDocument.Parse(await dry.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.False(report.GetProperty("canApply").GetBoolean());
        Assert.Contains("renameConflict", report.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));

        var response = await PostWithRenamesAsync(client, s, dryRun: false, renames).ConfigureAwait(true);

        var problem = await AssertProblemAsync(
            response, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateRenameConflict").ConfigureAwait(true);
        Assert.Contains($"C3_{s.Tag}", problem, StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Зламана_мапа_422_migrateRenameMapInvalid_і_нічого_не_змінюється()
    {
        var s = await ArrangeAsync(Target.TableRenamed).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostWithRenamesAsync(
            client, s, dryRun: true,
            new[] { new { fromTableCode = "A", toTableCode = "A", sheetCode = (string?)null, columns = (object?)null } })
            .ConfigureAwait(true);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateRenameMapInvalid").ConfigureAwait(true);
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Мапа_не_обходить_права_без_гранта_Manage_403_і_нічого_не_змінюється()
    {
        var s = await ArrangeAsync(Target.TableRenamed, grant: GrantLevel.Write).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostWithRenamesAsync(client, s, dryRun: false, Renames(s)).ConfigureAwait(true);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "ECR-AUTH-0403", "err.ECR-AUTH-0403.noProjectManageGrant").ConfigureAwait(true);
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    private static object[] Renames(Scenario s)
        => [new { fromTableCode = s.SourceTableCode, toTableCode = s.TargetTableCode }];

    private static Task<HttpResponseMessage> PostWithRenamesAsync(HttpClient client, Scenario s, bool dryRun, object? renames)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId.ToString(CultureInfo.InvariantCulture)}/migrate-version", UriKind.Relative),
            new { targetVersionId = s.TargetVersionId, mode = "Safe", dryRun, tableRenames = renames });

    /// <summary>Значення першого рядка переїхали на колонки й таблицю цільової версії; версія проєкту — нова.</summary>
    private async Task AssertMovedAsync(Scenario s, int expectedColumnForC2)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        var project = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == s.Doc.ProjectId).ConfigureAwait(false);
        Assert.Equal(s.TargetVersionId, project.TemplateVersionId);

        var cells = await db.CellValues.AsNoTracking()
            .Where(c => c.PeriodKeyValue == s.Doc.PeriodKey.Value && s.Doc.RowIds.Contains(c.TableRowId))
            .ToListAsync().ConfigureAwait(false);

        // Обидва значення (рядок «a» і число 42) збережено; порожня комірка C3 — теж на колонці цілі.
        Assert.Contains(cells, c => c.ValueString == "a" && c.ColumnDefId == s.TargetColumns["C1"]);
        Assert.Contains(cells, c => c.ValueNumeric == 42m && c.ColumnDefId == expectedColumnForC2);
        Assert.All(cells, c => Assert.Equal(s.TargetTableDefId, c.TableDefId));

        var instance = await db.TableInstances.AsNoTracking()
            .SingleAsync(t => t.PeriodKeyValue == s.Doc.PeriodKey.Value && t.Id == s.Doc.TableInstanceId).ConfigureAwait(false);
        Assert.Equal(s.TargetTableDefId, instance.TableDefId);
    }
}
