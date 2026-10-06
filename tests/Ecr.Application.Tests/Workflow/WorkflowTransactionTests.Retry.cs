// tests/Ecr.Application.Tests/Workflow/WorkflowTransactionTests.Retry.cs
using Ecr.Application.Common;
using Ecr.Application.Reporting;
using Ecr.Application.Workflow;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// L6-15 (аудит 2026-10-03): подання й затвердження, чию першу спробу обірвав
/// транзієнтний збій (у застосунку — 1205, жертва дедлоку), повторюються успішно.
/// </summary>
/// <remarks>
/// ⛔ Що було. <c>UnitOfWork.ExecuteInTransactionAsync</c> повторював замикання над
/// «брудним» трекером змін. Запит другої спроби повертав уже змінений першою спробою
/// стан аркуша (подання → <c>wrongState</c>, бо аркуш «уже поданий»), а запис
/// <c>ApprovalEvent</c>, доданий першою спробою, лишався <c>Added</c> і вставлявся
/// вдруге. Затвердження ще й читало стан ДО транзакції.
///
/// ⚠ Збій — на ПЕРШОМУ <c>SaveChanges</c>, що несе новий <c>ApprovalEvent</c>; тестова
/// стратегія повторює лише його (той самий прийом, що <c>DataSourceSaveRetryTests</c>).
/// </remarks>
public sealed partial class WorkflowTransactionTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L6-15")]
    public async Task Подання_після_дедлоку_повторюється_успішно()
    {
        var world = await ArrangeAsync().ConfigureAwait(true);
        var fault = new FirstEventSaveFault();

        await using (var db = RetryingContext(fault))
        {
            await Submit(world, db, AccessAt(Step(ordinal: 1, next: 2)))
                .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, CancellationToken.None)
                .ConfigureAwait(true);
        }

        Assert.True(fault.Fired, "Збій першої спроби не спрацював — тест нічого не довів.");
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(world).ConfigureAwait(true));
        Assert.Equal(1, await SnapshotsAsync(world.DocumentId).ConfigureAwait(true));
        Assert.Equal([ApprovalAction.Submit], (await EventsAsync(world).ConfigureAwait(true)).Select(e => e.Action));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L6-15")]
    public async Task Затвердження_після_дедлоку_повторюється_успішно_і_пише_одну_подію()
    {
        var world = await ArrangeAsync().ConfigureAwait(true);
        await SubmitAsync(world).ConfigureAwait(true);
        var fault = new FirstEventSaveFault();

        await using (var db = RetryingContext(fault))
        {
            var user = Substitute.For<ICurrentUser>();
            user.UserId.Returns(ApproverId);
            var handler = new ApproveSheetHandler(
                new WorkflowStore(db), AccessAt(step: null), new ReportSnapshotSync(NoSnapshots(), Documents(world)),
                new UnitOfWork(db), user, new TestClock(Now), new AuditWriter(db), Documents(world));

            await handler
                .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, approved: true, reason: null, CancellationToken.None)
                .ConfigureAwait(true);
        }

        Assert.True(fault.Fired, "Збій першої спроби не спрацював — тест нічого не довів.");
        Assert.Equal(DocumentStatus.Approved, await StatusAsync(world).ConfigureAwait(true));
        Assert.Equal(
            [ApprovalAction.Submit, ApprovalAction.Approve],
            (await EventsAsync(world).ConfigureAwait(true)).Select(e => e.Action));
    }

    private EcrDbContext RetryingContext(FirstEventSaveFault fault)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o =>
            {
                o.CommandTimeout(30);
                o.ExecutionStrategy(d => new RetryOnTestFault(d));
            })
            .AddInterceptors(fault)
            .Options);

    /// <summary>Збій, який тестова стратегія вважає транзієнтним (замість 1205).</summary>
    private sealed class TransientTestFault() : Exception("Імітований транзієнтний збій (L6-15).");

    /// <summary>Стратегія, що повторює лише <see cref="TransientTestFault"/>.</summary>
    private sealed class RetryOnTestFault(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromMilliseconds(1))
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is TransientTestFault;
    }

    /// <summary>Кидає один раз — на першому збереженні, що несе новий <c>ApprovalEvent</c>.</summary>
    private sealed class FirstEventSaveFault : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && eventData.Context!.ChangeTracker.Entries<ApprovalEvent>().Any(e => e.State == EntityState.Added))
            {
                Fired = true;
                throw new TransientTestFault();
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
