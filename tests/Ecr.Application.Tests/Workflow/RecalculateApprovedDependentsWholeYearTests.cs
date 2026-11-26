// tests/Ecr.Application.Tests/Workflow/RecalculateApprovedDependentsWholeYearTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// ✎ D-581 (огляд H1): явне «перерахувати й затверджені» — УВЕСЬ рік, навіть коли вплив, який бачила людина, звужено до
/// місяця першої правки: після неї людина могла правити й березень, а затверджений лютий, що читає січень ([Period:-1]),
/// теж мусить отримати нові числа. Відбиток — той, що з місяцями (людина бачила саме його).
/// </summary>
/// <remarks>Мутація: обробник передає тригеру <c>impact.CascadeMonths</c> — червоний.</remarks>
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait("Decision", "D-581")]
public sealed class RecalculateApprovedDependentsWholeYearTests
{
    private const long DocumentId = 98;
    private const int SheetDefId = 4;
    private const int PeriodKey = 202601;

    [Fact]
    public async Task Перекриття_затверджених_ставить_увесь_рік_а_не_місяці_першої_правки()
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission(SourceImpactCheck.RecalculateApprovedPermission).Build());
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>()).Returns(EditDecision.Allow());
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);

        var assessor = Substitute.For<ISourceImpactAssessor>();
        IReadOnlyCollection<int>? assessedMonths = null;
        assessor.AssessAsync(
                DocumentId, SheetDefId, PeriodKey, SourceApprovalChange.Edited,
                Arg.Do<IReadOnlyCollection<int>?>(m => assessedMonths = m), Arg.Any<CancellationToken>())
            .Returns(new SourceImpact(
                SourceApprovalChange.Edited, DocumentId, SheetDefId, PeriodKey, DocumentSourcePolicy.Warn, [202601],
                [new SourceImpactConsumer(1, "B", "LAND", 5, "LAND-26", "DOC-LAND", 202602, Approved: true, Sheets: [])], "h-jan")
            {
                CascadeMonths = [1],
            });
        var trigger = Substitute.For<ISourceApprovalChangeTrigger>();
        trigger.RequestApprovedDependentsRecalculationAsync(
                Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<int>?>(),
                Arg.Any<ApprovedRecalculationOverride>(), Arg.Any<CancellationToken>())
            .Returns(1);

        var result = await new RecalculateApprovedDependentsHandler(
                access, user, Substitute.For<IDocumentStore>(), assessor, trigger, Substitute.For<IAuditWriter>(), new TestClock(DateTime.UtcNow))
            .HandleAsync(DocumentId, SheetDefId, PeriodKey, "h-jan", "акт", CancellationToken.None, [1]);

        Assert.Equal(1, result.Queued);
        Assert.Equal([1], assessedMonths);
        await trigger.Received(1).RequestApprovedDependentsRecalculationAsync(
            DocumentId, SheetDefId, PeriodKey, null, Arg.Is<ApprovedRecalculationOverride>(o => o.UserId == 9 && o.Reason == "акт"),
            Arg.Any<CancellationToken>());
    }
}
