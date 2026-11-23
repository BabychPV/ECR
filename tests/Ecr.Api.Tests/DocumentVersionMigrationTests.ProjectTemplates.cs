// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.ProjectTemplates.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ✎ B74 P3 (D-576, рішення людини 13.11): «нова версія шаблону переносить разом усі документи ЦЬОГО шаблону в проєкті;
/// інші шаблони проєкту незалежні». Проєкт з основним шаблоном A (документ a1) і приєднаним шаблоном B (документ b1,
/// <c>Document.TemplateId = B</c>, рядки <c>doc.ProjectTemplate</c> і шкали для обох) — справжнім HTTP на SQL Server.
/// </summary>
/// <remarks>
/// Мутації (прогнано в робочому дереві, не комітяться; усі червоні): прелюдія без фільтра шаблону (<c>@docs</c> = увесь
/// проєкт) — <see cref="Перенос_документа_шаблону_B_переносить_лише_документи_B"/> (екземпляр a1 видалено); <c>FinishSql</c>
/// без <c>@isPrimary = 1</c> — той самий (версія проєкту = B v2); обсяг комірок без фільтра — той самий (звіт сухого прогону розходиться);
/// позначки ручних значень без фільтра документів — той самий (позначку a1 знято); блок із версією проєкту замість пари —
/// той самий (422 dataLoss); шкала без фільтра шаблону — обидва тести переносу (422 multiVersion); перелік документів без
/// фільтра — обидва (documentCount 2); ключ задачі без шаблону — фоновий тест (той самий jobId); «інші проєкти» без пари
/// або без фільтра документів — тест «інші проєкти».
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B74")]
    public async Task Перенос_документа_шаблону_B_переносить_лише_документи_B()
    {
        var t = await ArrangeTwoTemplatesAsync().ConfigureAwait(true);
        var aBefore = await SnapshotAsync(t.A).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, t.A.UserName).ConfigureAwait(true);

        // Сухий прогін: обсяг — лише документ шаблону B, вихідна версія — версія пари B.
        var dry = await PostMigrateAsync(client, t.B.DocumentId, t.BTargetVersionId, dryRun: true).ConfigureAwait(true);
        var dryBody = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {dryBody} {app.ErrorsText}");
        var report = JsonDocument.Parse(dryBody).RootElement;
        Assert.Equal(1, report.GetProperty("documentCount").GetInt32());
        Assert.Equal(t.B.TemplateVersionId, report.GetProperty("fromVersionId").GetInt32());
        Assert.Equal(2, report.GetProperty("transferredValues").GetInt64());
        Assert.Equal(0, report.GetProperty("lostValues").GetInt64());

        var response = await PostMigrateAsync(client, t.B.DocumentId, t.BTargetVersionId, dryRun: false).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {app.ErrorsText}");

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        // ⛔ Предмет тесту: документ основного шаблону, версія проєкту, пара й шкала A — без змін.
        Assert.Equal(aBefore, await SnapshotAsync(t.A).ConfigureAwait(true));
        Assert.Equal(t.A.Doc.TemplateVersionId, await PairVersionAsync(db, t.A.Doc.ProjectId, t.A.Doc.TemplateId).ConfigureAwait(true));
        Assert.Equal([$"{t.A.Doc.TemplateId}:202601:{t.A.Doc.TemplateVersionId}"],
            await TimelineAsync(db, t.A.Doc.ProjectId, t.A.Doc.TemplateId).ConfigureAwait(true));
        Assert.True(await db.CellFormulaOverrides.AsNoTracking()
            .AnyAsync(o => o.PeriodKeyValue == t.A.Doc.PeriodKey.Value && o.TableRowId == t.A.Doc.RowIds[0]
                           && o.ColumnDefId == t.A.Doc.ColumnDefIds[1]).ConfigureAwait(true),
            "Позначку ручного значення документа шаблону A знято переносом шаблону B.");

        // Пара B — на новій версії, шкала B — один рядок нової версії саме з TemplateId = B.
        Assert.Equal(t.BTargetVersionId, await PairVersionAsync(db, t.A.Doc.ProjectId, t.B.TemplateId).ConfigureAwait(true));
        Assert.Equal([$"{t.B.TemplateId}:202601:{t.BTargetVersionId}"],
            await TimelineAsync(db, t.A.Doc.ProjectId, t.B.TemplateId).ConfigureAwait(true));

        // Документ B — на структурі B v2.
        var cells = await db.CellValues.AsNoTracking()
            .Where(c => c.PeriodKeyValue == t.B.PeriodKey.Value && t.B.RowIds.Contains(c.TableRowId))
            .ToListAsync().ConfigureAwait(true);
        Assert.Equal(2, cells.Count);
        Assert.All(cells, c => Assert.Equal(t.BTargetTableDefId, c.TableDefId));
        Assert.Equal("b", cells.Single(c => c.ColumnDefId == t.BTargetColumns[0]).ValueString);
        Assert.Equal(7m, cells.Single(c => c.ColumnDefId == t.BTargetColumns[1]).ValueNumeric);
        Assert.Equal(t.BTargetTableDefId, await db.TableInstances.AsNoTracking()
            .Where(i => i.PeriodKeyValue == t.B.PeriodKey.Value && i.Id == t.B.TableInstanceId)
            .Select(i => i.TableDefId).SingleAsync().ConfigureAwait(true));
        Assert.Equal([t.BTargetSheetDefId], await db.DocumentSheets.AsNoTracking()
            .Where(s => s.DocumentId == t.B.DocumentId).Select(s => s.SheetDefId).ToListAsync().ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B74")]
    public async Task Перенос_документа_основного_шаблону_не_чіпає_приєднаний_шаблон()
    {
        var t = await ArrangeTwoTemplatesAsync().ConfigureAwait(true);
        var bBefore = await DocumentSnapshotAsync(t.B).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, t.A.UserName).ConfigureAwait(true);

        var response = await PostMigrateAsync(client, t.A.Doc.DocumentId, t.A.TargetVersionId, dryRun: false).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {app.ErrorsText}");
        Assert.Equal(1, JsonDocument.Parse(body).RootElement.GetProperty("documentCount").GetInt32());

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        // Основний шаблон: версія проєкту (до P6 — дубль версії основного) і пара A — нова версія.
        Assert.Equal(t.A.TargetVersionId, await db.Projects.AsNoTracking()
            .Where(p => p.Id == t.A.Doc.ProjectId).Select(p => p.TemplateVersionId).SingleAsync().ConfigureAwait(true));
        Assert.Equal(t.A.TargetVersionId, await PairVersionAsync(db, t.A.Doc.ProjectId, t.A.Doc.TemplateId).ConfigureAwait(true));
        Assert.Equal([$"{t.A.Doc.TemplateId}:202601:{t.A.TargetVersionId}"],
            await TimelineAsync(db, t.A.Doc.ProjectId, t.A.Doc.TemplateId).ConfigureAwait(true));

        // ⛔ Предмет тесту: документ B, його пара і шкала — без змін.
        Assert.Equal(bBefore, await DocumentSnapshotAsync(t.B).ConfigureAwait(true));
        Assert.Equal(t.B.TemplateVersionId, await PairVersionAsync(db, t.A.Doc.ProjectId, t.B.TemplateId).ConfigureAwait(true));
        Assert.Equal([$"{t.B.TemplateId}:202601:{t.B.TemplateVersionId}"],
            await TimelineAsync(db, t.A.Doc.ProjectId, t.B.TemplateId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B74")]
    public async Task Версія_основного_шаблону_для_документа_приєднаного_шаблону_відмова_іншого_шаблону()
    {
        var t = await ArrangeTwoTemplatesAsync().ConfigureAwait(true);
        var bBefore = await DocumentSnapshotAsync(t.B).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, t.A.UserName).ConfigureAwait(true);

        foreach (var dryRun in new[] { true, false })
        {
            var response = await PostMigrateAsync(client, t.B.DocumentId, t.A.TargetVersionId, dryRun).ConfigureAwait(true);
            await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "ECR-TMPL-0422", "err.ECR-TMPL-0422.migrateOtherTemplate")
                .ConfigureAwait(true);
        }

        Assert.Equal(bBefore, await DocumentSnapshotAsync(t.B).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B74")]
    public async Task Перелік_цілей_документа_приєднаного_шаблону_лише_версії_його_шаблону()
    {
        var t = await ArrangeTwoTemplatesAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, t.A.UserName).ConfigureAwait(true);

        async Task<(int Current, List<int> Targets)> TargetsAsync(long documentId)
        {
            var response = await client.GetAsync(
                new Uri($"/api/v1/documents/{documentId.ToString(CultureInfo.InvariantCulture)}/migrate-version", UriKind.Relative))
                .ConfigureAwait(true);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
            var root = JsonDocument.Parse(body).RootElement;
            return (root.GetProperty("currentVersionId").GetInt32(),
                [.. root.GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("id").GetInt32())]);
        }

        var b = await TargetsAsync(t.B.DocumentId).ConfigureAwait(true);
        Assert.Equal(t.B.TemplateVersionId, b.Current);
        Assert.Equal([t.BTargetVersionId], b.Targets);

        var a = await TargetsAsync(t.A.Doc.DocumentId).ConfigureAwait(true);
        Assert.Equal(t.A.Doc.TemplateVersionId, a.Current);
        Assert.Equal([t.A.TargetVersionId], a.Targets);
    }

    /// <summary>
    /// «Інші проєкти на вихідній версії» (D-1b P3) — за ПАРОЮ: проєкт, де шаблон B приєднано на B v1 (рядок
    /// <c>doc.ProjectTemplate</c>), рахується з документом B; документ іншого шаблону в проєкті, чий основний — B v1, — ні.
    /// </summary>
    /// <remarks>
    /// Без пари (лише <c>Project.TemplateVersionId</c>) — 1 проєкт і 1 документ (чужий d1); без фільтра документів за
    /// шаблоном — 2 документи.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B74")]
    public async Task Інші_проєкти_на_вихідній_версії_рахуються_за_парою_проєкт_шаблон()
    {
        var t = await ArrangeTwoTemplatesAsync().ConfigureAwait(true);
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var c = await builder.BuildAsync(t.B.PeriodKey.Value, columnCount: 1, rowCount: 1).ConfigureAwait(true);
        var d = await builder.BuildAsync(t.B.PeriodKey.Value, columnCount: 1, rowCount: 1).ConfigureAwait(true);
        // Початковий проєкт ланцюга B (TestDocument зберіг його Id): основний шаблон — B v1, власних документів після
        // переносу b1 у проєкт A немає.
        var bHome = t.B.ProjectId;
        await using (var db = builder.CreateContext())
        {
            // c1 — документ шаблону B у ТРЕТЬОМУ проєкті, де B приєднано на B v1; d1 — документ шаблону D у проєкті bHome.
            await db.Database.ExecuteSqlAsync($"""
                INSERT doc.ProjectTemplate (ProjectId, TemplateId, CurrentTemplateVersionId) VALUES ({c.ProjectId}, {t.B.TemplateId}, {t.B.TemplateVersionId});
                UPDATE doc.Document SET TemplateId = {t.B.TemplateId} WHERE Id = {c.DocumentId};
                UPDATE doc.Document SET ProjectId = {bHome}, TemplateId = {d.TemplateId} WHERE Id = {d.DocumentId};
                """).ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, t.A.UserName).ConfigureAwait(true);

        var dry = await PostMigrateAsync(client, t.B.DocumentId, t.BTargetVersionId, dryRun: true).ConfigureAwait(true);
        var body = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {body} {app.ErrorsText}");
        var report = JsonDocument.Parse(body).RootElement;
        Assert.Equal(2, report.GetProperty("otherProjectsOnSourceVersion").GetInt32());
        Assert.Equal(1, report.GetProperty("otherDocumentsOnSourceVersion").GetInt32());
    }

    /// <summary>
    /// Фонова постановка переносу приєднаного шаблону, поки йде перенос основного шаблону того самого проєкту, — ОКРЕМА
    /// задача (ціль — пара), а не <c>jobId</c> переносу основного; обидві виконуються (друга — після першої, блок проєкту).
    /// </summary>
    /// <remarks>Мутація: <c>TargetOf(projectId)</c> для обох шаблонів — другий запит отримує <c>jobId</c> першого, B не переноситься.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B74")]
    public async Task Фоновий_перенос_приєднаного_шаблону_окрема_задача_поки_йде_перенос_основного()
    {
        var t = await ArrangeTwoTemplatesAsync(withOverride: false).ConfigureAwait(true);
        var pause = new ApplyPause();

        using var baseApp = new EcrApiFactory(sql);
        using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IDocumentVersionMigrationStore>(sp => new PausingMigrationStore(
                new DocumentVersionMigrationStore(sp.GetRequiredService<EcrDbContext>()), pause))));
        using var client = await SignedInAsync(app, t.A.UserName, baseApp).ConfigureAwait(true);

        async Task<string> EnqueueAsync(long documentId, int targetVersionId)
        {
            var response = await client.PostAsJsonAsync(
                new Uri($"/api/v1/documents/{documentId.ToString(CultureInfo.InvariantCulture)}/migrate-version", UriKind.Relative),
                new { targetVersionId, mode = "Safe", dryRun = false, async = true }).ConfigureAwait(true);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{response.StatusCode}: {body} {baseApp.ErrorsText}");
            return JsonDocument.Parse(body).RootElement.GetProperty("jobId").GetString()!;
        }

        var jobA = await EnqueueAsync(t.A.Doc.DocumentId, t.A.TargetVersionId).ConfigureAwait(true);
        await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(true);

        var jobB = await EnqueueAsync(t.B.DocumentId, t.BTargetVersionId).ConfigureAwait(true);
        Assert.NotEqual(jobA, jobB);

        pause.Release.TrySetResult();
        foreach (var jobId in new[] { jobA, jobB })
        {
            var job = await WaitJobAsync(client, jobId, baseApp).ConfigureAwait(true);
            Assert.True(job.GetProperty("state").GetString() == "Succeeded", $"задача {jobId}: {job.GetRawText()}\n{baseApp.ErrorsText}");
        }

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        Assert.Equal(t.A.TargetVersionId, await PairVersionAsync(db, t.A.Doc.ProjectId, t.A.Doc.TemplateId).ConfigureAwait(true));
        Assert.Equal(t.BTargetVersionId, await PairVersionAsync(db, t.A.Doc.ProjectId, t.B.TemplateId).ConfigureAwait(true));
        Assert.Equal(t.A.TargetVersionId, await db.Projects.AsNoTracking()
            .Where(p => p.Id == t.A.Doc.ProjectId).Select(p => p.TemplateVersionId).SingleAsync().ConfigureAwait(true));
    }

    private static Task<HttpResponseMessage> PostMigrateAsync(HttpClient client, long documentId, int targetVersionId, bool dryRun)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId.ToString(CultureInfo.InvariantCulture)}/migrate-version", UriKind.Relative),
            new { targetVersionId, mode = "Safe", dryRun });

    private static Task<int> PairVersionAsync(EcrDbContext db, int projectId, int templateId)
        => db.ProjectTemplates.AsNoTracking()
            .Where(p => p.ProjectId == projectId && p.TemplateId == templateId)
            .Select(p => p.CurrentTemplateVersionId)
            .SingleAsync();

    private static async Task<List<string>> TimelineAsync(EcrDbContext db, int projectId, int templateId)
        => await db.Database
            .SqlQuery<string>($"""
                SELECT CONCAT(TemplateId, ':', FromPeriodKey, ':', TemplateVersionId) AS Value
                FROM   doc.ProjectTemplateVersion
                WHERE  ProjectId = {projectId} AND TemplateId = {templateId}
                ORDER BY FromPeriodKey
                """)
            .ToListAsync().ConfigureAwait(false);

    /// <summary>Документ без версії проєкту: склад, екземпляр, рядки, комірки, позначки ручних значень.</summary>
    private async Task<string> DocumentSnapshotAsync(TestDocument d)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        var cells = await db.CellValues.AsNoTracking()
            .Where(c => c.PeriodKeyValue == d.PeriodKey.Value && d.RowIds.Contains(c.TableRowId))
            .OrderBy(c => c.TableRowId).ThenBy(c => c.ColumnDefId)
            .Select(c => $"{c.TableRowId}/{c.ColumnDefId}/{c.TableDefId}/{c.ValueString}/{c.ValueNumeric}/{c.IsEmpty}/{c.IsCalculated}")
            .ToListAsync().ConfigureAwait(false);
        var rows = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == d.PeriodKey.Value && r.TableInstanceId == d.TableInstanceId)
            .OrderBy(r => r.Id).Select(r => $"{r.Id}/{r.RowKeyValue}/{r.RowDefId}/{r.IsDeleted}")
            .ToListAsync().ConfigureAwait(false);
        var instance = await db.TableInstances.AsNoTracking()
            .Where(i => i.PeriodKeyValue == d.PeriodKey.Value && i.Id == d.TableInstanceId)
            .Select(i => i.TableDefId).SingleAsync().ConfigureAwait(false);
        var sheets = await db.DocumentSheets.AsNoTracking()
            .Where(x => x.DocumentId == d.DocumentId).Select(x => x.SheetDefId).ToListAsync().ConfigureAwait(false);

        return $"t{instance}|s{string.Join(",", sheets)}|{string.Join(";", cells)}|{string.Join(";", rows)}";
    }

    /// <summary>
    /// Проєкт сценарію <see cref="Target.OnlyLabels"/> (основний шаблон A, документ a1, ціль A v2) + приєднаний шаблон B:
    /// ланцюг будівника B перенесено в проєкт A (<c>Document.TemplateId = B</c>), рядки <c>doc.ProjectTemplate</c> і шкали
    /// для A і B, значення «b»/7 у b1, ціль B v2 з тими самими кодами. На комірці C2 документа a1 — позначка ручного значення.
    /// </summary>
    /// <param name="withOverride">
    /// Позначка ручного значення на a1. ⚠ Без неї — для тестів зі сховищем-обгорткою (<c>PausingMigrationStore</c>): обгортка
    /// йде старим шляхом «усе в одній транзакції» (важка частина на окремому з'єднанні ПІСЛЯ зняття позначки в T2), і
    /// діапазон комірок чекав би власну T2 на рядку цієї комірки. Справжній шлях (MoveData між T1 і T2) цього не має.
    /// </param>
    private async Task<TwoTemplates> ArrangeTwoTemplatesAsync(bool withOverride = true)
    {
        var a = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(false);
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var b = await builder.BuildAsync(a.Doc.PeriodKey.Value, columnCount: 3, rowCount: 2).ConfigureAwait(false);
        var tag = b.SheetCode["SHEET".Length..];
        var now = new DateTime(2026, 1, 16, 9, 0, 0, DateTimeKind.Utc);
        var projectId = a.Doc.ProjectId;
        var period = a.Doc.PeriodKey.Value;

        await using var db = builder.CreateContext();

        await db.Database.ExecuteSqlAsync($"""
            UPDATE doc.Document SET ProjectId = {projectId}, TemplateId = {b.TemplateId} WHERE Id = {b.DocumentId};
            INSERT doc.ProjectTemplate (ProjectId, TemplateId, CurrentTemplateVersionId)
            VALUES ({projectId}, {a.Doc.TemplateId}, {a.Doc.TemplateVersionId}), ({projectId}, {b.TemplateId}, {b.TemplateVersionId});
            INSERT doc.ProjectTemplateVersion (ProjectId, TemplateId, FromPeriodKey, TemplateVersionId)
            VALUES ({projectId}, {a.Doc.TemplateId}, {period}, {a.Doc.TemplateVersionId}), ({projectId}, {b.TemplateId}, {period}, {b.TemplateVersionId});
            """).ConfigureAwait(false);

        var document = await db.Documents.Include(d => d.Sheets).SingleAsync(d => d.Id == b.DocumentId).ConfigureAwait(false);
        document.IncludeSheet(b.SheetDefId);
        db.CellValues.Add(new CellValue(
            new CellAddress(b.PeriodKey, b.RowIds[0], b.ColumnDefIds[0]), b.TableDefId, new CellValueData { ValueString = "b" }));
        db.CellValues.Add(new CellValue(
            new CellAddress(b.PeriodKey, b.RowIds[0], b.ColumnDefIds[1]), b.TableDefId, new CellValueData { ValueNumeric = 7m }));
        if (withOverride)
        {
            db.CellFormulaOverrides.Add(new CellFormulaOverride(
                new CellAddress(a.Doc.PeriodKey, a.Doc.RowIds[0], a.Doc.ColumnDefIds[1]), a.Doc.TableInstanceId, "manual", a.UserId, now));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        // ── B v2: ті самі коди аркуша, таблиці, колонок і рядків ──
        var version = new TemplateVersion(b.TemplateId, $"2.0.0.{tag}", 1, now, b.TemplateVersionId);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var sheet = new SheetDef(version.Id, EcrCode.Create(b.SheetCode), Name($"Sheet {tag} v2"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var tableCode = await db.TableDefs.AsNoTracking()
            .Where(x => x.Id == b.TableDefId).Select(x => x.Code).SingleAsync().ConfigureAwait(false);
        var table = new TableDef(
            sheet.Id, EcrCode.Create(tableCode), Name($"Table {tag} v2"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var columns = Enumerable.Range(1, 3)
            .Select(i => new ColumnDef(
                table.Id, EcrCode.Create($"C{i}_{tag}"), Name($"C{i} v2"), i, i == 1 ? CellDataType.String : CellDataType.Decimal))
            .ToList();
        db.ColumnDefs.AddRange(columns);
        db.RowDefs.AddRange(Enumerable.Range(1, 2)
            .Select(i => new RowDef(table.Id, RowKey.Create($"R{i}_{tag}"), i, Name($"Row {i}"), RowKind.Item)));
        await db.SaveChangesAsync().ConfigureAwait(false);

        version.Publish(1, now);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new TwoTemplates(a, b, version.Id, sheet.Id, table.Id, [.. columns.Select(c => c.Id)]);
    }

    private sealed record TwoTemplates(
        Scenario A,
        TestDocument B,
        int BTargetVersionId,
        int BTargetSheetDefId,
        int BTargetTableDefId,
        IReadOnlyList<int> BTargetColumns);
}
