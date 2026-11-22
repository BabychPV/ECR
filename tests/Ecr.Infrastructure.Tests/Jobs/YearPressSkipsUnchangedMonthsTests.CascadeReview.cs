// tests/Ecr.Infrastructure.Tests/Jobs/YearPressSkipsUnchangedMonthsTests.CascadeReview.cs
using System.Diagnostics;
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ PERF-06 (<c>D-566</c>), огляд F1–F3: провал кроку ланцюжка каскаду не губить решту місяців; місяць, скинутий через
/// задачу «з затвердженими», однаково будить пізніших читачів; старий payload ланцюжка без плану не відкидає дописані місяці.
/// </summary>
public sealed partial class YearPressSkipsUnchangedMonthsTests
{
    /// <summary>Місяць, на якому фейк оркестратора падає (детермінований збій, без ретраю); <c>null</c> — не падає.</summary>
    private int? _failPeriod;

    /// <summary>
    /// F1: справжній виконавець черги. Крок квітня падає не транзієнтно — рядок ланцюжка <c>Failed</c> з місяцем у помилці, а
    /// решта плану (травень) і дописаний злиттям червень рахуються новою задачею каскаду на ту саму ціль.
    /// </summary>
    [Fact]
    public async Task Провал_кроку_каскаду_не_губить_решту_і_дописані_місяці()
    {
        var (builder, document) = await ArrangeAsync();
        _failPeriod = 202604;
        var target = CalculationTrigger.SourceCascadeTargetOf(document.DocumentId, Year);
        var before = await MaxRunIdAsync();
        string jobId;

        // Перший крок (лютий) — вручну; між кроками зливається нова правка (червень → cascadeAppend).
        await using (var host = NewHost())
        {
            var scheduler = Scheduler(host);
            jobId = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
                target, CascadePayload(document, 202602, 202604, 202605), CancellationToken.None);
            var steps = new List<int>();
            await StepAsync(builder, host, Press, steps, null);
            Assert.Equal([202602], steps);
            Assert.Equal(jobId, await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
                target, CascadePayload(document, 202606), CancellationToken.None));
            var merged = JsonSerializer.Deserialize<RecalculationRequest>((await RowAsync(jobId))!.Payload!, Web)!;
            Assert.Equal([202606], merged.CascadeAppend!);
        }

        await RunWorkerUntilIdleAsync(builder, target);

        // Квітень — проваленим прогоном (видно в журналі прогонів), травень і червень — пораховано.
        Assert.Equal([202602, 202604, 202605, 202606], await NewRunPeriodsAsync(document.DocumentId, before));
        Assert.Equal([202602, 202605, 202606], await CurrentPeriodsStartedAtAsync(document.DocumentId, Press));

        var failed = (await RowAsync(jobId))!;
        Assert.Equal("Failed", failed.State);
        Assert.Contains("202604", failed.Error, StringComparison.Ordinal);

        await using var db = Sql.CreateContext();
        var rows = await db.JobProgresses.AsNoTracking().Where(p => p.TargetKey == $"{nameof(IRecalculationJob)}~{target}")
            .ToListAsync();
        var followUp = Assert.Single(rows, r => r.JobId != jobId);
        Assert.Equal("Succeeded", followUp.State);
        Assert.Contains(followUp.JobId, failed.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// F2: місяць, скинутий з каскаду, бо його вже покриває задача «з затвердженими» (<c>-dependents-approved</c>), сам не
    /// рахується (його рахує та задача), але пізніші місяці, що читають його обчислені числа (<c>[Period:#4]</c>), — так: задача
    /// названого періоду будить лише ранніших читачів.
    /// </summary>
    [Theory]
    [InlineData(new[] { 202602, 202604 }, new[] { 202602, 202605, 202606 })]
    [InlineData(new[] { 202604 }, new[] { 202605, 202606 })]
    public async Task Місяць_покритий_задачею_з_затвердженими_будить_пізніших_читачів(int[] edited, int[] expected)
    {
        var (builder, document) = await ArrangeAsync();
        await CrossReadAsync(document, kind: 5, offset: 4);
        await using var host = NewHost();
        var scheduler = Scheduler(host);

        // Покривна задача квітня чекає (довго — у цьому тесті її не беремо).
        await scheduler.EnqueueCoalescedDelayedAsync<IRecalculationJob>(
            CalculationTrigger.DependentsApprovedTargetOf(document.DocumentId, new PeriodKey(202604)),
            new { document.DocumentId, PeriodKey = 202604, IncludeApproved = true }, TimeSpan.FromHours(1), CancellationToken.None);

        var queued = await OpenTrigger(scheduler).RequestSourceCascadeAsync(document.DocumentId, Year, edited, 1, CancellationToken.None);
        Assert.NotNull(queued.JobId);
        await ExecAsync(
            "UPDATE itg.JobProgress SET AvailableAt = DATEADD(second, -1, SYSUTCDATETIME()) WHERE JobId = @id AND [State] = 'Queued';",
            queued.JobId!);

        var before = await MaxRunIdAsync();
        var steps = new List<int>();
        while (await StepAsync(builder, host, Press, steps, null))
        {
        }

        Assert.Equal(expected, await NewRunPeriodsAsync(document.DocumentId, before));
    }

    /// <summary>
    /// F2 у ланцюжку: правка, ЄДИНИЙ період якої (квітень) покриває задача «з затвердженими», злилася в ланцюжок каскаду між
    /// кроками — дописуються пізніші читачі квітня (травень, червень), сам квітень — ні.
    /// </summary>
    [Fact]
    public async Task Затравка_злита_в_ланцюжок_дописує_пізніших_читачів()
    {
        var (builder, document) = await ArrangeAsync();
        await CrossReadAsync(document, kind: 5, offset: 4);
        await using var host = NewHost();
        var scheduler = Scheduler(host);
        await scheduler.EnqueueCoalescedDelayedAsync<IRecalculationJob>(
            CalculationTrigger.DependentsApprovedTargetOf(document.DocumentId, new PeriodKey(202604)),
            new { document.DocumentId, PeriodKey = 202604, IncludeApproved = true }, TimeSpan.FromHours(1), CancellationToken.None);
        var jobId = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            CalculationTrigger.SourceCascadeTargetOf(document.DocumentId, Year), CascadePayload(document, 202602, 202603),
            CancellationToken.None);
        var before = await MaxRunIdAsync();
        var steps = new List<int>();
        await StepAsync(builder, host, Press, steps, null);

        var queued = await OpenTrigger(scheduler).RequestSourceCascadeAsync(document.DocumentId, Year, [202604], 1, CancellationToken.None);
        Assert.Equal(jobId, queued.JobId);
        while (await StepAsync(builder, host, Press, steps, null))
        {
        }

        Assert.Equal([202602, 202603, 202605, 202606], await NewRunPeriodsAsync(document.DocumentId, before));
    }

    /// <summary>
    /// Прогалина мутацій (огляд PERF-06): читачів кроку дописує в план і САМ крок (<c>ExtendWithLaterReadersAsync</c>), а не
    /// лише план першого кроку. Залежність <c>[Period:#3]</c> з'явилась МІЖ кроками (структура шаблону змінилась, план
    /// першого кроку її не бачив) — крок березня дописує травень… червень (і квітень).
    /// </summary>
    /// <remarks>МУТАЦІЯ: лише <c>ExtendWithLaterReadersAsync</c> → повертати <c>remaining</c> — червоний ([202602, 202603]).</remarks>
    [Fact]
    public async Task Крок_дописує_читачів_залежності_що_зявилась_між_кроками()
    {
        var (builder, document) = await ArrangeAsync();
        await using var host = NewHost();
        var jobId = await Scheduler(host).EnqueueCoalescedAsync<IRecalculationJob>(
            CalculationTrigger.SourceCascadeTargetOf(document.DocumentId, Year), CascadePayload(document, 202602, 202603),
            CancellationToken.None);
        var before = await MaxRunIdAsync();
        var steps = new List<int>();
        await StepAsync(builder, host, Press, steps, null);
        var planned = JsonSerializer.Deserialize<RecalculationRequest>((await RowAsync(jobId))!.Payload!, Web)!;
        Assert.Equal([202603], planned.ChainPlan!);

        await CrossReadAsync(document, kind: 5, offset: 3);
        while (await StepAsync(builder, host, Press, steps, null))
        {
        }

        Assert.Equal([202602, 202603, 202604, 202605, 202606], await NewRunPeriodsAsync(document.DocumentId, before));
    }

    /// <summary>
    /// F3: старий payload ланцюжка (без плану, <c>ChainPlan = null</c>) зі злитими місяцями каскаду (<c>CascadeAppend</c>) бачить
    /// увесь рік — дописаний місяць не пізніший за <c>ChainAfter</c> не відкидається межею скоупу.
    /// </summary>
    [Theory]
    [InlineData(new[] { 202603 }, 202600)]
    [InlineData(null, 202606)]
    public void Старий_payload_ланцюжка_з_дописаними_місяцями_бачить_увесь_рік(int[]? appended, int fromKey)
    {
        var request = new RecalculationRequest(1, 1, null, null, Year: Year, ChainAfter: 202605, CascadeAppend: appended);

        Assert.Equal(fromKey, request.FromKey);
    }

    /// <summary>Тригер на вільних сховищах: кожен період відкритий, аркушів немає (лише постановка).</summary>
    private static CalculationTrigger OpenTrigger(IBackgroundJobScheduler jobs)
    {
        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodStateAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(PeriodState.Open);

        var workflow = Substitute.For<IWorkflowStore>();
        workflow.GetSheetsAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ApprovalState>());

        var documents = Substitute.For<IDocumentStore>();
        documents.GetIncludedSheetIdsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

        var sheetCopies = Substitute.For<IDocumentSheetCopyStore>();
        sheetCopies.ListGroupsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<SheetCopyGroupInfo>());

        return new CalculationTrigger(jobs, periods, workflow, documents, sheetCopies);
    }

    /// <summary>Справжній <see cref="JobWorker"/> над задачею перерахунку стенда, доки на ціль нічого не чекає й не виконується.</summary>
    private async Task RunWorkerUntilIdleAsync(TestDocumentBuilder builder, string target)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Sql.CreateContext());
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IJobQueue>(sp => new DbJobQueue(sp.GetRequiredService<EcrDbContext>(), sp.GetRequiredService<IClock>()));
        services.AddScoped<IJobProgressStore>(sp => new JobProgressStore(sp.GetRequiredService<EcrDbContext>()));
        services.AddScoped<JobLeaseContext>();
        services.AddScoped<IJobLeaseContext>(sp => sp.GetRequiredService<JobLeaseContext>());
        services.AddSingleton<JobQueueSignal>();
        services.AddScoped<IRecalculationJob>(sp =>
        {
            var db = sp.GetRequiredService<EcrDbContext>();
            var queue = (DbJobQueue)sp.GetRequiredService<IJobQueue>();
            var scheduler = new DbBackgroundJobScheduler(
                queue, new QuartzJobScheduler(null, new JobProgressStore(db), new SystemClock()), new JobQueueSignal());
            return NewJob(builder, db, Press, scheduler, sp.GetRequiredService<IJobLeaseContext>(), queue);
        });
        await using var provider = services.BuildServiceProvider();

        var worker = new JobWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new JobWorkerOptions
            {
                Lanes = [JobLanes.Recalc],
                Role = JobProgressStore.CurrentRole,
                MaxConcurrency = 1,
                PollInterval = TimeSpan.FromMilliseconds(50),
                RenewInterval = TimeSpan.FromMilliseconds(200),
            },
            provider.GetRequiredService<JobQueueSignal>(),
            NullLogger<JobWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var key = $"{nameof(IRecalculationJob)}~{target}";
            var watch = Stopwatch.StartNew();
            while (true)
            {
                await Task.Delay(200);
                await using var db = Sql.CreateContext();
                if (!await db.JobProgresses.AnyAsync(p => p.TargetKey == key && (p.State == "Queued" || p.State == "Running")))
                {
                    break;
                }

                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), "Ланцюжок каскаду не дійшов кінця за 60 с.");
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }
}
