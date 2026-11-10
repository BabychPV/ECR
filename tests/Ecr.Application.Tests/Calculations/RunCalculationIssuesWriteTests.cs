// tests/Ecr.Application.Tests/Calculations/RunCalculationIssuesWriteTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// AN-69: завершення прогону замінює проблеми розрахунку в транзакції перемикання; прогін, відмовлений як старіший
/// (<c>Superseded</c>), нічого не замінює.
/// </summary>
/// <remarks>Мутація: прибрати умову <c>!superseded.Any(...)</c> в <c>RunCalculationHandler.SwitchAsync</c> - червоніє другий тест.</remarks>
public sealed class RunCalculationIssuesWriteTests
{
    private const long RunId = 55;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Актуальний_прогін_передає_сховищу_перераховані_таблиці_і_рядки_без_правила()
    {
        var (handler, issues) = Arrange(supersededSelf: false);
        var profile = new ModuleProfile();
        profile.RecordEvaluatedTables([10, 11]);
        profile.RecordUnmatched([new UnmatchedRow(10, 2, "R2", 900, 7)]);

        await handler.CompleteAsync(RunId, profile, CancellationToken.None);

        await issues.Received(1).ReplaceAsync(
            RunId,
            Arg.Is<IReadOnlyCollection<long>>(t => t.Order().SequenceEqual(new long[] { 10, 11 })),
            Arg.Is<IReadOnlyCollection<CalculationIssueInput>>(i => i.Single().RowKey == "R2" && i.Single().MethodologyId == 7),
            Arg.Any<CancellationToken>());
    }

    /// <remarks>
    /// ✎ 10.11 (doc 95): рядок, відхилений правилом категорії, раніше лишав лише повідомлення задачі — панель казала
    /// «none» без причини. Мутація: прибрати додавання <c>profile.RejectedRows</c> у <c>SwitchAsync</c> — тест червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Відхилений_правилом_категорії_рядок_стає_проблемою_свого_виду_з_методологією()
    {
        var (handler, issues) = Arrange(supersededSelf: false);
        var profile = new ModuleProfile();
        profile.RecordEvaluatedTables([10]);
        profile.RecordRejected([new RejectedRow(10, 1, "R1", 900, 2), new RejectedRow(10, 1, "R1", 900, 2)]);

        await handler.CompleteAsync(RunId, profile, CancellationToken.None);

        await issues.Received(1).ReplaceAsync(
            RunId,
            Arg.Any<IReadOnlyCollection<long>>(),
            Arg.Is<IReadOnlyCollection<CalculationIssueInput>>(i =>
                i.Count == 1
                && i.Single().RowKey == "R1"
                && i.Single().RowNumber == 1
                && i.Single().MethodologyId == 2
                && i.Single().Kind == ICalculationIssueStore.CategoryRuleRejected),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Відмовлений_як_старіший_прогін_не_замінює_проблеми()
    {
        var (handler, issues) = Arrange(supersededSelf: true);
        var profile = new ModuleProfile();
        profile.RecordEvaluatedTables([10]);

        await handler.CompleteAsync(RunId, profile, CancellationToken.None);

        await issues.DidNotReceiveWithAnyArgs().ReplaceAsync(default, default!, default!, default);
    }

    private static (RunCalculationHandler Handler, ICalculationIssueStore Issues) Arrange(bool supersededSelf)
    {
        var results = Substitute.For<ICalculationResultStore>();
        results.SwitchCurrentRunAsync(RunId, Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(supersededSelf ? [new SupersededRun(RunId, 202601, 900)] : []);
        results.InvalidateReportSnapshotsAsync(RunId, Arg.Any<CancellationToken>())
               .Returns(Array.Empty<InvalidatedReportSnapshot>());

        var uow = Substitute.For<IUnitOfWork>();
        uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
           .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        var issues = Substitute.For<ICalculationIssueStore>();
        var handler = new RunCalculationHandler(
            Substitute.For<IPeriodStore>(), Substitute.For<IWorkflowStore>(), results,
            Substitute.For<IBackgroundJobScheduler>(), uow, Substitute.For<IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(), Substitute.For<IClock>(),
            Substitute.For<IAuditWriter>(), issues: issues);

        return (handler, issues);
    }
}
