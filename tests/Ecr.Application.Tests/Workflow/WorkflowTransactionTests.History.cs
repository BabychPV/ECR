// tests/Ecr.Application.Tests/Workflow/WorkflowTransactionTests.History.cs
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// <c>BE-11b</c>: <c>WorkflowStore.GetHistoryAsync</c> на справжній базі — фільтр,
/// порядок, стеля і приєднані код аркуша та ім'я виконавця.
/// </summary>
public sealed partial class WorkflowTransactionTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Історія_лише_цього_документа_і_періоду_найновіші_перші_до_стелі()
    {
        var world = await ArrangeAsync().ConfigureAwait(true);
        var other = await ArrangeAsync().ConfigureAwait(true);

        string sheetCode;
        int actorId;

        await using (var db = CreateContext())
        {
            var actor = new User($"hist_{Guid.NewGuid():N}"[..20], "Olena Koval", AuthProvider.Local);
            actor.SetPassword("not-a-real-hash"); // CK_User_Provider: локальний користувач без хеша не вставиться
            db.Users.Add(actor);
            await db.SaveChangesAsync().ConfigureAwait(true);
            actorId = actor.Id;

            sheetCode = (await db.SheetDefs.FindAsync(world.SheetDefId).ConfigureAwait(true))!.Code;

            db.ApprovalEvents.AddRange(
                Entry(world, PeriodKeyValue, ApprovalAction.Submit, actorId, minutes: 1),
                Entry(world, PeriodKeyValue, ApprovalAction.Reject, actorId, minutes: 2, reason: RejectReason),
                Entry(world, PeriodKeyValue, ApprovalAction.Reopen, byUserId: null, minutes: 3),

                // ⚠ Обидва «чужі» записи — НАЙНОВІШІ: без фільтра вони стали б
                // першими у відповіді, а не сховалися б за стелею.
                Entry(world, 202602, ApprovalAction.Submit, actorId, minutes: 50),
                Entry(other, PeriodKeyValue, ApprovalAction.Submit, actorId, minutes: 60));

            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        await using var read = CreateContext();
        var store = new WorkflowStore(read);
        var key = new PeriodKey(PeriodKeyValue);

        var history = await store.GetHistoryAsync(world.DocumentId, key, 10, CancellationToken.None)
                                 .ConfigureAwait(true);

        Assert.Equal(
            [ApprovalAction.Reopen, ApprovalAction.Reject, ApprovalAction.Submit],
            history.Select(e => e.Action));

        Assert.All(history, e => Assert.Equal(sheetCode, e.SheetCode));

        // Системний перехід не випав із журналу (ліве з'єднання) і лишився без імені.
        Assert.Equal((null, null), (history[0].ByUserId, history[0].ByDisplayName));
        Assert.Equal((actorId, "Olena Koval", RejectReason),
            (history[1].ByUserId, history[1].ByDisplayName, history[1].Reason));

        var capped = await store.GetHistoryAsync(world.DocumentId, key, 2, CancellationToken.None)
                                .ConfigureAwait(true);

        Assert.Equal([ApprovalAction.Reopen, ApprovalAction.Reject], capped.Select(e => e.Action));
    }

    private static ApprovalEvent Entry(
        World world, int periodKey, ApprovalAction action, int? byUserId, int minutes, string? reason = null)
        => new(
            world.DocumentId, world.SheetDefId, periodKey,
            DocumentStatus.Draft, DocumentStatus.Submitted, action, byUserId, Now.AddMinutes(minutes), reason);
}
