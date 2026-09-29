// tests/Ecr.Infrastructure.Tests/Jobs/RunCalculationFencingTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Fencing видимості (MI-02, правка «Аудиту» 1, тест 4б): виконавець, чию оренду
/// перехопили, не робить свій прогін актуальним.
/// </summary>
/// <remarks>
/// Сценарій: A захопив задачу, оренду прострочено, B перезахопив. A доробляє й
/// кличе <see cref="RunCalculationHandler.CompleteAsync"/> —
/// <see cref="JobLeaseLostException"/>, прогін A НЕ актуальний; B — актуальний.
/// Мутація: прибрати <c>FenceAsync</c> у <c>CompleteAsync</c> — прогін A стає
/// актуальним, тест червоний.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "ФВ-9.11")]
public sealed class RunCalculationFencingTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    [Fact]
    public async Task Перехоплена_оренда_відкочує_перемикання_прогону_A_а_B_стає_актуальним()
    {
        var document = await new TestDocumentBuilder(Sql.ConnectionString).BuildAsync();
        var (runA, runB) = await TwoRunsAsync(document.ProjectId, document.PeriodKey.Value);

        var jobId = await EnqueueAsync();

        await using var hostA = NewHost();
        var claimA = await hostA.Queue.ClaimAsync(
            DefaultLane, "test/host-a", TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.NotNull(claimA);

        await ExpireLeaseAsync(jobId);

        await using var hostB = NewHost();
        var claimB = await hostB.ClaimAsync("test/host-b");
        Assert.True(claimB?.Reclaimed);

        var lost = await Record.ExceptionAsync(
            () => Handler(hostA, claimA.Claim).CompleteAsync(runA, new ModuleProfile(), CancellationToken.None));

        // ⛔ Головне твердження — видимість, а не тип винятку: без fencing прогін A актуальний.
        Assert.False(await IsCurrentAsync(runA), "Прогін виконавця з перехопленою орендою став актуальним.");
        Assert.IsType<JobLeaseLostException>(lost);

        await Handler(hostB, claimB!.Claim).CompleteAsync(runB, new ModuleProfile(), CancellationToken.None);

        Assert.True(await IsCurrentAsync(runB));
        Assert.False(await IsCurrentAsync(runA));
    }

    private async Task<(long A, long B)> TwoRunsAsync(int projectId, int periodKey)
    {
        await using var db = Sql.CreateContext();
        var at = new DateTime(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);
        var a = new CalculationRun(projectId, periodKey, triggeredByUserId: null, at);
        var b = new CalculationRun(projectId, periodKey, triggeredByUserId: null, at);
        db.CalculationRuns.AddRange(a, b);
        await db.SaveChangesAsync();
        return (a.Id, b.Id);
    }

    private async Task<bool> IsCurrentAsync(long runId)
    {
        await using var db = Sql.CreateContext();
        return (await db.CalculationRuns.AsNoTracking().SingleAsync(r => r.Id == runId)).IsCurrent;
    }

    /// <summary>Обробник у «scope задачі»: той самий контекст для черги, сховища й одиниці роботи.</summary>
    private static RunCalculationHandler Handler(Host host, JobClaimToken claim)
    {
        var lease = new JobLeaseContext();
        lease.Bind(claim);
        var clock = new SystemClock();

        return new RunCalculationHandler(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            new CalculationResultStore(host.Db, clock),
            Substitute.For<IBackgroundJobScheduler>(),
            new UnitOfWork(host.Db),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(),
            clock,
            Substitute.For<IRecalculationApprovalStore>(),
            Substitute.For<IAuditWriter>(),
            host.Queue,
            lease);
    }
}
