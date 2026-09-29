// tests/Ecr.Application.Tests/Calculations/RunCalculationCompleteFencingTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// <see cref="RunCalculationHandler.CompleteAsync"/>: fencing лише для задачі з черги
/// (оренда в scope), шлях Quartz/HTTP — без змін (MI-02, F1c).
/// </summary>
/// <remarks>
/// Мутації: перемикання ДО FenceAsync — другий тест червоний (перемикання вже
/// відбулося, коли оренду втрачено); fencing і без оренди — перший червоний.
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage5)]
public sealed class RunCalculationCompleteFencingTests
{
    private readonly ICalculationResultStore results = Substitute.For<ICalculationResultStore>();
    private readonly IUnitOfWork uow = Substitute.For<IUnitOfWork>();
    private readonly IJobQueue queue = Substitute.For<IJobQueue>();
    private readonly IJobLeaseContext lease = Substitute.For<IJobLeaseContext>();

    public RunCalculationCompleteFencingTests()
        => uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

    [Fact]
    public async Task Без_оренди_перемикає_як_раніше_без_транзакції_черги()
    {
        lease.Current.Returns((JobClaimToken?)null);

        await Handler().CompleteAsync(7, new ModuleProfile(), CancellationToken.None);

        await results.Received(1).SwitchCurrentRunAsync(7, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await queue.DidNotReceiveWithAnyArgs().FenceAsync(default!, default);
        await uow.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(default!, default);
    }

    [Fact]
    public async Task Втрачена_оренда_кидає_і_не_перемикає_жива_перемикає_в_транзакції()
    {
        var claim = new JobClaimToken("IRecalculationJob-1", Guid.NewGuid());
        lease.Current.Returns(claim);

        queue.FenceAsync(claim, Arg.Any<CancellationToken>()).Returns(false);
        await Assert.ThrowsAsync<JobLeaseLostException>(
            () => Handler().CompleteAsync(7, new ModuleProfile(), CancellationToken.None));
        await results.DidNotReceiveWithAnyArgs().SwitchCurrentRunAsync(default, default!, default);

        queue.FenceAsync(claim, Arg.Any<CancellationToken>()).Returns(true);
        await Handler().CompleteAsync(7, new ModuleProfile(), CancellationToken.None);
        await results.Received(1).SwitchCurrentRunAsync(7, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await uow.Received(2).ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
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
