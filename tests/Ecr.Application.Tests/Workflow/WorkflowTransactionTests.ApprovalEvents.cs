// tests/Ecr.Application.Tests/Workflow/WorkflowTransactionTests.ApprovalEvents.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// <c>BE-11</c>: журнал переходів <c>wf.ApprovalEvent</c> — на справжній базі,
/// справжніми обробниками і тим самим стендом, що й тести меж коміту.
/// </summary>
public sealed partial class WorkflowTransactionTests
{
    private const string RejectReason = "не сходиться підсумок за січень";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Кожен_перехід_маршруту_лишає_рівно_одну_подію()
    {
        var world = await ArrangeAsync().ConfigureAwait(true);

        await SubmitAsync(world).ConfigureAwait(true);
        await ApproveAsync(world, Step(ordinal: 1, next: 2), approved: true, reason: null).ConfigureAwait(true);
        await ApproveAsync(world, Step(ordinal: 2, next: null), approved: true, reason: null).ConfigureAwait(true);
        await ReopenAsync(world, "уточнення за скаргою").ConfigureAwait(true);

        var events = await EventsAsync(world).ConfigureAwait(true);

        (DocumentStatus From, DocumentStatus To, ApprovalAction Action, string? Reason, int? Step)[] expected =
        [
            (DocumentStatus.Draft, DocumentStatus.Submitted, ApprovalAction.Submit, null, null),
            (DocumentStatus.Submitted, DocumentStatus.Submitted, ApprovalAction.ApproveStep, null, 1),
            (DocumentStatus.Submitted, DocumentStatus.Approved, ApprovalAction.Approve, null, 2),
            (DocumentStatus.Approved, DocumentStatus.Draft, ApprovalAction.Reopen, "уточнення за скаргою", null),
        ];

        Assert.Equal(expected, events.Select(e => (e.FromStatus, e.ToStatus, e.Action, e.Reason, e.StepOrdinal)));

        Assert.All(events, e =>
        {
            Assert.Equal(Now, e.At);
            Assert.Equal(world.SheetDefId, e.SheetDefId);
        });

        // ⚠ F-25: `Submit`/`Reopen` — автор (`UserId`); `ApproveStep`/`Approve` —
        // ІНШИЙ погоджувач (`ApproverId`), бо той самий користувач більше не
        // може подати й погодити власний аркуш.
        Assert.All(
            events.Where(e => e.Action is ApprovalAction.Submit or ApprovalAction.Reopen),
            e => Assert.Equal(UserId, e.ByUserId));
        Assert.All(
            events.Where(e => e.Action is ApprovalAction.ApproveStep or ApprovalAction.Approve),
            e => Assert.Equal(ApproverId, e.ByUserId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Причина_відхилення_переживає_повторне_подання()
    {
        // ⛔ Саме те, що губив `wf.ApprovalState`: `Submit` обнуляє
        // `RejectedReason`, і після повторного подання причини не було ніде.
        var world = await ArrangeAsync().ConfigureAwait(true);

        await SubmitAsync(world).ConfigureAwait(true);
        // ⚠ F-25 не забороняє ВІДХИЛЕННЯ власного подання (лише затвердження),
        // тож тут навмисно лишається той самий `UserId`, що й у `SubmitAsync`
        // — сценарій, який тест і перевіряє (`rejected.ByUserId` нижче).
        await ApproveAsync(world, step: null, approved: false, RejectReason, userId: UserId).ConfigureAwait(true);
        await SubmitAsync(world).ConfigureAwait(true);

        // Контроль передумови: на рядку стану причини вже справді немає.
        await using (var db = CreateContext())
        {
            var state = await db.ApprovalStates.AsNoTracking()
                                .SingleAsync(s => s.DocumentId == world.DocumentId).ConfigureAwait(true);
            Assert.Null(state.RejectedReason);
        }

        var events = await EventsAsync(world).ConfigureAwait(true);

        Assert.Equal(
            [ApprovalAction.Submit, ApprovalAction.Reject, ApprovalAction.Submit],
            events.Select(e => e.Action));

        var rejected = events[1];
        Assert.Equal(RejectReason, rejected.Reason);
        Assert.Equal((DocumentStatus.Submitted, DocumentStatus.Rejected), (rejected.FromStatus, rejected.ToStatus));
        Assert.Equal(UserId, rejected.ByUserId);

        // Повторне подання бере `From` зі стану ДО зміни, а не константу.
        Assert.Equal(DocumentStatus.Rejected, events[2].FromStatus);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збій_збереження_стану_не_лишає_події()
    {
        // ⚠ Збій — справжній конфлікт `RowVersion`: між читанням стану і
        // `SaveChangesAsync` рядок змінює інше підключення. Вікно відкриває
        // `IReportSnapshotBuilder.ListAsync` — останній виклик перед збереженням.
        var world = await ArrangeAsync(submitted: true).ConfigureAwait(true);

        var snapshots = Substitute.For<IReportSnapshotBuilder>();
        snapshots.ListAsync(
                     Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<IReadOnlyCollection<int>?>(),
                     Arg.Any<CancellationToken>())
                 .Returns<IReadOnlyList<ReportSnapshotSummary>>(_ =>
                 {
                     TouchState(world.DocumentId);
                     return [];
                 });

        await using var db = CreateContext();
        // ⚠ F-25: `Approver()` — `world` подано від `UserId` (`ArrangeAsync`),
        // і той самий користувач більше не може себе ж і погодити.
        var handler = new ApproveSheetHandler(
            new WorkflowStore(db), AccessAt(step: null), new ReportSnapshotSync(snapshots, Documents(world)),
            new UnitOfWork(db), Approver(), new TestClock(Now), new AuditWriter(db));

        await Assert.ThrowsAsync<Ecr.Application.Errors.ConcurrencyConflictException>(
            () => handler.HandleAsync(
                world.DocumentId, world.SheetDefId, PeriodKeyValue, approved: true, reason: null,
                CancellationToken.None))
            .ConfigureAwait(true);

        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(world).ConfigureAwait(true));
        Assert.Empty(await EventsAsync(world).ConfigureAwait(true));
    }

    // ────────────────────────────── дії ───────────────────────────────

    private async Task SubmitAsync(World world)
    {
        await using var db = CreateContext();
        await Submit(world, db, AccessAt(Step(ordinal: 1, next: 2)))
            .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task ApproveAsync(
        World world, ApprovalStepView? step, bool approved, string? reason, int userId = ApproverId)
    {
        await using var db = CreateContext();
        // ⚠ F-25: за замовчуванням `ApproverId`, а не `UserId` — той самий
        // користувач не може погодити власне подання (`SubmitAsync` подає від
        // `UserId`). Виклик на ВІДХИЛЕННЯ (`Причина_відхилення_переживає_…`)
        // підставляє `UserId` сам: відмова стосується лише затвердження.
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(userId);
        var handler = new ApproveSheetHandler(
            new WorkflowStore(db), AccessAt(step), new ReportSnapshotSync(NoSnapshots(), Documents(world)),
            new UnitOfWork(db), user, new TestClock(Now), new AuditWriter(db));

        await handler
            .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, approved, reason, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task ReopenAsync(World world, string reason)
    {
        await using var db = CreateContext();
        var handler = new ReopenDocumentHandler(
            new WorkflowStore(db), AccessAt(step: null), new UnitOfWork(db), User(), new TestClock(Now));

        await handler
            .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, reason, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task<List<ApprovalEvent>> EventsAsync(World world)
    {
        await using var db = CreateContext();
        return await db.ApprovalEvents.AsNoTracking()
                       .Where(e => e.DocumentId == world.DocumentId && e.PeriodKey == PeriodKeyValue)
                       .OrderBy(e => e.Id)
                       .ToListAsync().ConfigureAwait(false);
    }

    /// <summary>Зсуває <c>RowVersion</c> стану стороннім підключенням.</summary>
    private void TouchState(long documentId)
    {
        using var connection = new SqlConnection(sql.ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE wf.ApprovalState SET SubmittedAt = SubmittedAt WHERE DocumentId = @id;";
        command.Parameters.AddWithValue("@id", documentId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    // ────────────────────────────── збірка ────────────────────────────

    private static ApprovalStepView Step(int ordinal, int? next)
        => new(StepId: ordinal, Ordinal: ordinal, RoleId: RoleId, NextStepId: next, TotalSteps: 2);

    /// <summary>Дозволяє все; поточний крок маршруту — заданий (<c>null</c> — маршруту немає).</summary>
    private static IAccessDecisionService AccessAt(ApprovalStepView? step)
    {
        var access = Access();

        // ⚠ Будь-який `userId`: `SubmitAsync`/`ApproveAsync`/`ReopenAsync` цього
        // файлу відтепер кличуть із РІЗНИМИ користувачами (`UserId`,
        // `ApproverId` — F-25), і профіль мусить будуватися для того, хто
        // насправді викликає.
        access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(call => new AccessBuilder { UserId = call.Arg<int>() }
                  .Permission(ReopenDocumentHandler.Permission).Build());
        access.CanReopenAsync(
                  Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<PeriodKey>(),
                  Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.CurrentApprovalStepAsync(
                  Arg.Any<long>(), Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
              .Returns(step);

        return access;
    }

    private static IDocumentStore Documents(World world)
    {
        var documents = Substitute.For<IDocumentStore>();
        documents.FindProjectIdAsync(world.DocumentId, Arg.Any<CancellationToken>()).Returns(world.ProjectId);
        return documents;
    }

    private static IReportSnapshotBuilder NoSnapshots()
    {
        var snapshots = Substitute.For<IReportSnapshotBuilder>();
        snapshots.ListAsync(
                     Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<IReadOnlyCollection<int>?>(),
                     Arg.Any<CancellationToken>())
                 .Returns<IReadOnlyList<ReportSnapshotSummary>>([]);
        return snapshots;
    }
}
