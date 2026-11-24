// tests/Ecr.Application.Tests/Workflow/WorkflowTransactionTests.SourceTrigger.cs
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// B80 (N2a, T-A/T-B): обробники кличуть тригер джерела лише на ОСТАТОЧНЕ затвердження і на повернення ІЗ
/// ЗАТВЕРДЖЕНОГО — і лише після коміту (на контексті обробника транзакції вже немає).
/// </summary>
/// <remarks>
/// Що саме ставить тригер (стан зв'язки, біти, рік, місяць) — <c>SourceApprovalChangeTriggerTests</c> (Infrastructure).
/// </remarks>
public sealed partial class WorkflowTransactionTests
{
    /// <summary>Мутація: <c>state.Status == DocumentStatus.Approved</c> → <c>true</c> — проміжний крок кличе тригер, червоний.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B80")]
    public async Task B80_проміжний_крок_не_кличе_тригер_джерела_а_остаточний_кличе_після_коміту()
    {
        var world = await ArrangeAsync(submitted: true).ConfigureAwait(true);
        var trigger = Substitute.For<ISourceApprovalChangeTrigger>();

        await ApproveWithTriggerAsync(world, Step(ordinal: 1, next: 2), trigger, committed: null).ConfigureAwait(true);
        await trigger.DidNotReceiveWithAnyArgs()
            .OnSourceSheetApprovalChangedAsync(default, default, default, default, default, default);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(world).ConfigureAwait(true));

        var committed = new List<bool>();
        await ApproveWithTriggerAsync(world, Step(ordinal: 2, next: null), trigger, committed).ConfigureAwait(true);

        Assert.Equal(DocumentStatus.Approved, await StatusAsync(world).ConfigureAwait(true));
        await trigger.Received(1).OnSourceSheetApprovalChangedAsync(
            world.DocumentId, world.SheetDefId, PeriodKeyValue, SourceApprovalChange.Approved, ApproverId,
            Arg.Any<CancellationToken>());

        // ⛔ Мутація: виклик тригера всередину транзакції — на контексті обробника транзакція ще відкрита, червоний.
        Assert.Equal([true], committed);
    }

    /// <summary>Мутація: <c>fromStatus == DocumentStatus.Approved</c> → <c>true</c> — повернення поданого кличе тригер, червоний.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B80")]
    public async Task B80_повернення_поданого_не_кличе_тригер_а_повернення_затвердженого_кличе()
    {
        var world = await ArrangeAsync(submitted: true).ConfigureAwait(true);
        var trigger = Substitute.For<ISourceApprovalChangeTrigger>();

        // Поданий → Draft: джерело для залежних не змінилося.
        await ReopenWithTriggerAsync(world, trigger).ConfigureAwait(true);
        await trigger.DidNotReceiveWithAnyArgs()
            .OnSourceSheetApprovalChangedAsync(default, default, default, default, default, default);

        // Знову подано й остаточно затверджено (без тригера) → повернення із затвердженого.
        await SubmitAsync(world).ConfigureAwait(true);
        await ApproveAsync(world, Step(ordinal: 2, next: null), approved: true, reason: null).ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Approved, await StatusAsync(world).ConfigureAwait(true));

        await ReopenWithTriggerAsync(world, trigger).ConfigureAwait(true);

        Assert.Equal(DocumentStatus.Draft, await StatusAsync(world).ConfigureAwait(true));
        await trigger.Received(1).OnSourceSheetApprovalChangedAsync(
            world.DocumentId, world.SheetDefId, PeriodKeyValue, SourceApprovalChange.Reopened, UserId,
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// B179 L2 (знахідка аудитора): остаточне затвердження і повернення із затвердженого КОПІЇ аркуша джерела тригер не
    /// кличуть — джерелом для залежних є лише оригінал (завантажувач складу читає <c>SheetCopyId = null</c>).
    /// </summary>
    /// <remarks>
    /// Мутації: у <c>ApproveSheetHandler</c> <c>approved &amp;&amp; sheetCopyId is null &amp;&amp; …</c> → без
    /// <c>sheetCopyId is null</c> — червоний перший виклик; у <c>ReopenDocumentHandler</c> <c>reopenedFromApproved =
    /// sheetCopyId is null &amp;&amp; …</c> → без умови — червоний другий.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B179")]
    public async Task B179_затвердження_й_повернення_копії_аркуша_джерела_не_кличуть_тригер()
    {
        var world = await ArrangeAsync().ConfigureAwait(true);
        var copyId = await AddSheetCopyAsync(world).ConfigureAwait(true);
        await using (var db = CreateContext())
        {
            var copyState = new Ecr.Domain.Entities.Workflow.ApprovalState(world.DocumentId, world.SheetDefId, PeriodKeyValue, copyId);
            copyState.Submit(UserId, Now);
            db.ApprovalStates.Add(copyState);
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var trigger = Substitute.For<ISourceApprovalChangeTrigger>();
        var documents = CopyDocuments(world, copyId);
        var access = CopyAccess(copyId, nextStepId: null);
        var reopenAccess = AccessAt(step: null);
        reopenAccess.CanReopenCopyAsync(
                  Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<Ecr.Domain.ValueObjects.PeriodKey>(), copyId,
                  Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());

        // Копія — на останньому кроці: затвердження остаточне.
        await using (var db = CreateContext())
        {
            await new ApproveSheetHandler(
                    new WorkflowStore(db), access, new UnitOfWork(db), Approver(), new TestClock(Now), new AuditWriter(db), documents,
                    sourceTrigger: trigger)
                .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, approved: true, reason: null, copyId, CancellationToken.None)
                .ConfigureAwait(true);
        }

        Assert.Equal(DocumentStatus.Approved, await StatusAsync(world, copyId).ConfigureAwait(true));
        await trigger.DidNotReceiveWithAnyArgs()
            .OnSourceSheetApprovalChangedAsync(default, default, default, default, default, default);

        // Повернення копії із затвердженого — так само без тригера.
        await using (var db = CreateContext())
        {
            await new ReopenDocumentHandler(
                    new WorkflowStore(db), reopenAccess, new UnitOfWork(db), User(), new TestClock(Now), documents,
                    sourceTrigger: trigger)
                .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, "уточнення копії", copyId, CancellationToken.None)
                .ConfigureAwait(true);
        }

        Assert.Equal(DocumentStatus.Draft, await StatusAsync(world, copyId).ConfigureAwait(true));
        await trigger.DidNotReceiveWithAnyArgs()
            .OnSourceSheetApprovalChangedAsync(default, default, default, default, default, default);
    }

    private async Task ApproveWithTriggerAsync(
        World world, ApprovalStepView step, ISourceApprovalChangeTrigger trigger, List<bool>? committed)
    {
        await using var db = CreateContext();
        trigger.ClearReceivedCalls();
        trigger.OnSourceSheetApprovalChangedAsync(
                Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<SourceApprovalChange>(), Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                committed?.Add(db.Database.CurrentTransaction is null);
                return Task.CompletedTask;
            });

        await new ApproveSheetHandler(
                new WorkflowStore(db), AccessAt(step), new UnitOfWork(db), Approver(), new TestClock(Now), new AuditWriter(db), Documents(world),
                sourceTrigger: trigger)
            .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, approved: true, reason: null, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task ReopenWithTriggerAsync(World world, ISourceApprovalChangeTrigger trigger)
    {
        await using var db = CreateContext();
        await new ReopenDocumentHandler(
                new WorkflowStore(db), AccessAt(step: null), new UnitOfWork(db), User(), new TestClock(Now), Documents(world),
                sourceTrigger: trigger)
            .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, "уточнення джерела", CancellationToken.None)
            .ConfigureAwait(false);
    }
}
