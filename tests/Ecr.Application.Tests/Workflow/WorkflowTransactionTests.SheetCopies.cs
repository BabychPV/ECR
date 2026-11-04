// tests/Ecr.Application.Tests/Workflow/WorkflowTransactionTests.SheetCopies.cs
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Application.Workflow;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// RS (копії аркушів, рішення людини 03.11): копія має ВЛАСНИЙ життєвий цикл за період — подання, затвердження, зріз і
/// журнал переходів копії не зачіпають оригіналу й навпаки.
/// </summary>
/// <remarks>
/// ⛔ Мутаційний доказ (локально): прибрати <c>&amp;&amp; s.SheetCopyId == sheetCopyId</c> з
/// <c>WorkflowStore.GetOrCreateCoreAsync</c> — червоніє <see cref="Затвердження_копії_не_зачіпає_поданого_оригіналу"/>
/// (рішення про копію змінює рядок стану оригіналу).
/// </remarks>
public sealed partial class WorkflowTransactionTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Подання_копії_лишає_оригінал_чернеткою_а_зріз_і_подія_належать_копії()
    {
        var world = await ArrangeAsync().ConfigureAwait(true);
        var copyId = await AddSheetCopyAsync(world).ConfigureAwait(true);

        await using var db = CreateContext();
        var access = CopyAccess(copyId, nextStepId: 2);
        await Submit(world, db, access, documents: CopyDocuments(world, copyId))
            .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, acknowledgeWarnings: false, copyId, CancellationToken.None)
            .ConfigureAwait(true);

        await using var check = CreateContext();
        var states = await check.ApprovalStates.AsNoTracking()
            .Where(s => s.DocumentId == world.DocumentId)
            .ToListAsync().ConfigureAwait(true);
        var copyState = Assert.Single(states);
        Assert.Equal((copyId, DocumentStatus.Submitted), (copyState.SheetCopyId!.Value, copyState.Status));

        var snapshot = Assert.Single(await check.SubmissionSnapshots.AsNoTracking()
            .Where(s => s.DocumentId == world.DocumentId).ToListAsync().ConfigureAwait(true));
        Assert.Equal(copyId, snapshot.SheetCopyId);

        var submitted = Assert.Single(await check.ApprovalEvents.AsNoTracking()
            .Where(e => e.DocumentId == world.DocumentId).ToListAsync().ConfigureAwait(true));
        Assert.Equal((ApprovalAction.Submit, (long?)copyId), (submitted.Action, submitted.SheetCopyId));

        // Шлях оригіналу копії не бачить: за ним — Draft (рядка немає), зрізу немає.
        Assert.Equal(DocumentStatus.Draft, await StatusAsync(world, sheetCopyId: null).ConfigureAwait(true));
        Assert.Null(await new Ecr.Infrastructure.Persistence.WorkflowStore(check)
            .GetLatestSnapshotKeyAsync(world.DocumentId, world.SheetDefId, new PeriodKey(PeriodKeyValue), CancellationToken.None)
            .ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Затвердження_копії_не_зачіпає_поданого_оригіналу()
    {
        // Оригінал поданий (ArrangeAsync), копія подана окремо.
        var world = await ArrangeAsync(submitted: true).ConfigureAwait(true);
        var copyId = await AddSheetCopyAsync(world).ConfigureAwait(true);
        await using (var db = CreateContext())
        {
            var copyState = new ApprovalState(world.DocumentId, world.SheetDefId, PeriodKeyValue, copyId);
            copyState.Submit(UserId, Now);
            db.ApprovalStates.Add(copyState);
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        await using (var db = CreateContext())
        {
            var snapshots = Substitute.For<IReportSnapshotBuilder>();
            snapshots.ListAsync(
                         Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<IReadOnlyCollection<int>?>(),
                         Arg.Any<CancellationToken>())
                     .Returns<IReadOnlyList<ReportSnapshotSummary>>([]);
            var documents = CopyDocuments(world, copyId);

            // Копія — на останньому кроці маршруту: затвердження остаточне.
            await new ApproveSheetHandler(
                    new Ecr.Infrastructure.Persistence.WorkflowStore(db), CopyAccess(copyId, nextStepId: null),
                    new ReportSnapshotSync(snapshots, documents), new Ecr.Infrastructure.Persistence.UnitOfWork(db),
                    Approver(), new TestClock(Now), new Ecr.Infrastructure.Persistence.AuditWriter(db), documents)
                .HandleAsync(
                    world.DocumentId, world.SheetDefId, PeriodKeyValue, approved: true, reason: null, copyId,
                    CancellationToken.None)
                .ConfigureAwait(true);
        }

        Assert.Equal(DocumentStatus.Approved, await StatusAsync(world, copyId).ConfigureAwait(true));
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(world, sheetCopyId: null).ConfigureAwait(true));

        await using var check = CreateContext();
        var approve = Assert.Single(await check.ApprovalEvents.AsNoTracking()
            .Where(e => e.DocumentId == world.DocumentId && e.Action == ApprovalAction.Approve)
            .ToListAsync().ConfigureAwait(true));
        Assert.Equal(copyId, approve.SheetCopyId);
    }

    /// <summary>Оригінал аркуша світу в складі документа і його копія №2; повертає Id копії.</summary>
    private async Task<long> AddSheetCopyAsync(World world)
    {
        await using var db = CreateContext();
        var original = new DocumentSheet(world.DocumentId, world.SheetDefId);
        db.DocumentSheets.Add(original);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var copy = original.CreateCopy(2);
        db.DocumentSheets.Add(copy);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return copy.Id;
    }

    private async Task<DocumentStatus> StatusAsync(World world, long? sheetCopyId)
    {
        await using var db = CreateContext();
        var state = await db.ApprovalStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.DocumentId == world.DocumentId
                                      && s.SheetDefId == world.SheetDefId
                                      && s.PeriodKey == PeriodKeyValue
                                      && s.SheetCopyId == sheetCopyId)
            .ConfigureAwait(false);

        return state?.Status ?? DocumentStatus.Draft;
    }

    /// <summary>Склад документа знає копію (решта — як у <see cref="Submit"/>).</summary>
    private static Ecr.Application.Ports.IDocumentStore CopyDocuments(World world, long copyId)
    {
        var documents = Substitute.For<Ecr.Application.Ports.IDocumentStore>();
        documents.HasSheetAsync(world.DocumentId, world.SheetDefId, Arg.Any<CancellationToken>()).Returns(true);
        documents.HasSheetCopyAsync(world.DocumentId, world.SheetDefId, copyId, Arg.Any<CancellationToken>()).Returns(true);
        documents.FindProjectIdAsync(world.DocumentId, Arg.Any<CancellationToken>()).Returns(world.ProjectId);
        return documents;
    }

    /// <summary><see cref="Access"/> плюс рішення для копії; крок маршруту копії — з <paramref name="nextStepId"/>.</summary>
    private static IAccessDecisionService CopyAccess(long copyId, int? nextStepId)
    {
        var access = Access();
        access.CanSubmitCopyAsync(
                  Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<PeriodKey>(), copyId,
                  Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.CanApproveCopyAsync(
                  Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<PeriodKey>(), copyId,
                  Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.CurrentCopyApprovalStepAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<PeriodKey>(), copyId, Arg.Any<CancellationToken>())
              .Returns(new ApprovalStepView(StepId: 1, Ordinal: 1, RoleId: RoleId, NextStepId: nextStepId, TotalSteps: 2));
        return access;
    }
}
