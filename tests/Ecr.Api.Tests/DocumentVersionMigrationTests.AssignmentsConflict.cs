// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.AssignmentsConflict.cs
using System.Net;
using System.Text.Json;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D-10-b (аудит 2026-11, рішення координатора 12.11): мапа перейменовує таблицю <c>X → Y</c>, а в документі вже є
/// передача зрізу під <c>Y</c> з тим самим зрізом. Перенос мусить відмовити НА ЕТАПІ ПЛАНУ (<c>assignmentsConflict</c>),
/// до маркера «перенос у процесі», а не падати на <c>UQ_EditAssignment</c>.
/// </summary>
/// <remarks>
/// Червоне до фіксу (крок відмови в <c>MigrateDocumentVersionHandler.PlanAsync</c> прибрано): сухий прогін
/// <c>canApply = true</c> без причини, застосування — 500 (<c>SqlException 2601</c> на <c>UQ_EditAssignment</c> у
/// <c>RenameTableCodesAsync</c>), відкат усього переносу із загальною помилкою.
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-10-b")]
    public async Task Rename_onto_existing_assignment_slice_is_refused_before_marker_and_project_is_not_stuck()
    {
        var s = await ArrangeAsync(Target.TableRenamed).ConfigureAwait(true);
        var executor = await NewProjectUserAsync(s, "d10b", GrantLevel.Write, "Document.View").ConfigureAwait(true);
        var onX = await AddAssignmentAsync(s, s.SourceTableCode, executor.Id).ConfigureAwait(true);
        var onY = await AddAssignmentAsync(s, s.TargetTableCode, executor.Id).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);
        var assignmentsBefore = await AssignmentKeysAsync(onX, onY).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = await PostWithRenamesAsync(client, s, dryRun: true, Renames(s)).ConfigureAwait(true);
        var dryBody = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {dryBody} {app.ErrorsText}");
        var report = JsonDocument.Parse(dryBody).RootElement;
        Assert.False(report.GetProperty("canApply").GetBoolean(), dryBody);
        Assert.Equal(
            ["assignmentsConflict"],
            report.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()).ToList());

        var applied = await PostWithRenamesAsync(client, s, dryRun: false, Renames(s)).ConfigureAwait(true);
        var problem = await AssertProblemAsync(
            applied, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateAssignmentsConflict").ConfigureAwait(true);
        Assert.Equal("1", JsonDocument.Parse(problem).RootElement.GetProperty("assignmentConflicts").GetString());

        // ⛔ Предмет тесту: відмова нічого не лишила — ні маркера, ні нової версії, ні змінених передач.
        Assert.False(await HasMigrationMarkerAsync(s.Doc.ProjectId).ConfigureAwait(true), "маркер migratingTo лишився — проєкт застряг");
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
        Assert.Equal(assignmentsBefore, await AssignmentKeysAsync(onX, onY).ConfigureAwait(true));

        // Проєкт не застряг: зняли передачу під Y — той самий перенос проходить, передача X переїжджає на Y.
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            await db.EditAssignmentRecipients.Where(r => r.AssignmentId == onY).ExecuteDeleteAsync().ConfigureAwait(true);
            await db.EditAssignments.Where(a => a.Id == onY).ExecuteDeleteAsync().ConfigureAwait(true);
        }

        var retried = await PostWithRenamesAsync(client, s, dryRun: false, Renames(s)).ConfigureAwait(true);
        var retriedBody = await retried.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(retried.StatusCode == HttpStatusCode.OK, $"{retried.StatusCode}: {retriedBody} {app.ErrorsText}");

        await using var check = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        Assert.Equal(s.TargetVersionId, await check.Projects.AsNoTracking().Where(p => p.Id == s.Doc.ProjectId).Select(p => p.TemplateVersionId).SingleAsync().ConfigureAwait(true));
        Assert.Equal(s.TargetTableCode, await check.EditAssignments.AsNoTracking().Where(a => a.Id == onX).Select(a => a.TableCode).SingleAsync().ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-10-b")]
    public async Task Assignment_under_Y_with_another_slice_does_not_block_the_rename()
    {
        // Контроль хибної відмови: під Y є передача іншого ЗРІЗУ (явний період) — ключі різні, перенос іде.
        var s = await ArrangeAsync(Target.TableRenamed).ConfigureAwait(true);
        var executor = await NewProjectUserAsync(s, "d10c", GrantLevel.Write, "Document.View").ConfigureAwait(true);
        var onX = await AddAssignmentAsync(s, s.SourceTableCode, executor.Id).ConfigureAwait(true);
        await AddAssignmentAsync(s, s.TargetTableCode, executor.Id, periodKey: s.Doc.PeriodKey.Value).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = await PostWithRenamesAsync(client, s, dryRun: true, Renames(s)).ConfigureAwait(true);
        var dryBody = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {dryBody} {app.ErrorsText}");
        Assert.True(JsonDocument.Parse(dryBody).RootElement.GetProperty("canApply").GetBoolean(), dryBody);

        var applied = await PostWithRenamesAsync(client, s, dryRun: false, Renames(s)).ConfigureAwait(true);
        var body = await applied.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(applied.StatusCode == HttpStatusCode.OK, $"{applied.StatusCode}: {body} {app.ErrorsText}");

        await using var check = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        Assert.Equal(s.TargetTableCode, await check.EditAssignments.AsNoTracking().Where(a => a.Id == onX).Select(a => a.TableCode).SingleAsync().ConfigureAwait(true));
    }

    /// <summary>Передача зрізу «аркуш × таблиця <paramref name="tableCode"/>» рівня Write одному виконавцю.</summary>
    private async Task<long> AddAssignmentAsync(Scenario s, string tableCode, int executorId, int? periodKey = null)
    {
        var now = DateTime.UtcNow;
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var assignment = new EditAssignment(
            s.Doc.DocumentId,
            EditAssignmentSlice.Create(s.Doc.SheetCode, tableCode, monthNumber: null, periodKey: periodKey),
            isExclusive: false,
            delegatedByUserId: s.UserId,
            handoffId: Guid.NewGuid(),
            createdByUserId: s.UserId,
            utcNow: now,
            level: GrantLevel.Write);
        assignment.AddRecipient(executorId, s.UserId, now);
        new EditAssignmentStore(db).Add(assignment);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return assignment.Id;
    }

    private async Task<string> AssignmentKeysAsync(params long[] ids)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var rows = await db.EditAssignments.AsNoTracking()
            .Where(a => ids.Contains(a.Id)).OrderBy(a => a.Id)
            .Select(a => a.Id + "/" + a.TableCode + "/" + a.SliceKey)
            .ToListAsync().ConfigureAwait(false);
        return string.Join(";", rows);
    }
}
