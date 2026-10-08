using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// RC14 (приймальна №10, P2-3): глухий кут «один аркуш затверджено, на іншому — чернетка». Перерахунок
/// ОБЛАСТІ (<c>sheetDefId</c>) чернеткового аркуша не відхиляється через ПОДАНИЙ СУСІДНІЙ аркуш; поданий
/// аркуш області, закритий період і схований аркуш — відхиляються, як і раніше.
/// </summary>
/// <remarks>
/// Мутація (локально): у <c>RecalculateDocumentHandler</c> прибрати фільтр за <c>sheetDefId</c> перед
/// <c>RecalculationWritePolicy.Check</c> — червоніє перший тест.
/// </remarks>
public sealed class RecalculateDocumentSheetScopeTests
{
    private const long Document = 900;
    private const int Project = 1;
    private const int Period = 202610;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Область_чернеткового_аркуша_не_відхиляється_через_поданий_сусідній()
    {
        var fixture = Arrange(submittedSheet: 3, hiddenSheet: null);

        var jobId = await fixture.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId: 1, CancellationToken.None);

        Assert.Equal("job-1", jobId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Область_самого_поданого_аркуша_відхиляється()
    {
        var fixture = Arrange(submittedSheet: 3, hiddenSheet: null);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => fixture.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId: 3, CancellationToken.None));

        Assert.Equal("ECR-CALC-4221", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-4221.sheetsSubmitted", error.Details?["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Перерахунок_усього_документа_з_одним_поданим_аркушем_ставиться_в_чергу_а_не_відхиляється()
    {
        // RC15 (P2-A): подані/затверджені аркуші задача ПРОПУСКАЄ, а не відмовляє цілому запиту; відмова
        // «усі аркуші подані» (немає що рахувати) — у задачі, яка знає склад аркушів періоду.
        var fixture = Arrange(submittedSheet: 3, hiddenSheet: null);

        var jobId = await fixture.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId: null, CancellationToken.None);

        Assert.Equal("job-1", jobId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.7")]
    public async Task Перерахунок_усього_документа_у_закритому_періоді_відхиляється_як_раніше()
    {
        var fixture = Arrange(submittedSheet: 3, hiddenSheet: null, periodState: PeriodState.Closed);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => fixture.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId: null, CancellationToken.None));

        Assert.Equal("err.ECR-CALC-4221.periodClosed", error.Details?["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.7")]
    public async Task Область_у_закритому_періоді_відхиляється_навіть_без_поданих_аркушів()
    {
        var fixture = Arrange(submittedSheet: null, hiddenSheet: null, periodState: PeriodState.Closed);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => fixture.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId: 1, CancellationToken.None));

        Assert.Equal("err.ECR-CALC-4221.periodClosed", error.Details?["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Схований_аркуш_як_область_дає_404_а_не_стан_і_нічого_не_ставить_у_чергу()
    {
        var fixture = Arrange(submittedSheet: 3, hiddenSheet: 1);

        await Assert.ThrowsAsync<NotFoundException>(
            () => fixture.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId: 1, CancellationToken.None));

        await fixture.Jobs.DidNotReceiveWithAnyArgs()
            .EnqueueCoalescedAsync<IRecalculationJob>(default!, default, default);
        await fixture.Jobs.DidNotReceiveWithAnyArgs()
            .EnqueueExclusiveAsync<IRecalculationJob>(default!, default, default);
    }

    private static Fixture Arrange(int? submittedSheet, int? hiddenSheet, PeriodState periodState = PeriodState.Open)
    {
        var builder = new AccessBuilder { UserId = 9 }
            .Permission(RecalculateDocumentHandler.Permission)
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read);
        if (hiddenSheet is { } hidden)
        {
            builder.Deny(ResourceKind.Sheet, hidden);
        }

        var profile = builder.Build();

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(profile);
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.DocumentProjectIdAsync(Document, Arg.Any<CancellationToken>()).Returns(AccessBuilder.ProjectId);
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
              .Returns(call => DocumentReadScope.For(call.Arg<AccessProfile>(), AccessBuilder.ProjectId, SnapshotOfTwoSheets()));

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);

        var documents = Substitute.For<IDocumentStore>();
        documents.HasSheetAsync(Document, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodStateAsync(Document, Period, Arg.Any<CancellationToken>()).Returns((PeriodState?)periodState);

        var states = new List<ApprovalState>();
        if (submittedSheet is { } submitted)
        {
            var state = new ApprovalState(Document, submitted, Period);
            state.Submit(userId: 5, DateTime.UtcNow);
            states.Add(state);
        }

        var workflow = Substitute.For<IWorkflowStore>();
        workflow.GetSheetsAsync(Document, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                .Returns(states);

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.EnqueueCoalescedAsync<IRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("job-1");
        jobs.EnqueueExclusiveAsync<IRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("job-1");

        return new Fixture(new RecalculateDocumentHandler(jobs, access, user, documents, periods, workflow), jobs);
    }

    private static TemplateVersionSnapshot SnapshotOfTwoSheets()
    {
        var template = new TemplateBuilder();
        var s1 = template.Sheet("S1");
        template.Table(s1, "T1");
        var s2 = template.Sheet("S2");
        template.Table(s2, "T2");
        return template.Build();
    }

    private sealed record Fixture(RecalculateDocumentHandler Handler, IBackgroundJobScheduler Jobs);
}
