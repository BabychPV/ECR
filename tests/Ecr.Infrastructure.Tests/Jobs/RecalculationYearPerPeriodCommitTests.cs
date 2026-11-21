// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationYearPerPeriodCommitTests.cs
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
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
/// ✎ PSS-04 (аудит 20.11, <c>D-561</c>; рішення людини: «місяці річного перерахунку стають актуальними по одному»):
/// перерахунок документа за кілька періодів у черзі в базі — ланцюжок кроків по одному періоду. Крок робить свій
/// період актуальним, звільняє лок документа й місце виконавця і продовжується наступним кроком — тим самим рядком
/// черги з новим payload; між кроками йдуть інші задачі документа (правка комірки); межа тривалості — на крок.
/// </summary>
/// <remarks>
/// Справжні черга, сховище результатів і перемикання актуальності (<see cref="RunCalculationHandler.CompleteAsync"/>);
/// оркестратор — фейк, що пише результати справжнім сховищем (як у <c>RecalculationJobYearRunResultsVisibilityTests</c>).
/// Кроки ганяє тест (claim → задача → <see cref="IJobQueue.ContinueAsync"/>), як виконавець, — детерміновано.
/// <para>
/// МУТАЦІЇ (локально): (1) <c>chain = false</c> у <c>RecalculationJob.ExecuteAsync</c> — червоний
/// <c>Місяці_стають_актуальними_по_одному…</c> (крок 1 рахує все й не продовжується); (2) <c>JobWorker</c> без
/// гілки <c>JobContinuedException</c> (продовження йде провалом) — червоний <c>Межа_тривалості_діє_на_крок…</c>;
/// (3) без <c>EditFirstExclusion</c> у claim лейна перерахунку — червоний перший тест (наступний крок узяв би документ
/// раніше за правку); (4) без <c>ParkRemainingAsync</c> — червоний перший тест (залежні березня порахувались би окремо);
/// (5) огляд H2: <c>ContinueAsync</c> з <c>AvailableAt = зараз</c> чи паркування ручних/авто на пізніший місяць — червоний.
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "PSS-04")]
public sealed class RecalculationYearPerPeriodCommitTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private static readonly DateTime Now = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);

    private static readonly PeriodKey February = new(202602);

    private static readonly PeriodKey March = new(202603);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private int _methodologyVersionId;

    private int _unitId;

    [Fact]
    public async Task Місяці_стають_актуальними_по_одному_а_між_ними_йде_правка_комірки_документа()
    {
        var (builder, document) = await ArrangeAsync();
        var january = document.PeriodKey;

        await using var host = NewHost();
        var scheduler = Scheduler(host);
        var jobId = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            RecalculateDocumentHandler.YearTargetOf(document.DocumentId),
            new { document.DocumentId, PeriodKey = (int?)null, TriggeredByUserId = (int?)null }, CancellationToken.None);

        // Залежні березня (каскад, вікно 10 с) — та сама робота, яку ланцюжок зробить на своєму кроці березня.
        var marchDependents = await scheduler.EnqueueCoalescedDelayedAsync<IRecalculationJob>(
            CalculationTrigger.DependentsTargetOf(document.DocumentId, March),
            new { document.DocumentId, PeriodKey = March.Value, RecalculateSubmitted = true, SourceHop = 1 },
            CalculationTrigger.CascadeDebounce, CancellationToken.None);

        // Автоперерахунок березня (збір PI, D-174): огляд H2 — НЕ паркується на пізніший місяць ланцюжка.
        var marchAuto = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            CalculationTrigger.AutoTargetOf(document.DocumentId, March),
            new { document.DocumentId, PeriodKey = March.Value, RecalculateSubmitted = true }, CancellationToken.None);

        // ── Крок 1: січень.
        var step = (await ClaimAsync(host, JobLanes.Recalc))!;
        Assert.Equal(jobId, step.Claim.JobId);

        // Ручний перерахунок березня людиною — поставлений, поки крок іде: огляд H2 — теж НЕ паркується.
        var marchManual = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            RecalculateDocumentHandler.TargetOf(document.DocumentId, March),
            new { document.DocumentId, PeriodKey = March.Value, TriggeredByUserId = 7 }, CancellationToken.None, 7);

        // Правка комірки під час кроку: задачу формул документа claim не бере, поки крок виконується.
        var edit = await EnqueueEditAsync(document.DocumentId);
        Assert.Null(await ClaimAsync(host, JobLanes.Interactive));

        var continued = await Assert.ThrowsAsync<JobContinuedException>(
            () => RunStepAsync(builder, scheduler, host.Queue, step));
        Assert.Equal(january.Value, continued.SettleThrough);
        Assert.Equal(TimeSpan.Zero, continued.Delay);
        var next = JsonSerializer.Deserialize<RecalculationRequest>(continued.PayloadJson, Web)!;
        Assert.Equal(january.Value, next.ChainAfter);
        Assert.Equal([january.Value], next.ChainDone!);

        // Січень уже актуальний, лютий і березень ще не рахувались.
        Assert.Equal([january.Value], await CurrentPeriodsAsync(document.DocumentId));

        var availableBefore = (await RowAsync(jobId))!.AvailableAt;
        Assert.True(await host.Queue.ContinueAsync(
            step.Claim, continued.PayloadJson, continued.Delay, continued.SettleThrough, CancellationToken.None));

        // Огляд H2: крок зберігає місце ланцюжка в FIFO (рік документа — вглиб).
        Assert.Equal(availableBefore, (await RowAsync(jobId))!.AvailableAt);

        // Залежні березня — запарковані ланцюжком до його кроку березня; ручний і авто — ні.
        AssertParked(await RowAsync(marchDependents), jobId);
        Assert.Equal(("Queued", 2026), ((await RowAsync(marchManual))!.State, (await RowAsync(marchManual))!.AvailableAt!.Value.Year));
        Assert.Equal(2026, (await RowAsync(marchAuto))!.AvailableAt!.Value.Year);

        // Між місяцями: на документ чекає правка — лейн перерахунку документ не бере (поступається їй), її бере воркер Api;
        // лок документа вільний.
        Assert.Null(await ClaimAsync(host, JobLanes.Recalc));
        var editClaim = await ClaimAsync(host, JobLanes.Interactive);
        Assert.Equal(edit, editClaim?.Claim.JobId);
        Assert.Null(await ClaimAsync(host, JobLanes.Recalc));
        await using (var lockDb = builder.CreateContext())
        {
            await using var free = await RecalculationDocumentLock.AcquireAsync(
                lockDb, document.DocumentId, TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.NotNull(free);
        }

        Assert.True(await host.Queue.CompleteAsync(editClaim!.Claim, CancellationToken.None));

        // Огляд H2: ручний березень іде попереду ланцюжка (людина не чекає року), автоперерахунок — у FIFO за ним.
        var manualClaim = await ClaimAsync(host, JobLanes.Recalc);
        Assert.Equal(marchManual, manualClaim?.Claim.JobId);
        Assert.True(await host.Queue.CompleteAsync(manualClaim!.Claim, CancellationToken.None));

        // ── Крок 2: лютий — той самий рядок черги, спроба не рахується.
        step = (await ClaimAsync(host, JobLanes.Recalc))!;
        Assert.Equal((jobId, 1), (step.Claim.JobId, step.Attempt));
        continued = await Assert.ThrowsAsync<JobContinuedException>(() => RunStepAsync(builder, scheduler, host.Queue, step));
        Assert.Equal((February.Value, TimeSpan.Zero), (continued.SettleThrough, continued.Delay));
        Assert.Equal([january.Value, February.Value], await CurrentPeriodsAsync(document.DocumentId));
        Assert.True(await host.Queue.ContinueAsync(
            step.Claim, continued.PayloadJson, continued.Delay, continued.SettleThrough, CancellationToken.None));
        AssertParked(await RowAsync(marchDependents), jobId);

        // ── Крок 3: березень — останній, задача закінчується. Автоперерахунок березня, що досі чекає, крок березня
        // покриває на своєму старті (свій період).
        step = (await ClaimAsync(host, JobLanes.Recalc))!;
        Assert.Equal(jobId, step.Claim.JobId);
        await RunStepAsync(builder, scheduler, host.Queue, step);
        Assert.True(await host.Queue.CompleteAsync(step.Claim, CancellationToken.None));

        Assert.Equal([january.Value, February.Value, March.Value], await CurrentPeriodsAsync(document.DocumentId));
        Assert.Equal("Succeeded", (await RowAsync(jobId))!.State);

        // Залежні й авто березня закрито кроком березня, а не пораховано вдруге.
        Assert.Equal("Succeeded", (await RowAsync(marchDependents))!.State);
        Assert.Equal("Succeeded", (await RowAsync(marchAuto))!.State);

        // По одному прогону на період — жодного повтору.
        await using var db = builder.CreateContext();
        var runs = await db.CalculationRuns.AsNoTracking()
            .Where(r => r.DocumentId == document.DocumentId)
            .GroupBy(r => r.PeriodKey)
            .Select(g => new { Period = g.Key, Count = g.Count() })
            .ToListAsync();
        Assert.All(runs, r => Assert.Equal(1, r.Count));
        Assert.Equal(3, runs.Count);
    }

    /// <summary>
    /// ✎ Огляд H1: нова постановка на ціль ланцюжка, що чекає між кроками, зливається в нього — і він починає знову з
    /// ПЕРШОГО періоду (зміна могла торкнутися вже зроблених місяців), а позначка батька — нового розкладу.
    /// </summary>
    /// <remarks>МУТАЦІЯ: без скидання полів ланцюжка в <c>CoalescePayloadSql</c> — крок 2 рахує лютий, тест червоний.</remarks>
    [Fact]
    public async Task Нова_постановка_на_ціль_ланцюжка_між_кроками_починає_його_з_першого_періоду()
    {
        var (builder, document) = await ArrangeAsync();
        var january = document.PeriodKey;

        await using var host = NewHost();
        var scheduler = Scheduler(host);
        var target = RecalculateDocumentHandler.YearTargetOf(document.DocumentId);
        var jobId = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            target, new { document.DocumentId, PeriodKey = (int?)null, FanOutParentJobId = "parent-old" }, CancellationToken.None);

        var step = (await ClaimAsync(host, JobLanes.Recalc))!;
        var continued = await Assert.ThrowsAsync<JobContinuedException>(() => RunStepAsync(builder, scheduler, host.Queue, step));
        Assert.True(await host.Queue.ContinueAsync(
            step.Claim, continued.PayloadJson, continued.Delay, continued.SettleThrough, CancellationToken.None));
        Assert.Contains("\"chainAfter\"", (await RowAsync(jobId))!.Payload, StringComparison.Ordinal);

        // Нічний розклад ставить рік документа знову (новий батько) — злиття в ланцюжок між кроками.
        var again = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            target, new { document.DocumentId, PeriodKey = (int?)null, FanOutParentJobId = "parent-new" }, CancellationToken.None);
        Assert.Equal(jobId, again);

        var row = await RowAsync(jobId);
        Assert.DoesNotContain("chainAfter", row!.Payload, StringComparison.Ordinal);
        Assert.Equal("parent-new", row.FanOutParentJobId);

        // Наступний крок — знову січень.
        step = (await ClaimAsync(host, JobLanes.Recalc))!;
        continued = await Assert.ThrowsAsync<JobContinuedException>(() => RunStepAsync(builder, scheduler, host.Queue, step));
        Assert.Equal(january.Value, continued.SettleThrough);
    }

    /// <summary>
    /// ✎ Огляд M2: вимір ПРД-13 ланцюжка — сума часу РОБОТИ кроків (<c>ChainElapsedMs</c>), а не годинник від першого кроку
    /// з очікуваннями в черзі між кроками (хибне «понад бюджет» і Degraded на /health/ready).
    /// </summary>
    /// <remarks>МУТАЦІЯ: виміряти <c>clock.UtcNow - (ChainStartedAt)</c> чи без <c>ChainElapsedMs</c> — червоний.</remarks>
    [Fact]
    public async Task Вимір_бюджету_ланцюжка_сумує_роботу_кроків_без_очікувань_між_ними()
    {
        var (builder, document) = await ArrangeAsync();
        await using var host = NewHost();
        var scheduler = Scheduler(host);

        // Останній крок (березень) ланцюжка, що вже відпрацював 5 хв (сума кроків), а від старту минули години черги.
        var jobId = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            RecalculateDocumentHandler.YearTargetOf(document.DocumentId),
            new RecalculationRequest(
                document.ProjectId, document.DocumentId, null, null, ChainAfter: February.Value,
                ChainDone: [document.PeriodKey.Value, February.Value], ChainElapsedMs: 5 * 60 * 1000),
            CancellationToken.None);
        var step = (await ClaimAsync(host, JobLanes.Recalc))!;
        Assert.Equal(jobId, step.Claim.JobId);

        var messages = new List<string?>();
        using var budget = new RecalculationBudgetMonitor(
            new RecalculationBudgetOptions(TimeSpan.FromMinutes(10), "Database"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RecalculationBudgetMonitor>.Instance);
        await RunStepAsync(builder, scheduler, host.Queue, step, budget, messages);
        Assert.DoesNotContain(messages, m => m?.Contains(RecalculationBudgetMonitor.OverBudgetKey, StringComparison.Ordinal) == true);

        // Контроль: сума кроків понад поріг — сигнал є.
        await host.Queue.CompleteAsync(step.Claim, CancellationToken.None);
        await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            RecalculateDocumentHandler.YearTargetOf(document.DocumentId),
            new RecalculationRequest(
                document.ProjectId, document.DocumentId, null, null, ChainAfter: February.Value,
                ChainDone: [document.PeriodKey.Value, February.Value], ChainElapsedMs: 11 * 60 * 1000),
            CancellationToken.None);
        step = (await ClaimAsync(host, JobLanes.Recalc))!;
        messages.Clear();
        await RunStepAsync(builder, scheduler, host.Queue, step, budget, messages);
        Assert.Contains(messages, m => m?.Contains(RecalculationBudgetMonitor.OverBudgetKey, StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// ✎ Огляд M3: читач результатів ПІЗНІШОГО місяця ланцюжка отримує прохід одразу на своєму кроці (стоїть у FIFO за
    /// ланцюжком) — і лишається в черзі, навіть якщо ланцюжок скасують посеред року.
    /// </summary>
    /// <remarks>МУТАЦІЯ: повернути перенос читачів до останнього кроку — після скасування проходу немає, червоний.</remarks>
    [Fact]
    public async Task Прохід_читача_пізнішого_місяця_ставиться_на_кроці_і_переживає_скасування_ланцюжка()
    {
        var (builder, document) = await ArrangeAsync();
        var january = document.PeriodKey;
        await using var host = NewHost();
        var scheduler = Scheduler(host);
        var jobId = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            RecalculateDocumentHandler.YearTargetOf(document.DocumentId),
            new { document.DocumentId, PeriodKey = (int?)null }, CancellationToken.None);

        var step = (await ClaimAsync(host, JobLanes.Recalc))!;
        await Assert.ThrowsAsync<JobContinuedException>(
            () => RunStepAsync(builder, scheduler, host.Queue, step, readsOwner: (january, March)));

        var passTarget = $"{nameof(IRecalculationJob)}~{RecalculateDocumentHandler.TargetOf(document.DocumentId, january)}";
        await using (var db = Sql.CreateContext())
        {
            var pass = await db.JobProgresses.AsNoTracking().SingleAsync(p => p.TargetKey == passTarget && p.State == "Queued");
            Assert.Contains("\"foreignOwnerPass\":true", pass.Payload, StringComparison.Ordinal);
        }

        // Ланцюжок скасовано посеред року (між кроками) — прохід січня лишається.
        await ExecAsync("UPDATE itg.JobProgress SET [State] = 'Cancelled' WHERE JobId = @id;", jobId);
        await using (var db = Sql.CreateContext())
        {
            Assert.Equal(1, await db.JobProgresses.CountAsync(p => p.TargetKey == passTarget && p.State == "Queued"));
        }
    }

    [Fact]
    public async Task Межа_тривалості_діє_на_крок_а_не_на_весь_ланцюжок()
    {
        var probe = new ChainProbe();
        var services = new ServiceCollection();
        services.AddScoped(_ => Sql.CreateContext());
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IJobQueue>(sp => new DbJobQueue(sp.GetRequiredService<EcrDbContext>(), sp.GetRequiredService<IClock>()));
        services.AddScoped<IJobProgressStore>(sp => new JobProgressStore(sp.GetRequiredService<EcrDbContext>()));
        services.AddScoped<JobLeaseContext>();
        services.AddScoped<IJobLeaseContext>(sp => sp.GetRequiredService<JobLeaseContext>());
        services.AddSingleton<JobQueueSignal>();
        services.AddSingleton(probe);
        services.AddScoped<ChainStepJob>();
        await using var provider = services.BuildServiceProvider();

        // 12 кроків по 150 мс — 1,8 с роботи; межа 1 с. Одна задача без кроків упала б за межею.
        var worker = new JobWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new JobWorkerOptions
            {
                Lanes = [JobLanes.Default],
                Role = JobProgressStore.CurrentRole,
                MaxConcurrency = 1,
                PollInterval = TimeSpan.FromMilliseconds(50),
                RenewInterval = TimeSpan.FromMilliseconds(200),
                MaxDuration = TimeSpan.FromSeconds(1),
            },
            provider.GetRequiredService<JobQueueSignal>(),
            NullLogger<JobWorker>.Instance);

        string jobId;
        await using (var host = NewHost())
        {
            jobId = (await host.Queue.EnqueueAsync(
                new JobEnqueueRequest(
                    typeof(ChainStepJob).FullName!, JobLanes.Default, "{\"step\":1,\"total\":12,\"ms\":150}"),
                CancellationToken.None)).JobId;
        }

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Domain.Entities.Integration.JobProgress? row;
            while ((row = await RowAsync(jobId))?.State is not ("Succeeded" or "Failed" or "Cancelled"))
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"ланцюжок не закінчився: {row?.State} {row?.Payload}");
                await Task.Delay(50);
            }

            Assert.Equal("Succeeded", row!.State);
            Assert.Equal(12, probe.Steps);
            Assert.True(probe.Work >= TimeSpan.FromSeconds(1.2), $"роботи {probe.Work}: менше за межу — доказ не доказ");
            Assert.Equal(1, row.Attempt);
            Assert.Equal("{\"step\":12,\"total\":12,\"ms\":150}", row.Payload);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    /// <summary>
    /// ✎ L1b / D3 (стенд Land 21.11): шкала ланцюжка відома з ПЕРШОГО звіту кроку — жодного «Період k з k» на старті кроку
    /// і жодного стрибка відсотка назад (на стенді 42 → 75 → 48: <c>Total</c> починався з <c>Done + 1</c>).
    /// </summary>
    /// <remarks>
    /// МУТАЦІЯ: не класти <c>ChainTotal</c> у payload наступного кроку (чи ігнорувати його в <c>ChainProgress</c>) — червоний:
    /// перший звіт кроку 2 — «2 з 2», 50 %, далі 33 %.
    /// </remarks>
    [Fact]
    public async Task Шкала_ланцюжка_відома_з_першого_звіту_кроку_і_не_стрибає_назад()
    {
        var (builder, document) = await ArrangeAsync();
        await using var host = NewHost();
        var scheduler = Scheduler(host);
        var jobId = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            RecalculateDocumentHandler.YearTargetOf(document.DocumentId),
            new { document.DocumentId, PeriodKey = (int?)null }, CancellationToken.None);

        var reports = new List<(int Percent, string? Message)>();
        var stepReports = new List<int>();
        for (var k = 1; k <= 3; k++)
        {
            var step = (await ClaimAsync(host, JobLanes.Recalc))!;
            Assert.Equal(jobId, step.Claim.JobId);
            var before = reports.Count;
            if (k < 3)
            {
                var continued = await Assert.ThrowsAsync<JobContinuedException>(
                    () => RunStepAsync(builder, scheduler, host.Queue, step, reports: reports));
                Assert.Equal(3, JsonSerializer.Deserialize<RecalculationRequest>(continued.PayloadJson, Web)!.ChainTotal);
                Assert.True(await host.Queue.ContinueAsync(
                    step.Claim, continued.PayloadJson, continued.Delay, continued.SettleThrough, CancellationToken.None));
            }
            else
            {
                await RunStepAsync(builder, scheduler, host.Queue, step, reports: reports);
            }

            stepReports.Add(reports.Count - before);

            // Кроки 2 і 3: ПЕРШИЙ же звіт — «період k з 3», відсоток не нижче частки зробленого.
            if (k > 1)
            {
                var (percent, message) = reports[before];
                Assert.True(JobProgressMessageCodec.TryDecode(message, out var first), message);
                Assert.Equal(
                    ("jobs.chainStep", k.ToString(System.Globalization.CultureInfo.InvariantCulture), "3"),
                    (first.Key, first.Params?["step"], first.Params?["total"]));
                Assert.Equal((k - 1) * 100 / 3, percent);
            }
        }

        Assert.All(stepReports, n => Assert.True(n > 0));

        // Жоден звіт не каже «з N», крім «з 3»; відсоток рядка задачі не спадає.
        foreach (var (_, message) in reports)
        {
            if (JobProgressMessageCodec.TryDecode(message, out var envelope) && envelope.Key == "jobs.chainStep")
            {
                Assert.Equal("3", envelope.Params?["total"]);
            }
        }

        var percents = reports.Select(r => r.Percent).ToList();
        Assert.Equal(percents.Order(), percents);
    }

    /// <summary>
    /// ✎ L1b / D4 (стенд Land 21.11): скасування задачі посеред кроку закриває його прогін одразу — <c>Failed</c> з причиною
    /// «задачу скасовано», а не лишає <c>Running</c> до <c>AbandonedWorkSweeper</c> (2 год). Скасування проходить нагору як є.
    /// </summary>
    /// <remarks>
    /// Обидві форми скасування, що бачить задача: <see cref="OperationCanceledException"/> і виняток драйвера посеред запиту
    /// (не OCE) при скасованому токені. МУТАЦІЯ: прибрати гілку <c>catch … when (ct.IsCancellationRequested)</c> у
    /// <c>RecalculationJob.ExecuteAsync</c> — червоні обидва випадки (прогін <c>Running</c>).
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Скасування_кроку_закриває_його_прогін_з_причиною(bool driverError)
    {
        var (builder, document) = await ArrangeAsync();
        await using var host = NewHost();
        var scheduler = Scheduler(host);
        await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            RecalculateDocumentHandler.YearTargetOf(document.DocumentId),
            new { document.DocumentId, PeriodKey = (int?)null }, CancellationToken.None);
        var step = (await ClaimAsync(host, JobLanes.Recalc))!;

        using var cancel = new CancellationTokenSource();
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => RunStepAsync(
            builder, scheduler, host.Queue, step, runner: new CancellingRunner(cancel, driverError), ct: cancel.Token));
        Assert.Equal(driverError, thrown is InvalidOperationException);

        await using var db = Sql.CreateContext();
        var runs = await db.CalculationRuns.AsNoTracking().Where(r => r.DocumentId == document.DocumentId).ToListAsync();
        var run = Assert.Single(runs);
        Assert.Equal(("Failed", document.PeriodKey.Value), (run.Status, run.PeriodKey!.Value));
        Assert.NotNull(run.FinishedAt);
        Assert.True(JobProgressMessageCodec.TryDecode(run.ErrorMessage, out var reason), run.ErrorMessage);
        Assert.Equal(RecalculationJob.CancelledRunKey, reason.Key);
    }

    /// <summary>
    /// ✎ L1b / D5 (стенд Land 21.11): прибирання прогону, який крок зняв з актуальності (D-560), іде ПІСЛЯ лока документа
    /// (+47–111 с на крок під локом, правки й ручні документа чекали): без оренди воркера — одразу після лока; у черзі в базі
    /// — після фіксації рядка задачі воркером (до неї документ для claim зайнятий). Прибираються лише передані прогони.
    /// </summary>
    /// <remarks>
    /// Проба — журнал сховища «прогін N прибрано» (пишеться посеред прибирання, після кожного прогону): у цю мить вона пробує
    /// взяти лок документа з окремого з'єднання без очікування. МУТАЦІЯ: <c>purgeLater: null</c> у <c>CompleteAsync</c>
    /// задачі (прибирання знову всередині лока) — червоні обидва випадки.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Прибирання_замінених_прогонів_після_лока_документа(bool queueWorker)
    {
        var (builder, document) = await ArrangeAsync();
        var january = document.PeriodKey;
        await using var host = NewHost();
        var scheduler = Scheduler(host);
        var target = RecalculateDocumentHandler.TargetOf(document.DocumentId, january);

        // Перший прогін січня — актуальний.
        await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            target, new { document.DocumentId, PeriodKey = january.Value }, CancellationToken.None);
        var first = (await ClaimAsync(host, JobLanes.Recalc))!;
        await RunStepAsync(builder, scheduler, host.Queue, first);
        Assert.True(await host.Queue.CompleteAsync(first.Claim, CancellationToken.None));
        long replacedRun;
        await using (var db = Sql.CreateContext())
        {
            replacedRun = await db.CalculationRuns.AsNoTracking()
                .Where(r => r.DocumentId == document.DocumentId && r.Status == CalculationRun.CurrentStatus)
                .Select(r => r.Id)
                .SingleAsync();
        }

        // Другий прогін січня замінює перший — і прибирає його.
        await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            target, new { document.DocumentId, PeriodKey = january.Value }, CancellationToken.None);
        var second = (await ClaimAsync(host, JobLanes.Recalc))!;
        var probe = new DocumentLockProbe(Sql.ConnectionString, document.DocumentId);
        JobLeaseContext? worker = null;
        if (queueWorker)
        {
            worker = new JobLeaseContext();
            worker.Bind(second.Claim);
        }

        await RunStepAsync(
            builder, scheduler, host.Queue, second, purgeProbe: probe, lease: worker,
            afterJobInScope: worker is null ? null : async () =>
            {
                // Черга в базі: задача прибирання не робила — доручила воркеру; він виконає після фіксації рядка.
                Assert.Empty(probe.LockFreeAtPurge);
                Assert.True(await RunExistsAsync(replacedRun));
                Assert.True(await host.Queue.CompleteAsync(second.Claim, CancellationToken.None));
                var work = Assert.Single(worker.TakeAfterSettle());
                await work(CancellationToken.None);
            });

        Assert.NotEmpty(probe.LockFreeAtPurge);
        Assert.All(probe.LockFreeAtPurge, Assert.True);
        Assert.False(await RunExistsAsync(replacedRun));
    }

    /// <summary>
    /// ✎ L1b / D5: справжній <see cref="JobWorker"/> виконує доручену задачею роботу (<see cref="IJobLeaseContext.TryRunAfterSettle"/>)
    /// ПІСЛЯ фіксації її рядка — коли той уже не <c>Running</c> (документ вільний для claim) — і в scope задачі.
    /// </summary>
    /// <remarks>МУТАЦІЯ: прибрати виклик <c>RunAfterSettleAsync</c> у <c>JobWorker.ExecuteClaimedAsync</c> — червоний.</remarks>
    [Fact]
    public async Task Воркер_виконує_доручене_після_фіксації_рядка_задачі()
    {
        var probe = new AfterSettleProbe(Sql);
        var services = new ServiceCollection();
        services.AddScoped(_ => Sql.CreateContext());
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IJobQueue>(sp => new DbJobQueue(sp.GetRequiredService<EcrDbContext>(), sp.GetRequiredService<IClock>()));
        services.AddScoped<IJobProgressStore>(sp => new JobProgressStore(sp.GetRequiredService<EcrDbContext>()));
        services.AddScoped<JobLeaseContext>();
        services.AddScoped<IJobLeaseContext>(sp => sp.GetRequiredService<JobLeaseContext>());
        services.AddSingleton<JobQueueSignal>();
        services.AddSingleton(probe);
        services.AddScoped<AfterSettleProbeJob>();
        await using var provider = services.BuildServiceProvider();

        var worker = new JobWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new JobWorkerOptions
            {
                Lanes = [JobLanes.Default],
                Role = JobProgressStore.CurrentRole,
                MaxConcurrency = 1,
                PollInterval = TimeSpan.FromMilliseconds(50),
            },
            provider.GetRequiredService<JobQueueSignal>(),
            NullLogger<JobWorker>.Instance);

        string jobId;
        await using (var host = NewHost())
        {
            jobId = (await host.Queue.EnqueueAsync(
                new JobEnqueueRequest(typeof(AfterSettleProbeJob).FullName!, JobLanes.Default, "{}"), CancellationToken.None)).JobId;
        }

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var state = await probe.Seen.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal((jobId, "Succeeded"), state);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    private async Task<bool> RunExistsAsync(long runId)
    {
        await using var db = Sql.CreateContext();
        return await db.CalculationRuns.AsNoTracking().AnyAsync(r => r.Id == runId);
    }

    /// <summary>
    /// Журнал сховища, що на рядку «прогін N прибрано» пробує взяти лок перерахунку документа з окремого з'єднання без
    /// очікування: <c>true</c> — лок вільний (прибирання поза критичною секцією).
    /// </summary>
    private sealed class DocumentLockProbe(string connectionString, long documentId)
        : Microsoft.Extensions.Logging.ILogger<CalculationResultStore>
    {
        public List<bool> LockFreeAtPurge { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!formatter(state, exception).Contains("прибрано", StringComparison.Ordinal))
            {
                return;
            }

            using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DECLARE @r int;
                EXEC @r = sys.sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;
                IF @r >= 0 EXEC sys.sp_releaseapplock @Resource = @res, @LockOwner = 'Session';
                SELECT @r;
                """;
            command.Parameters.AddWithValue("@res", RecalculationDocumentLock.Resource(documentId));
            LockFreeAtPurge.Add(Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) >= 0);
        }
    }

    private async Task RunStepAsync(
        TestDocumentBuilder builder, IBackgroundJobScheduler scheduler, IJobQueue queue, ClaimedJob step,
        RecalculationBudgetMonitor? budget = null, List<string?>? messages = null, (PeriodKey Reader, PeriodKey Owner)? readsOwner = null,
        List<(int Percent, string? Message)>? reports = null, ICalculationRunner? runner = null,
        Microsoft.Extensions.Logging.ILogger<CalculationResultStore>? purgeProbe = null, IJobLeaseContext? lease = null,
        Func<Task>? afterJobInScope = null, CancellationToken ct = default)
    {
        await using var db = builder.CreateContext();
        var clock = new TestClock(Now);
        var job = new RecalculationJob(
            db,
            runner ?? new WritingRunner(builder, _methodologyVersionId, _unitId, readsOwner),
            RunHandler(db, clock, purgeProbe),
            FormulaService(),
            clock,
            jobs: scheduler,
            budget: budget,
            lease: lease ?? new FixedLease(step.Claim),
            queue: queue);

        await job.ExecuteAsync(step.PayloadJson, new RecordingProgress(messages, reports), ct);

        // Як JobWorker: робота «після фіксації» — у scope задачі (той самий контекст), після її повернення.
        if (afterJobInScope is not null)
        {
            await afterJobInScope();
        }
    }

    private async Task<string> EnqueueEditAsync(long documentId)
    {
        await using var host = NewHost();
        return (await host.Queue.EnqueueAsync(
                new JobEnqueueRequest(
                    JobPayloadMerge.MergeableJobCode, JobLanes.Interactive, $"{{\"documentId\":{documentId},\"cells\":[]}}",
                    DocumentId: documentId),
                CancellationToken.None))
            .JobId;
    }

    private static Task<ClaimedJob?> ClaimAsync(Host host, string lane)
        => host.Queue.ClaimAsync([lane], "test/chain", JobQueueLimits.DefaultLease, CancellationToken.None);

    private async Task<List<int>> CurrentPeriodsAsync(long documentId)
    {
        await using var db = Sql.CreateContext();
        return await db.CalculationRuns.AsNoTracking()
            .Where(r => r.DocumentId == documentId && r.Status == CalculationRun.CurrentStatus && r.PeriodKey != null)
            .OrderBy(r => r.PeriodKey)
            .Select(r => r.PeriodKey!.Value)
            .ToListAsync();
    }

    private static void AssertParked(Domain.Entities.Integration.JobProgress? row, string covererJobId)
    {
        Assert.Equal("Queued", row?.State);
        Assert.Equal(9999, row!.AvailableAt!.Value.Year);
        Assert.True(JobProgressMessageCodec.TryDecode(row.Message, out var envelope), row.Message);
        Assert.Equal((IJobQueue.ParkedMessageKey, covererJobId), (envelope.Key, envelope.Params?["jobId"]));
    }

    private static DbBackgroundJobScheduler Scheduler(Host host)
        => new(host.Queue, new QuartzJobScheduler(null, new JobProgressStore(host.Db), new SystemClock()), new JobQueueSignal());

    /// <summary>Документ із січнем (будівник), лютим і березнем, по екземпляру таблиці в кожному, і прив'язка методології.</summary>
    private async Task<(TestDocumentBuilder Builder, TestDocument Document)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(Sql.ConnectionString);
        var document = await builder.BuildAsync();

        await using var db = builder.CreateContext();
        var loader = new BulkCellLoader(Sql.ConnectionString, 1000);
        foreach (var key in new[] { February, March })
        {
            db.Periods.Add(new Period(
                document.ProjectId, key, (byte)key.Sequence,
                new DateOnly(key.Year, key.Sequence, 1),
                new DateOnly(key.Year, key.Sequence, DateTime.DaysInMonth(key.Year, key.Sequence))));
            var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
            db.TableInstances.Add(new TableInstance(key, instanceId, document.DocumentId, document.TableDefId, Now));
        }

        var tag = Guid.NewGuid().ToString("N")[..8];
        var methodology = new Methodology(
            EcrCode.Create($"PPC_{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        db.CalculationBindings.Add(new CalculationBinding(
            document.TableDefId, document.ColumnDefIds[1], methodology.Id, "tons", "{}"));
        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        _methodologyVersionId = version.Id;
        _unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();

        return (builder, document);
    }

    private static RunCalculationHandler RunHandler(
        EcrDbContext db, TestClock clock, Microsoft.Extensions.Logging.ILogger<CalculationResultStore>? purgeProbe = null)
        => new(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            purgeProbe is null
                ? new CalculationResultStore(db, clock)
                : new CalculationResultStore(
                    db, clock, purgeOptions: SupersededPurgeOptions.Off with { Enabled = true }, logger: purgeProbe),
            Substitute.For<IBackgroundJobScheduler>(),
            new UnitOfWork(db),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(),
            clock,
            Substitute.For<IAuditWriter>());

    private static RecalculationService FormulaService()
    {
        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableInstanceRef>>([]));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var header = Substitute.For<IDocumentHeaderStore>();
        header.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        return new(
            Substitute.For<ICellStore>(),
            rows,
            Substitute.For<IPeriodStore>(),
            Substitute.For<IMetadataCache>(),
            Substitute.For<ITemplateVersionStore>(),
            Substitute.For<IFormulaEngine>(),
            units,
            Substitute.For<IRegistryStore>(),
            header,
            Substitute.For<IAuditWriter>(),
            new TestClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Substitute.For<IUnitOfWork>(), Substitute.For<ISheetEditGate>());
    }

    /// <summary>Фейк оркестратора: пише вихід через справжнє сховище, як <c>CalculationOutputWriter</c>.</summary>
    private sealed class WritingRunner(
        TestDocumentBuilder builder, int methodologyVersionId, int unitId, (PeriodKey Reader, PeriodKey Owner)? readsOwner = null)
        : ICalculationRunner
    {
        public async Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
        {
            if (bindings.Count == 0)
            {
                return new ModuleProfile();
            }

            var profile = new ModuleProfile();
            if (readsOwner is { } read && read.Reader == periodKey)
            {
                profile.RecordForeignOwnerReads([read.Owner.Value]);
            }

            await using var db = builder.CreateContext();
            var store = new CalculationResultStore(db, new TestClock(Now));
            await store.WriteResultsAsync(
                calculationRunId,
                [
                    new CalculationOutput(documentId, "R-1",
                        [new CalculationOutputValue(methodologyVersionId, null, "tons", periodKey.Sequence, unitId)], []),
                ],
                ct);
            await db.SaveChangesAsync(ct);
            return profile;
        }
    }

    /// <summary>Фейк оркестратора: посеред методологій «людина натискає Скасувати» — токен задачі скасовано.</summary>
    /// <param name="cancel">Токен задачі.</param>
    /// <param name="driverError">Виняток драйвера посеред запиту (не OCE), як SqlException «Operation cancelled by user».</param>
    private sealed class CancellingRunner(CancellationTokenSource cancel, bool driverError) : ICalculationRunner
    {
        public async Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
        {
            await cancel.CancelAsync();
            if (driverError)
            {
                throw new InvalidOperationException("Operation cancelled by user.");
            }

            ct.ThrowIfCancellationRequested();
            return new ModuleProfile();
        }
    }

    private sealed class FixedLease(JobClaimToken claim) : IJobLeaseContext
    {
        public JobClaimToken? Current { get; } = claim;
    }

    private sealed class RecordingProgress(List<string?>? messages, List<(int Percent, string? Message)>? reports = null)
        : IJobProgress
    {
        public Task ReportAsync(int percent, string? message, CancellationToken ct)
        {
            messages?.Add(message);
            reports?.Add((percent, message));
            return Task.CompletedTask;
        }
    }
}

/// <summary>Що побачила робота «після фіксації» <see cref="AfterSettleProbeJob"/>: задача і стан її рядка в цю мить.</summary>
public sealed class AfterSettleProbe(SqlServerFixture sql)
{
    /// <summary>(JobId, State) рядка задачі, прочитані роботою після фіксації.</summary>
    public TaskCompletionSource<(string JobId, string State)> Seen { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal async Task RecordAsync(string jobId)
    {
        await using var db = sql.CreateContext();
        var state = await db.JobProgresses.AsNoTracking().Where(p => p.JobId == jobId).Select(p => p.State).SingleAsync();
        Seen.TrySetResult((jobId, state));
    }
}

/// <summary>Задача, що доручає воркеру роботу після фіксації свого рядка (L1b / D5).</summary>
public sealed class AfterSettleProbeJob(IJobLeaseContext lease, AfterSettleProbe probe) : IBackgroundJob
{
    /// <inheritdoc />
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        var jobId = lease.Current!.JobId;
        Assert.True(lease.TryRunAfterSettle(_ => probe.RecordAsync(jobId)));
        return Task.CompletedTask;
    }
}

/// <summary>Лічильник кроків <see cref="ChainStepJob"/>.</summary>
public sealed class ChainProbe
{
    private long workTicks;
    private int steps;

    /// <summary>Скільки кроків виконано.</summary>
    public int Steps => Volatile.Read(ref steps);

    /// <summary>Сумарний час роботи кроків.</summary>
    public TimeSpan Work => TimeSpan.FromTicks(Interlocked.Read(ref workTicks));

    internal void Record(TimeSpan work)
    {
        Interlocked.Add(ref workTicks, work.Ticks);
        Interlocked.Increment(ref steps);
    }
}

/// <summary>
/// Задача-ланцюжок: крок «працює» <c>ms</c> мілісекунд (стежачи за токеном) і продовжується наступним кроком, доки
/// <c>step &lt; total</c> (<see cref="JobContinuedException"/>).
/// </summary>
public sealed class ChainStepJob(ChainProbe probe) : IBackgroundJob
{
    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse((string)payload!);
        var step = doc.RootElement.GetProperty("step").GetInt32();
        var total = doc.RootElement.GetProperty("total").GetInt32();
        var ms = doc.RootElement.GetProperty("ms").GetInt32();

        var started = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(ms, ct).ConfigureAwait(false);
        probe.Record(started.Elapsed);

        if (step < total)
        {
            throw new JobContinuedException($"{{\"step\":{step + 1},\"total\":{total},\"ms\":{ms}}}", TimeSpan.Zero, step);
        }
    }
}
