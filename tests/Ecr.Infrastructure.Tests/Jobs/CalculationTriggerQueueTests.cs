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

        var target = $"{nameof(IRecalculationJob)}~{CalculationTrigger.AutoTargetOf(DocumentId, Period)}";
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
            $"{nameof(IRecalculationJob)}~{CalculationTrigger.AutoTargetOf(DocumentId, new PeriodKey(202602))}",
            (await RowAsync(february))?.TargetKey);
    }

    /// <summary>
    /// ⛔ B83: нічна дочірня задача (ціль <c>TargetOf</c>, без людини й без позначки поданих) чекає в черзі — збір PI
    /// ставить СВОЮ задачу з позначкою поданих, а не зливається в нічну (урок B63: інакше позначка губилась би, і
    /// поданий аркуш із новими даними лишався б непорахованим). Нічна задача лишається без позначки.
    /// </summary>
    /// <remarks>
    /// ⛔ ДОКАЗ (локально): <c>AutoTargetOf</c> → <c>RecalculateDocumentHandler.TargetOf</c> у тригері — <c>auto == nightly</c>,
    /// payload без <c>recalculateSubmitted</c>, червоний.
    /// </remarks>
    [Fact]
    [Trait("Finding", "B83")]
    public async Task Збір_не_зливається_в_нічну_задачу_і_не_губить_позначку_поданих()
    {
        await using var host = NewHost();
        var jobs = Scheduler(host);
        var trigger = Trigger(host);

        // Нічний розклад розклав дочірню задачу документа (`RecalculationJob.FanOutAsync`): та сама ціль, що в кнопки.
        var nightly = await jobs.EnqueueCoalescedAsync<IRecalculationJob>(
            RecalculateDocumentHandler.TargetOf(DocumentId, Period),
            new { ProjectId = 1, DocumentId, PeriodKey = Period.Value, TriggeredByUserId = (int?)null },
            CancellationToken.None);

        var auto = await trigger.RequestAsync(DocumentId, Period, CancellationToken.None);

        Assert.NotNull(auto);
        Assert.NotEqual(nightly, auto);
        Assert.Contains("\"recalculateSubmitted\":true", (await RowAsync(auto))?.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("recalculateSubmitted", (await RowAsync(nightly))?.Payload, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Finding", "AN-91-N2")]
    public async Task Автоперерахунок_усього_документа_не_поглинається_чергою_перерахунку_одного_аркуша()
    {
        await using var host = NewHost();
        var jobs = Scheduler(host);
        var trigger = Trigger(host);
        const int sheet = 5;

        // Користувач без права витіснення ставить перерахунок аркуша (Coalesced) — задача чекає.
        var sheetJob = await jobs.EnqueueCoalescedAsync<IRecalculationJob>(
            RecalculateDocumentHandler.ScopedTargetOf(DocumentId, Period, sheet),
            new { DocumentId, PeriodKey = Period.Value, TriggeredByUserId = (int?)9, SheetDefId = (int?)sheet },
            CancellationToken.None);

        // Збір PI ставить автоперерахунок УСЬОГО документа (SheetDefId = null).
        var auto = await trigger.RequestAsync(DocumentId, Period, CancellationToken.None);

        // ⛔ До AN-91 ціль була спільна, черга віддавала `sheetJob` із payload аркуша 5 — решта аркушів
        // документа лишалась непорахованою. Мутація: `ScopedTargetOf(..)` → `TargetOf(..)` у обробнику
        // і в цьому тесті — `auto == sheetJob`.
        Assert.NotNull(auto);
        Assert.NotEqual(sheetJob, auto);
        Assert.Contains("\"sheetDefId\":5", (await RowAsync(sheetJob))?.Payload);
        Assert.Contains("\"sheetDefId\":null", (await RowAsync(auto))?.Payload);
    }

    [Fact]
    [Trait("Finding", "AN-91-N2")]
    public async Task Спільна_ціль_поглинає_запит_іншої_області_і_лишає_payload_першої_ось_чому_аркуш_у_цілі()
    {
        // Характеризація черги (не вимога до продукту): за ТІЄЇ САМОЇ цілі друга постановка повертає
        // JobId першої, а payload першої лишається. Тому ціль з областю (AN-91) обов'язкова.
        await using var host = NewHost();
        var jobs = Scheduler(host);
        var target = RecalculateDocumentHandler.TargetOf(DocumentId, Period);

        var first = await jobs.EnqueueCoalescedAsync<IRecalculationJob>(
            target, new { SheetDefId = (int?)5 }, CancellationToken.None);
        var second = await jobs.EnqueueCoalescedAsync<IRecalculationJob>(
            target, new { SheetDefId = (int?)null }, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Contains("\"sheetDefId\":5", (await RowAsync(first))?.Payload);
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

        var documents = Substitute.For<IDocumentStore>();
        documents.GetIncludedSheetIdsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

        var sheetCopies = Substitute.For<IDocumentSheetCopyStore>();
        sheetCopies.ListGroupsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<SheetCopyGroupInfo>());

        return new CalculationTrigger(Scheduler(host), periods, workflow, documents, sheetCopies);
    }
}
