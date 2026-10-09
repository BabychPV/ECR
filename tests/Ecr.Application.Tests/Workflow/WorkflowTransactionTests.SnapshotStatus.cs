// tests/Ecr.Application.Tests/Workflow/WorkflowTransactionTests.SnapshotStatus.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// R5-W1: статус зрізу звітності після переходів аркуша — СПРАВЖНІМ будівником
/// (<see cref="ReportSnapshotBuilder"/>) і справжнім <see cref="WorkflowStore"/>.
/// </summary>
/// <remarks>
/// ⛔ Решта тестів робочого процесу підставляла <c>IReportSnapshotBuilder</c> через
/// NSubstitute (<c>RefreshStatusAsync(...).Returns(Submitted)</c>), тож порядок «перехід
/// у БД → запит статусу» не перевіряв ніхто — і він був зворотний (W1-01). Тут
/// предмет — саме цей порядок, склад аркушів (W1-02), Reopen (W1-03), ефективний
/// стан періоду (W1-04) і роль кроку всередині транзакції (W1-05).
///
/// ⚠ Документ має ДВА аркуші складу: з одним аркушем «усі подано» і «цей подано»
/// збігаються, і дефект W1-02 не був би видимий.
/// </remarks>
public sealed partial class WorkflowTransactionTests
{
    // ─────────────────────────── W1-01 ───────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task W1_01_Подання_останнього_аркуша_морозить_зріз()
    {
        // Без фіксу: `SaveSnapshotAsync` зберіг рядок стану ще `Draft`, `state.Submit`
        // лишався в трекері, і запит статусу (`AsNoTracking`) бачив {Draft, Submitted} →
        // `Draft`: зріз не морозився НІКОЛИ.
        var world = await ArrangeSnapshotAsync(sheet: null, other: DocumentStatus.Submitted).ConfigureAwait(true);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        await using (var db = CreateContext())
        {
            await Submit(world.World, db, Access(), reportSnapshots: Builder(db, cache))
                .HandleAsync(world.World.DocumentId, world.World.SheetDefId, PeriodKeyValue, CancellationToken.None)
                .ConfigureAwait(true);
        }

        var snapshot = await ReportSnapshotAsync(world.SnapshotId).ConfigureAwait(true);
        Assert.Equal(SnapshotStatus.Submitted, snapshot.Status);
        Assert.Equal(UserId, snapshot.BuiltByUserId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-65")]
    public async Task W1_01_Відкликання_не_морозить_зріз_як_поданий()
    {
        // Без фіксу: обидва аркуші ще `Submitted` у БД, перехід `Recall` — лише в
        // трекері; статус рахувався як `Submitted`, і `RefreshStatus(Submitted)` робив
        // зріз іммутабельним саме тоді, коли аркуш повертався в `Draft`.
        var world = await ArrangeSnapshotAsync(DocumentStatus.Submitted, DocumentStatus.Submitted).ConfigureAwait(true);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        await using (var db = CreateContext())
        {
            await Recall(world.World, db, UserId, GrantLevel.Submit, reportSnapshots: Builder(db, cache))
                .HandleAsync(world.World.DocumentId, world.World.SheetDefId, PeriodKeyValue, "уточнення", CancellationToken.None)
                .ConfigureAwait(true);
        }

        Assert.Equal(DocumentStatus.Draft, await StatusAsync(world.World).ConfigureAwait(true));
        Assert.Equal(SnapshotStatus.Draft, (await ReportSnapshotAsync(world.SnapshotId).ConfigureAwait(true)).Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-65")]
    public async Task W1_01_Затвердження_останнього_аркуша_дає_зрізу_Approved()
    {
        // Без фіксу: {Approved, Submitted (ще в БД)} → `Submitted`, і зріз морозився як
        // поданий замість `Approved` — статус `Approved` через робочий процес був недосяжний.
        var world = await ArrangeSnapshotAsync(DocumentStatus.Submitted, DocumentStatus.Approved).ConfigureAwait(true);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        await using (var db = CreateContext())
        {
            await ApproveWith(world.World, db, AccessAt(step: null), Builder(db, cache))
                .HandleAsync(
                    world.World.DocumentId, world.World.SheetDefId, PeriodKeyValue, approved: true, reason: null,
                    CancellationToken.None)
                .ConfigureAwait(true);
        }

        Assert.Equal(SnapshotStatus.Approved, (await ReportSnapshotAsync(world.SnapshotId).ConfigureAwait(true)).Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task W1_01_Конфлікт_RowVersion_при_наявному_зрізі_дає_409_а_не_500()
    {
        // Без фіксу: сирий `db.SaveChangesAsync` у `RefreshStatusAsync` скидав і змінений
        // `ApprovalState` — `DbUpdateConcurrencyException` оминав переклад `UnitOfWork`
        // і доїжджав до клієнта як 500.
        var world = await ArrangeSnapshotAsync(DocumentStatus.Submitted, other: null).ConfigureAwait(true);

        var access = AccessAt(step: null);
        access.CurrentApprovalStepAsync(
                  Arg.Any<long>(), Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
              .Returns(_ =>
              {
                  TouchState(world.World.DocumentId);
                  return (ApprovalStepView?)null;
              });

        using var cache = new MemoryCache(new MemoryCacheOptions());
        await using var db = CreateContext();

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => ApproveWith(world.World, db, access, Builder(db, cache))
                .HandleAsync(
                    world.World.DocumentId, world.World.SheetDefId, PeriodKeyValue, approved: true, reason: null,
                    CancellationToken.None))
            .ConfigureAwait(true);

        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(world.World).ConfigureAwait(true));
        Assert.Equal(SnapshotStatus.Draft, (await ReportSnapshotAsync(world.SnapshotId).ConfigureAwait(true)).Status);
    }

    // ─────────────────────────── W1-02 ───────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-65")]
    public async Task W1_02_Аркуш_складу_без_рядка_стану_тримає_зріз_у_Draft()
    {
        // Без фіксу: рядок `wf.ApprovalState` є лише в поданого аркуша, другий (ще не
        // торканий) просто не потрапляв у перелік — {Submitted} → зріз `Submitted` і в `rpt.v_*`.
        var world = await ArrangeSnapshotAsync(DocumentStatus.Submitted, other: null).ConfigureAwait(true);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        await using var db = CreateContext();

        var status = await Builder(db, cache).RefreshStatusAsync(world.SnapshotId, CancellationToken.None)
            .ConfigureAwait(true);

        Assert.Equal(SnapshotStatus.Draft, status);
        Assert.Equal(SnapshotStatus.Draft, (await ReportSnapshotAsync(world.SnapshotId).ConfigureAwait(true)).Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-65")]
    public async Task W1_02_Аркуш_поза_складом_зі_старим_Draft_не_тримає_зріз()
    {
        // Без фіксу: виключений зі складу аркуш (`IsIncluded = 0`) зі старим рядком
        // `Draft` назавжди тримав зріз у `Draft` — склад не враховувався взагалі.
        var world = await ArrangeSnapshotAsync(DocumentStatus.Submitted, DocumentStatus.Draft).ConfigureAwait(true);
        await ExcludeSheetAsync(world.World.DocumentId, world.OtherSheetDefId).ConfigureAwait(true);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        await using var db = CreateContext();

        var status = await Builder(db, cache).RefreshStatusAsync(world.SnapshotId, CancellationToken.None)
            .ConfigureAwait(true);

        Assert.Equal(SnapshotStatus.Submitted, status);
    }

    // ─────────────────────────── W1-03 ───────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-65")]
    public async Task W1_03_Reopen_затвердженого_аркуша_повертає_незаморожений_зріз_у_Draft()
    {
        // Без фіксу: `ReopenDocumentHandler` зрізів не чіпав — зріз `Approved` лишався
        // `Approved` у `rpt.v_*`, хоча аркуш уже в роботі.
        var world = await ArrangeSnapshotAsync(
            DocumentStatus.Approved, DocumentStatus.Approved, SnapshotStatus.Approved).ConfigureAwait(true);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        await using (var db = CreateContext())
        {
            await ReopenWith(world.World, db, Builder(db, cache), Now)
                .HandleAsync(world.World.DocumentId, world.World.SheetDefId, PeriodKeyValue, "уточнення", CancellationToken.None)
                .ConfigureAwait(true);
        }

        Assert.Equal(DocumentStatus.Draft, await StatusAsync(world.World).ConfigureAwait(true));
        Assert.Equal(SnapshotStatus.Draft, (await ReportSnapshotAsync(world.SnapshotId).ConfigureAwait(true)).Status);
    }

    // ────────────────────────────── збірка ────────────────────────────

    private static ReportSnapshotBuilder Builder(EcrDbContext db, IMemoryCache cache)
        => new(db, new TestClock(Now), cache);

    private static ApproveSheetHandler ApproveWith(
        World world, EcrDbContext db, IAccessDecisionService access, IReportSnapshotBuilder snapshots)
        => new(
            new WorkflowStore(db), access, new ReportSnapshotSync(snapshots, Documents(world)),
            new UnitOfWork(db), Approver(), new TestClock(Now), new AuditWriter(db), Documents(world));

    private static ReopenDocumentHandler ReopenWith(
        World world, EcrDbContext db, IReportSnapshotBuilder snapshots, DateTime at)
        => new(
            new WorkflowStore(db), AccessAt(step: null), new UnitOfWork(db), User(), new TestClock(at),
            Documents(world), new ReportSnapshotSync(snapshots, Documents(world)));

    private async Task<ReportSnapshot> ReportSnapshotAsync(long snapshotId)
    {
        await using var db = CreateContext();
        return await db.ReportSnapshots.AsNoTracking().SingleAsync(s => s.Id == snapshotId).ConfigureAwait(false);
    }

    private async Task ExcludeSheetAsync(long documentId, int sheetDefId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE doc.DocumentSheet SET IsIncluded = 0 WHERE DocumentId = @documentId AND SheetDefId = @sheetDefId;";
        command.Parameters.AddWithValue("@documentId", documentId);
        command.Parameters.AddWithValue("@sheetDefId", sheetDefId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
    }

    // ────────────────────────────── підготовка ────────────────────────

    /// <summary>
    /// Документ із ДВОМА аркушами складу, період <c>Open</c> і поточний зріз звіту.
    /// </summary>
    /// <param name="sheet">Стан аркуша світу; <c>null</c> — рядка стану немає.</param>
    /// <param name="other">Стан другого аркуша; <c>null</c> — рядка стану немає.</param>
    /// <param name="snapshotStatus">Статус поточного (незамороженого) зрізу.</param>
    /// <param name="activeProject">Активний проєкт — період рахується за датами (F-08).</param>
    private async Task<SnapshotWorld> ArrangeSnapshotAsync(
        DocumentStatus? sheet,
        DocumentStatus? other,
        SnapshotStatus snapshotStatus = SnapshotStatus.Draft,
        bool activeProject = false)
    {
        await using var db = CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..10];

        var template = new Template(
            EcrCode.Create($"T{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Snapshot status" }),
            createdByUserId: 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new TemplateVersion(template.Id, "1.0.0.0", createdByUserId: 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var first = new SheetDef(
            version.Id, EcrCode.Create($"S{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet 1" }), 1);
        var second = new SheetDef(
            version.Id, EcrCode.Create($"Q{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet 2" }), 2);
        db.SheetDefs.AddRange(first, second);

        var project = new Project(
            EcrCode.Create($"P{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Snapshot status" }),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            templateVersionId: version.Id, PeriodKind.Monthly, periodPolicyId: 1, "Asia/Atyrau");
        if (activeProject)
        {
            project.Activate(Now);
        }

        db.Projects.Add(project);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var period = new Period(
            project.Id, new PeriodKey(PeriodKeyValue), 1,
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.RecomputeBoundaries(ProjectBuilder.Policy(), ProjectBuilder.Zone());
        period.AdvanceTo(PeriodState.Open, Now);
        db.Periods.Add(period);

        var document = new Document(project.Id, $"DOC-{Guid.NewGuid():N}"[..20], 1, Now);
        db.Documents.Add(document);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.DocumentSheets.AddRange(new DocumentSheet(document.Id, first.Id), new DocumentSheet(document.Id, second.Id));

        foreach (var (sheetDefId, status) in new[] { (first.Id, sheet), (second.Id, other) })
        {
            if (status is { } wanted)
            {
                db.ApprovalStates.Add(StateIn(document.Id, sheetDefId, wanted));
            }
        }

        var definition = new ReportDef(
            EcrCode.Create($"R{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "R5-W1" }),
            isRegulatory: true);
        db.ReportDefs.Add(definition);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var reportVersion = new ReportVersion(definition.Id, "1.0", "[]", "{}", Now);
        db.ReportVersions.Add(reportVersion);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var snapshot = new ReportSnapshot(
            reportVersion.Id, project.Id, PeriodKeyValue, snapshotStatus, Now, builtByUserId: null);
        snapshot.MakeCurrent();
        db.ReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new SnapshotWorld(new World(project.Id, version.Id, document.Id, first.Id), second.Id, snapshot.Id);
    }

    /// <summary>Рядок стану аркуша в заданому стані — тими самими доменними переходами.</summary>
    private static ApprovalState StateIn(long documentId, int sheetDefId, DocumentStatus status)
    {
        var state = new ApprovalState(documentId, sheetDefId, PeriodKeyValue);

        if (status is DocumentStatus.Submitted or DocumentStatus.Approved)
        {
            state.Submit(UserId, Now);
        }

        if (status == DocumentStatus.Approved)
        {
            state.Approve(ApproverId, Now);
        }

        return state;
    }

    private sealed record SnapshotWorld(World World, int OtherSheetDefId, long SnapshotId);
}
