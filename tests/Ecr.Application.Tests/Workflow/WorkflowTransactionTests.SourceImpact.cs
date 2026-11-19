// tests/Ecr.Application.Tests/Workflow/WorkflowTransactionTests.SourceImpact.cs
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// B80 N5 (D-554): де саме <c>ApproveSheetHandler</c> питає вплив на залежні — лише ОСТАТОЧНЕ затвердження оригіналу
/// (проміжний крок маршруту, відхилення — ні).
/// </summary>
/// <remarks>
/// Мутація: прибрати в <c>ApproveSheetHandler</c> умову <c>step is not { NextStepId: not null }</c> (питати на кожному
/// кроці) — червоніє «проміжний крок не питає»; прибрати саму перевірку — червоніє «остаточний крок питає».
/// </remarks>
public sealed partial class WorkflowTransactionTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B80")]
    public async Task B80_N5_проміжний_крок_маршруту_впливу_не_питає_а_остаточний_питає_і_відмова_лишає_Submitted()
    {
        var world = await ArrangeAsync(submitted: true).ConfigureAwait(true);
        var assessor = Impact(DocumentSourcePolicy.Block);

        // Проміжний крок (є наступний) — вплив не рахується, підпис проходить, аркуш лишається Submitted.
        await ApproveWithImpactAsync(world, Step(ordinal: 1, next: 2), assessor).ConfigureAwait(true);
        await assessor.DidNotReceiveWithAnyArgs().AssessAsync(default, default, default, default, default);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(world).ConfigureAwait(true));

        // Остаточний крок — питає; Block відмовляє, аркуш не змінено.
        var error = await Assert.ThrowsAsync<Ecr.Application.Errors.BusinessRuleException>(
            () => ApproveWithImpactAsync(world, Step(ordinal: 2, next: null), assessor)).ConfigureAwait(true);
        Assert.Equal("ECR-SRC-0422", error.ErrorCode);
        await assessor.Received(1).AssessAsync(
            world.DocumentId, world.SheetDefId, PeriodKeyValue, SourceApprovalChange.Approved, Arg.Any<CancellationToken>());
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(world).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B80")]
    public async Task B80_N5_відхилення_впливу_не_питає()
    {
        var world = await ArrangeAsync(submitted: true).ConfigureAwait(true);
        var assessor = Impact(DocumentSourcePolicy.Block);

        await using var db = CreateContext();
        await new ApproveSheetHandler(
                new WorkflowStore(db), AccessAt(Step(ordinal: 2, next: null)), new ReportSnapshotSync(NoSnapshots(), Documents(world)),
                new UnitOfWork(db), Approver(), new TestClock(Now), new AuditWriter(db), Documents(world),
                sourceImpact: assessor)
            .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, approved: false, "B80 N5: відхилення", CancellationToken.None)
            .ConfigureAwait(true);

        await assessor.DidNotReceiveWithAnyArgs().AssessAsync(default, default, default, default, default);
        Assert.Equal(DocumentStatus.Rejected, await StatusAsync(world).ConfigureAwait(true));
    }

    private static ISourceImpactAssessor Impact(DocumentSourcePolicy policy)
    {
        var assessor = Substitute.For<ISourceImpactAssessor>();
        assessor.AssessAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<SourceApprovalChange>(), Arg.Any<CancellationToken>())
            .Returns(call => new SourceImpact(
                call.Arg<SourceApprovalChange>(), call.ArgAt<long>(0), call.ArgAt<int>(1), call.ArgAt<int>(2), policy, [202601],
                [new SourceImpactConsumer(1, "B", "LAND", 5, "LAND-26", "DOC-LAND", 202601, Approved: true)], "hash-1"));
        return assessor;
    }

    private async Task ApproveWithImpactAsync(World world, ApprovalStepView step, ISourceImpactAssessor assessor)
    {
        await using var db = CreateContext();
        await new ApproveSheetHandler(
                new WorkflowStore(db), AccessAt(step), new ReportSnapshotSync(NoSnapshots(), Documents(world)),
                new UnitOfWork(db), Approver(), new TestClock(Now), new AuditWriter(db), Documents(world),
                sourceImpact: assessor)
            .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, approved: true, reason: null, CancellationToken.None)
            .ConfigureAwait(false);
    }
}
