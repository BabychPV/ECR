// tests/Ecr.Application.Tests/Calculations/RunCalculationPurgeAfterCommitTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Q-17 (рішення людини 28.10: у базі лишаються результати лише актуального прогону): прибирання рядків замінених
/// прогонів іде ПІСЛЯ коміту перемикання, лише для прогонів, які перемикання повернуло, і збій прибирання прогін
/// не валить.
/// </summary>
/// <remarks>
/// Мутації: прибрати виклик <c>PurgeSupersededAsync</c> у <c>RunCalculationHandler.CompleteAsync</c> - перший і
/// третій тести червоніють; перенести виклик усередину транзакції - перший (порядок) червоніє; прибрати
/// <c>try/catch</c> - четвертий червоніє.
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage5)]
public sealed class RunCalculationPurgeAfterCommitTests
{
    private static readonly IReadOnlyList<SupersededRun> Replaced = [new SupersededRun(5, 202601, 77), new SupersededRun(6, 202601, null)];

    private readonly ICalculationResultStore results = Substitute.For<ICalculationResultStore>();
    private readonly IUnitOfWork uow = Substitute.For<IUnitOfWork>();
    private readonly IJobQueue queue = Substitute.For<IJobQueue>();
    private readonly IJobLeaseContext lease = Substitute.For<IJobLeaseContext>();
    private readonly List<string> events = [];

    public RunCalculationPurgeAfterCommitTests()
    {
        uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None);
                events.Add("commit");
            });
        results.SwitchCurrentRunAsync(7, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                events.Add("switch");
                return Task.FromResult(Replaced);
            });
        results.PurgeSupersededRunsAsync(Arg.Any<IReadOnlyList<SupersededRun>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                events.Add("purge");
                return Task.FromResult(0);
            });
    }

    [Fact]
    public async Task Прибирання_іде_після_коміту_для_прогонів_з_перемикання_без_оренди()
    {
        lease.Current.Returns((JobClaimToken?)null);

        await Handler().CompleteAsync(7, new ModuleProfile(), CancellationToken.None);

        Assert.Equal(["switch", "commit", "purge"], events);
        await results.Received(1).PurgeSupersededRunsAsync(
            Arg.Is<IReadOnlyList<SupersededRun>>(r => r.SequenceEqual(Replaced)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Прибирання_іде_після_коміту_і_для_задачі_з_орендою()
    {
        var claim = new JobClaimToken("IRecalculationJob-1", Guid.NewGuid());
        lease.Current.Returns(claim);
        queue.FenceAsync(claim, Arg.Any<CancellationToken>()).Returns(true);

        await Handler().CompleteAsync(7, new ModuleProfile(), CancellationToken.None);

        Assert.Equal(["switch", "commit", "purge"], events);
    }

    [Fact]
    public async Task Втрачена_оренда_нічого_не_прибирає()
    {
        var claim = new JobClaimToken("IRecalculationJob-1", Guid.NewGuid());
        lease.Current.Returns(claim);
        queue.FenceAsync(claim, Arg.Any<CancellationToken>()).Returns(false);

        await Assert.ThrowsAsync<JobLeaseLostException>(
            () => Handler().CompleteAsync(7, new ModuleProfile(), CancellationToken.None));

        await results.DidNotReceiveWithAnyArgs().PurgeSupersededRunsAsync(default!, default);
    }

    [Fact]
    public async Task Порожнє_перемикання_не_кличе_прибирання()
    {
        lease.Current.Returns((JobClaimToken?)null);
        results.SwitchCurrentRunAsync(7, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SupersededRun>>([]));

        await Handler().CompleteAsync(7, new ModuleProfile(), CancellationToken.None);

        await results.DidNotReceiveWithAnyArgs().PurgeSupersededRunsAsync(default!, default);
    }

    [Fact]
    public async Task Збій_прибирання_не_валить_прогін()
    {
        lease.Current.Returns((JobClaimToken?)null);
        results.PurgeSupersededRunsAsync(Arg.Any<IReadOnlyList<SupersededRun>>(), Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ => throw new InvalidOperationException("прибирання впало"));

        await Handler().CompleteAsync(7, new ModuleProfile(), CancellationToken.None);

        await results.Received(1).PurgeSupersededRunsAsync(
            Arg.Any<IReadOnlyList<SupersededRun>>(), Arg.Any<CancellationToken>());
    }

    private RunCalculationHandler Handler()
        => new(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            results,
            Substitute.For<IBackgroundJobScheduler>(),
            uow,
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(),
            Substitute.For<IClock>(),
            Substitute.For<IRecalculationApprovalStore>(),
            Substitute.For<IAuditWriter>(),
            queue,
            lease);
}
