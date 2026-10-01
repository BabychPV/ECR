// tests/Ecr.Infrastructure.Tests/Jobs/CalculationTriggerQueueTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Автоперерахунок (<see cref="CalculationTrigger"/>, HSE301 A4) на СПРАВЖНІЙ черзі в базі:
/// виконуваний перерахунок не витісняється, повтори зливаються в одну задачу позаду.
/// </summary>
/// <remarks>
/// ⚠ Стан періоду й робочий процес — заглушки: тут перевіряється лише те, що робить
/// з постановкою черга. Правило «куди можна» — <c>CalculationTriggerTests</c>, справжні
/// сховища — <c>AutoRecalcAfterMaterializeTests</c>. Детерміновано: жодного таймінгу,
/// «виконується» — це <c>ClaimAsync</c>, а не гонитва потоків.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "HSE301-A4")]
public sealed class CalculationTriggerQueueTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private const long DocumentId = 7701;
    private static readonly PeriodKey Period = new(202601);

    [Fact]
    public async Task Виклик_під_час_виконання_не_витісняє_а_ставить_одну_задачу_позаду_і_поглинає_повтори()
    {
        await using var host = NewHost();
        var trigger = Trigger(host);

        var first = await trigger.RequestAsync(DocumentId, Period, CancellationToken.None);
        Assert.NotNull(first);

        // Перерахунок «пішов»: воркер узяв задачу.
        var claimed = await host.Queue.ClaimAsync(
            [JobLanes.Recalc], "test/host", JobQueueLimits.DefaultLease, CancellationToken.None);
        Assert.Equal(first, claimed?.Claim.JobId);

        var behind = await trigger.RequestAsync(DocumentId, Period, CancellationToken.None);

        Assert.NotNull(behind);
        Assert.NotEqual(first, behind);
        Assert.Equal("Queued", (await RowAsync(behind))?.State);

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: `EnqueueCoalescedAsync` → `EnqueueExclusiveAsync` у
        // `CalculationTrigger` → виконуваному ставиться запит скасування: гарячий
        // документ переривали б на кожному новому записі, і він не доходив би до кінця.
        var running = await RowAsync(first);
        Assert.Equal("Running", running?.State);
        Assert.Null(running?.CancelRequestedAt);

        // Ще два виклики (три сутності одного прогону збору) — та сама задача позаду.
        Assert.Equal(behind, await trigger.RequestAsync(DocumentId, Period, CancellationToken.None));
        Assert.Equal(behind, await trigger.RequestAsync(DocumentId, Period, CancellationToken.None));

        var target = $"{nameof(IRecalculationJob)}~{RecalculateDocumentHandler.TargetOf(DocumentId, Period)}";
        await using var db = Sql.CreateContext();
        Assert.Equal(1, await db.JobProgresses.CountAsync(p => p.TargetKey == target && p.State == "Queued"));
        Assert.Equal(1, await db.JobProgresses.CountAsync(p => p.TargetKey == target && p.State == "Running"));
        Assert.Null((await RowAsync(first))?.CancelRequestedAt);
    }

    [Fact]
    public async Task Різні_періоди_одного_документа_різні_цілі_одне_одного_не_поглинають()
    {
        await using var host = NewHost();
        var trigger = Trigger(host);

        var january = await trigger.RequestAsync(DocumentId, Period, CancellationToken.None);
        var february = await trigger.RequestAsync(DocumentId, new PeriodKey(202602), CancellationToken.None);

        Assert.NotNull(january);
        Assert.NotNull(february);
        Assert.NotEqual(january, february);
        Assert.Equal(
            $"{nameof(IRecalculationJob)}~{RecalculateDocumentHandler.TargetOf(DocumentId, new PeriodKey(202602))}",
            (await RowAsync(february))?.TargetKey);
    }

    [Fact]
    public async Task Задачу_кнопки_і_автоперерахунку_черга_бачить_як_одну_ціль()
    {
        await using var host = NewHost();
        var jobs = Scheduler(host);
        var trigger = Trigger(host);

        // Ручна кнопка «Перерахувати» ставить через `EnqueueExclusiveAsync` з тією самою ціллю.
        var manual = await jobs.EnqueueExclusiveAsync<IRecalculationJob>(
            RecalculateDocumentHandler.TargetOf(DocumentId, Period), new { }, CancellationToken.None);

        var auto = await trigger.RequestAsync(DocumentId, Period, CancellationToken.None);

        // ⚠ Ручна ще чекає в черзі — автоперерахунок її поглинає, а не дублює.
        Assert.Equal(manual, auto);
    }

    private static DbBackgroundJobScheduler Scheduler(Host host)
        => new(
            host.Queue,
            new QuartzJobScheduler(null, new JobProgressStore(host.Db), new SystemClock()),
            new JobQueueSignal());

    private static CalculationTrigger Trigger(Host host)
    {
        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodStateAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(PeriodState.Open);

        var workflow = Substitute.For<IWorkflowStore>();
        workflow.GetSheetsAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ApprovalState>());

        return new CalculationTrigger(Scheduler(host), periods, workflow);
    }
}
