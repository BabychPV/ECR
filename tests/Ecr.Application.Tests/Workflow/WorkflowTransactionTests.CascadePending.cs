// tests/Ecr.Application.Tests/Workflow/WorkflowTransactionTests.CascadePending.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// F5 (рішення людини «Заборонити, поки йде перерахунок»): подання й затвердження (на будь-якому кроці маршруту)
/// місяця, що чекає каскаду від зміни джерела, відмовляють 422 <c>ECR-SUB-4221</c> /
/// <see cref="SourceCascadePendingCheck.MessageKey"/>; ворота мовчать — дія як доти; відхилення воріт не питає.
/// </summary>
/// <remarks>
/// Коли саме місяць «чекає» (стан задачі, план ланцюжка) — <c>SourceCascadePendingGateTests</c> (Infrastructure); наскрізно
/// через HTTP з відкликанням і поверненням у роботу — <c>SubmitApproveCascadePendingHttpTests</c> (Api).
/// </remarks>
public sealed partial class WorkflowTransactionTests
{
    /// <summary>Мутація: прибрати виклик <c>SourceCascadePendingCheck</c> із <c>SubmitSheetHandler</c> — червоний.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "F5")]
    public async Task F5_подання_місяця_що_чекає_каскаду_джерела_відмовляє_з_ключем()
    {
        var world = await ArrangeAsync().ConfigureAwait(true);
        var gate = CascadeGate(pending: true);

        await using var db = CreateContext();
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
                () => Submit(world, db, Access(), cascadeGate: gate)
                    .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, CancellationToken.None))
            .ConfigureAwait(true);

        AssertCascadeRefusal(error);
        await gate.Received(1).GetPendingReasonAsync(world.DocumentId, PeriodKeyValue, Arg.Any<CancellationToken>());
        Assert.NotEqual(DocumentStatus.Submitted, await StatusAsync(world).ConfigureAwait(true));
        Assert.Equal(0, await SnapshotsAsync(world.DocumentId).ConfigureAwait(true));
    }

    /// <summary>Контроль: каскаду немає — подання як доти.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "F5")]
    public async Task F5_подання_без_каскаду_проходить()
    {
        var world = await ArrangeAsync().ConfigureAwait(true);

        await using var db = CreateContext();
        await Submit(world, db, Access(), cascadeGate: CascadeGate(pending: false))
            .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, CancellationToken.None)
            .ConfigureAwait(true);

        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(world).ConfigureAwait(true));
    }

    /// <summary>Проміжний і остаточний крок маршруту відмовляють; відхилення воріт не питає й проходить.</summary>
    /// <remarks>Мутація: прибрати виклик <c>SourceCascadePendingCheck</c> з <c>ApproveSheetHandler</c> — червоний.</remarks>
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, null)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "F5")]
    public async Task F5_затвердження_на_будь_якому_кроці_відмовляє_а_відхилення_проходить(int ordinal, int? next)
    {
        var world = await ArrangeAsync(submitted: true).ConfigureAwait(true);
        var gate = CascadeGate(pending: true);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
                () => ApproveWithCascadeGateAsync(world, Step(ordinal, next), gate, approved: true, reason: null))
            .ConfigureAwait(true);

        AssertCascadeRefusal(error);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(world).ConfigureAwait(true));

        gate.ClearReceivedCalls();
        await ApproveWithCascadeGateAsync(world, Step(ordinal, next), gate, approved: false, reason: "F5: повернути")
            .ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Rejected, await StatusAsync(world).ConfigureAwait(true));
        await gate.DidNotReceiveWithAnyArgs().GetPendingReasonAsync(default, default, default);
    }

    /// <summary>Контроль: каскаду немає — остаточне затвердження як доти.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "F5")]
    public async Task F5_затвердження_без_каскаду_проходить()
    {
        var world = await ArrangeAsync(submitted: true).ConfigureAwait(true);

        await ApproveWithCascadeGateAsync(world, Step(ordinal: 2, next: null), CascadeGate(pending: false), approved: true, reason: null)
            .ConfigureAwait(true);

        Assert.Equal(DocumentStatus.Approved, await StatusAsync(world).ConfigureAwait(true));
    }

    /// <summary>
    /// ✎ b11 (огляд координатора 24.11): тримає ВЛАСНА жива задача перерахунку документа — той самий 422 <c>ECR-SUB-4221</c>,
    /// але ключ <c>ownRecalculationPending</c>, а не текст D-567 про зміну джерела; на затвердженні — так само.
    /// Мутація: у <c>SourceCascadePendingCheck</c> завжди <c>MessageKey</c> — червоний.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "F5")]
    public async Task F5_власний_перерахунок_документа_відмовляє_зі_своїм_ключем()
    {
        var world = await ArrangeAsync().ConfigureAwait(true);
        var gate = CascadeGate(CascadePendingReason.OwnRecalculation);

        await using var db = CreateContext();
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
                () => Submit(world, db, Access(), cascadeGate: gate)
                    .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, CancellationToken.None))
            .ConfigureAwait(true);

        AssertCascadeRefusal(error, "err.ECR-SUB-4221.ownRecalculationPending");
        Assert.NotEqual(DocumentStatus.Submitted, await StatusAsync(world).ConfigureAwait(true));
    }

    private static ISourceCascadePendingGate CascadeGate(bool pending)
        => CascadeGate(pending ? CascadePendingReason.SourceCascade : CascadePendingReason.None);

    private static ISourceCascadePendingGate CascadeGate(CascadePendingReason reason)
    {
        var gate = Substitute.For<ISourceCascadePendingGate>();
        gate.GetPendingReasonAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(reason);
        gate.IsPendingAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(reason != CascadePendingReason.None);
        return gate;
    }

    private static void AssertCascadeRefusal(BusinessRuleException error, string messageKey = "err.ECR-SUB-4221.sourceCascadePending")
    {
        Assert.Equal(ErrorCodes.SubmitBlocked, error.ErrorCode);
        var details = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(error.Details);
        Assert.Equal(messageKey, details["messageKey"]);
        Assert.Equal(PeriodKeyValue.ToString(System.Globalization.CultureInfo.InvariantCulture), details["periodKey"]);
    }

    private async Task ApproveWithCascadeGateAsync(
        World world, ApprovalStepView step, ISourceCascadePendingGate gate, bool approved, string? reason)
    {
        await using var db = CreateContext();
        await new ApproveSheetHandler(
                new WorkflowStore(db), AccessAt(step), new UnitOfWork(db), Approver(), new TestClock(Now), new AuditWriter(db), Documents(world),
                cascadeGate: gate)
            .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, approved, reason, CancellationToken.None)
            .ConfigureAwait(false);
    }
}
