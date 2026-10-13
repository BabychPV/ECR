// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.LockedSafe.cs
using System.Net;
using System.Text.Json;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// LP-32 («locked-safe»): подані/затверджені аркуші самі по собі перенос на нову версію не блокують — відмова
/// <c>409 ECR-DOC-0409</c> лише коли план їх ЗАЧІПАЄ (втрата/видалення, зміна тлумачення, нові рядки фіксованої
/// таблиці, перейменування). Незачеплені переносяться зі статусом, значеннями й журналом погодження.
/// </summary>
/// <remarks>
/// ⚠ Справжнім HTTP на справжньому SQL Server. Мутаційні докази (у <c>MigrateDocumentVersionHandler.PlanAsync</c> /
/// <c>VersionMigrationPlanner.TouchesLockedSheet</c>): повернути безумовне «<c>scope.LockedSheets &gt; 0</c> →
/// <c>sheetsLocked</c>» — червоніють «нова колонка…» і «лише підписи…»; прибрати <c>Renamed</c> з
/// <c>TouchesLockedSheet</c> — червоніє «перейменування…»; прибрати <c>newRows.Count &gt; newRowsBefore</c> — червоніє
/// «нові рядки…» (там немає прибраних колонок, тож нічого іншого не зачеплено).
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    [Theory]
    [InlineData(Target.AddsC4Only, false)]
    [InlineData(Target.AddsC4Only, true)]
    [InlineData(Target.OnlyLabels, false)]
    [InlineData(Target.C2PrecisionWidened, true)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    [Trait("Task", "LP-32")]
    public async Task Подані_та_затверджені_аркуші_яких_план_не_зачіпає_переносяться_зі_статусом_значеннями_і_журналом(
        Target target, bool approved)
    {
        var s = await ArrangeAsync(target, submitted: true, approved: approved).ConfigureAwait(true);
        var expectedStatus = approved ? DocumentStatus.Approved : DocumentStatus.Submitted;

        await using (var before = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            Assert.Equal(
                expectedStatus,
                (await before.ApprovalStates.AsNoTracking().SingleAsync(a => a.DocumentId == s.Doc.DocumentId).ConfigureAwait(true)).Status);
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        // Сухий прогін: дозволено, і звіт каже, скільки подано/затверджено переїде без змін.
        var dry = await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true);
        var dryBody = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {dryBody} {app.ErrorsText}");
        var dryReport = JsonDocument.Parse(dryBody).RootElement;
        Assert.True(dryReport.GetProperty("canApply").GetBoolean(), dryBody);
        Assert.DoesNotContain("sheetsLocked", dryReport.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(1, dryReport.GetProperty("lockedSheets").GetInt32());
        Assert.Equal(1, dryReport.GetProperty("lockedSheetsCarried").GetInt32());
        Assert.Equal(0, dryReport.GetProperty("lockedSheetsAffected").GetInt32());
        var dryItem = Assert.Single(dryReport.GetProperty("lockedSheetItems").EnumerateArray());
        Assert.False(dryItem.GetProperty("affected").GetBoolean());
        Assert.Equal(expectedStatus.ToString(), dryItem.GetProperty("status").GetString());
        Assert.Equal(s.Doc.SheetCode, dryItem.GetProperty("sheetCode").GetString());

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {app.ErrorsText}");
        var report = JsonDocument.Parse(body).RootElement;
        Assert.True(report.GetProperty("applied").GetBoolean());
        Assert.Equal(1, report.GetProperty("lockedSheetsCarried").GetInt32());

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        var project = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == s.Doc.ProjectId).ConfigureAwait(true);
        Assert.Equal(s.TargetVersionId, project.TemplateVersionId);

        // ⛔ Предмет тесту: статус аркуша лишився (і на НОВОМУ SheetDefId), без повернення в Draft.
        var state = await db.ApprovalStates.AsNoTracking()
            .SingleAsync(a => a.DocumentId == s.Doc.DocumentId).ConfigureAwait(true);
        Assert.Equal(s.TargetSheetDefId, state.SheetDefId);
        Assert.Equal(expectedStatus, state.Status);
        Assert.Equal(s.Doc.PeriodKey.Value, state.PeriodKey);
        Assert.NotNull(state.SubmittedAt);
        Assert.Equal(approved, state.ApprovedAt is not null);

        // Журнал погодження не втрачено й не доповнено: ті самі події, перемаплені на новий аркуш.
        var events = await db.ApprovalEvents.AsNoTracking()
            .Where(e => e.DocumentId == s.Doc.DocumentId).OrderBy(e => e.Id).ToListAsync().ConfigureAwait(true);
        Assert.Equal(approved ? 2 : 1, events.Count);
        Assert.All(events, e => Assert.Equal(s.TargetSheetDefId, e.SheetDefId));

        // Значення на тих самих кодах, на колонках нової версії; нічого не загублено.
        var cells = await db.CellValues.AsNoTracking()
            .Where(c => c.PeriodKeyValue == s.Doc.PeriodKey.Value && s.Doc.RowIds.Contains(c.TableRowId) && !c.IsEmpty)
            .ToListAsync().ConfigureAwait(true);
        Assert.Equal("a", cells.Single(c => c.ColumnDefId == s.TargetColumns["C1"]).ValueString);
        Assert.Equal(42m, cells.Single(c => c.ColumnDefId == s.TargetColumns["C2"]).ValueNumeric);
        Assert.Equal(2, cells.Count);

        // Склад документа переїхав на новий аркуш, аркуш не зник.
        var sheets = await db.DocumentSheets.AsNoTracking()
            .Where(x => x.DocumentId == s.Doc.DocumentId).Select(x => x.SheetDefId).ToListAsync().ConfigureAwait(true);
        Assert.Equal([s.TargetSheetDefId], sheets);

        // Один запис журналу безпеки, з двома новими полями.
        var audit = await db.Database
            .SqlQuery<string>($"SELECT ISNULL(DetailsJson, N'') AS Value FROM aud.SecurityEvent WHERE EventType = N'DocumentVersionMigrated' AND ChangedByUserId = {s.UserId}")
            .ToListAsync().ConfigureAwait(true);
        var details = JsonDocument.Parse(Assert.Single(audit)).RootElement;
        Assert.Equal(1, details.GetProperty("lockedSheetsCarried").GetInt32());
        Assert.Equal(0, details.GetProperty("lockedSheetsAffected").GetInt32());
    }

    [Theory]
    [InlineData(Target.DropsC2, false)]
    [InlineData(Target.DropsC3AddsC4AndRow, true)]
    [InlineData(Target.C2BecomesText, false)]
    [InlineData(Target.RowModeBecomesMixed, true)]
    [InlineData(Target.TableRenamed, false)]
    [InlineData(Target.C2ScaleNarrowed, true)]
    [InlineData(Target.AddsRowOnly, false)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    [Trait("Task", "LP-32")]
    public async Task Подані_аркуші_яких_зачіпає_перенос_дають_409_з_переліком_і_нічого_не_змінюється(
        Target target, bool approved)
    {
        var s = await ArrangeAsync(target, submitted: true, approved: approved).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true);
        var dryBody = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {dryBody}");
        var report = JsonDocument.Parse(dryBody).RootElement;
        Assert.False(report.GetProperty("canApply").GetBoolean());
        Assert.Contains("sheetsLocked", report.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(1, report.GetProperty("lockedSheetsAffected").GetInt32());
        Assert.Equal(0, report.GetProperty("lockedSheetsCarried").GetInt32());
        Assert.True(Assert.Single(report.GetProperty("lockedSheetItems").EnumerateArray()).GetProperty("affected").GetBoolean());

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        var problem = await AssertProblemAsync(
            response, HttpStatusCode.Conflict, "ECR-DOC-0409", "err.ECR-DOC-0409.migrateSheetsLocked").ConfigureAwait(true);

        var root = JsonDocument.Parse(problem).RootElement;
        Assert.Equal("1", root.GetProperty("lockedSheetsAffected").GetString());
        var listed = root.GetProperty("lockedSheetItems").EnumerateArray().Select(i => i.GetString()).ToList();
        var status = approved ? "Approved" : "Submitted";
        Assert.Equal($"{s.Doc.SheetCode}|{s.Doc.PeriodKey.Value}|{status}", Assert.Single(listed));

        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var state = await db.ApprovalStates.AsNoTracking()
            .SingleAsync(a => a.DocumentId == s.Doc.DocumentId).ConfigureAwait(true);
        Assert.Equal(s.Doc.SheetDefId, state.SheetDefId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    [Trait("Task", "LP-32")]
    public async Task Перейменування_таблиці_з_мапою_зачіпає_подане_і_дає_409()
    {
        // Мапа робить план «безпечним» для даних (нічого не губиться), але код таблиці змінюється, а подане
        // стосувалося таблиці зі старим кодом — тож це все одно зачеплений аркуш.
        var s = await ArrangeAsync(Target.TableRenamed, submitted: true).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = await PostWithRenamesAsync(client, s, dryRun: true, Renames(s)).ConfigureAwait(true);
        var report = JsonDocument.Parse(await dry.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal(0, report.GetProperty("lostValues").GetInt64());
        Assert.Contains("sheetsLocked", report.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));

        var response = await PostWithRenamesAsync(client, s, dryRun: false, Renames(s)).ConfigureAwait(true);
        await AssertProblemAsync(
            response, HttpStatusCode.Conflict, "ECR-DOC-0409", "err.ECR-DOC-0409.migrateSheetsLocked").ConfigureAwait(true);
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }
}
