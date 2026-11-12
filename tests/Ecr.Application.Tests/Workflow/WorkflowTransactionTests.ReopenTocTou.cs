// tests/Ecr.Application.Tests/Workflow/WorkflowTransactionTests.ReopenTocTou.cs
using Ecr.Application.Errors;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// C-8 (аудит 2026-11): Reopen вирішує роль кроку маршруту (P2-4/D-454) ще раз — у транзакції, ПІСЛЯ читання стану,
/// як Approve (R5-W1 / W1-05).
/// </summary>
public sealed partial class WorkflowTransactionTests
{
    /// <remarks>
    /// Імітація гонки: рішення до транзакції — «так» (крок N), рішення в транзакції — «ні» (інший погоджувач уже
    /// закомітив крок N → N+1). Мутація: прибрати повторне <c>CanReopenOfAsync</c> у транзакції
    /// <c>ReopenDocumentHandler</c> → аркуш повертається в Draft, червоне.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.20a")]
    public async Task Reopen_повторює_рішення_в_транзакції_і_відмовляє_якщо_крок_змінився()
    {
        var world = await ArrangeAsync(submitted: true).ConfigureAwait(true);

        var access = AccessAt(step: null);
        access.CanReopenAsync(
                  Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<PeriodKey>(),
                  Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow(), EditDecision.Deny(EditDenyReason.NoGrant));

        await using (var db = CreateContext())
        {
            var handler = new ReopenDocumentHandler(
                new WorkflowStore(db), access, new UnitOfWork(db), User(), new TestClock(Now), Documents(world),
                new ReportSnapshotSync(NoSnapshots(), Documents(world)));

            var error = await Assert.ThrowsAsync<AccessDeniedException>(
                    () => handler.HandleAsync(
                        world.DocumentId, world.SheetDefId, PeriodKeyValue, "гонка кроку", CancellationToken.None))
                .ConfigureAwait(true);
            Assert.Equal("err.ECR-ACCS-0403.reopenDenied", error.Details!["messageKey"]);
        }

        await access.Received(2).CanReopenAsync(
                Arg.Any<AccessProfile>(), world.DocumentId, world.SheetDefId, Arg.Any<PeriodKey>(),
                Arg.Any<CancellationToken>())
            .ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(world).ConfigureAwait(true));
        Assert.Empty(await EventsAsync(world).ConfigureAwait(true));
    }
}
