// tests/Ecr.Infrastructure.Tests/Jobs/YearPressSkipsUnchangedMonthsTests.SourceCascade.cs
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ PERF-06 (аудит PERF1000, рішення людини 22.11 §6 п. 8; <c>D-566</c>): каскад від правки документа-джерела — ОДНА задача
/// на документ-споживач і рік (<see cref="CalculationTrigger.SourceCascadeTargetOf"/>), що рахує лише періоди каскаду
/// (<see cref="RecalculationRequest.CascadePeriods"/>) і пізніші місяці, що читають їхні обчислені числа; нова правка, що
/// злилася в ланцюжок, який уже йде, дописує свої періоди (і вже зроблені), не повертаючи його на перший місяць.
/// </summary>
/// <remarks>
/// Той самий стенд, що в тестах кнопки (справжні черга, ланцюжок кроків, сховище, перемикання актуальності; оркестратор — фейк).
/// <para>
/// МУТАЦІЇ (локально, не комітяться): (1) <c>CascadeMonthsAsync</c> без транзитивних читачів — червоний рядок <c>true</c>
/// <c>Каскад_без_ланцюжка…</c> (у ланцюжку тих самих читачів дописує ще й крок — <c>ExtendWithLaterReadersAsync</c>, друга
/// лінія; її разом із (1) ловить рядок <c>true</c> <c>Каскад_рахує_лише_свої_місяці…</c>); (2) <c>PlanChainAsync</c> без гілки каскаду (усі відкриті) — червоні обидва його рядки;
/// (3) <c>CoalescePayloadTemplate</c>: каскад у ланцюжок → <c>$.chainRecheck</c> (як натискання) — червоний
/// <c>Нова_правка_джерела_між_кроками…</c>; (4) <c>AbsorbChainSql</c>: каскад позаду → <c>$.chainRecheck</c> — червоний
/// <c>Правка_джерела_під_час_кроку…</c>; (5) <c>AbsorbChainSql</c> ELSE: <c>SET @payload = NULL</c> (як доти) — червоний
/// <c>Відкладений_перший_крок…</c>; (6) без гілки «каскад без ланцюжка» в <c>ExecuteAsync</c> — червоний <c>Каскад_без_ланцюжка…</c>.
/// </para>
/// </remarks>
public sealed partial class YearPressSkipsUnchangedMonthsTests
{
    /// <summary>
    /// Каскад місяця 3 рахує лише березень; пізніші місяці — лише коли вони читають ОБЧИСЛЕНІ числа березня (<c>[Period:#3]</c>).
    /// Правка лютого (вхід документа, не джерела) каскад не розширює — детектор кнопки тут не діє, підсумку «пропущено» немає.
    /// </summary>
    [Theory]
    [InlineData(false, new[] { 202603 })]
    [InlineData(true, new[] { 202603, 202604, 202605, 202606 })]
    public async Task Каскад_рахує_лише_свої_місяці_і_пізніших_читачів(bool crossRead, int[] expected)
    {
        var (builder, document) = await ArrangeAsync();
        if (crossRead)
        {
            await CrossReadAsync(document, kind: 5, offset: 3);
        }

        await CellChangeAsync(document, 202602, MonthColumn(document, 2), CellChangeOrigins.UserEdit, Baseline.AddMinutes(1));

        await using var host = NewHost();
        var jobId = await Scheduler(host).EnqueueCoalescedAsync<IRecalculationJob>(
            CalculationTrigger.SourceCascadeTargetOf(document.DocumentId, Year), CascadePayload(document, 202603),
            CancellationToken.None);
        var steps = new List<int>();
        var messages = new List<string?>();
        await StepAsync(builder, host, Press, steps, null, messages);

        // Читачі березня — у плані вже з першого кроку (шкала «з N» чесна з початку), а не лише коли крок їх допише.
        if (crossRead)
        {
            var planned = JsonSerializer.Deserialize<RecalculationRequest>((await RowAsync(jobId))!.Payload!, Web)!;
            Assert.Equal(expected.Skip(1), planned.ChainPlan!);
            Assert.Equal(expected.Length, planned.ChainTotal);
        }

        while (await StepAsync(builder, host, Press, steps, null, messages))
        {
        }

        Assert.Equal(expected, steps);
        Assert.Equal("Succeeded", (await RowAsync(jobId))!.State);
        Assert.DoesNotContain(messages, m => m?.Contains(RecalculationJob.SkippedUnchangedKey, StringComparison.Ordinal) == true);

        // Решта місяців лишила свій базовий прогін.
        Assert.Equal(Months, await CurrentPeriodsAsync(document.DocumentId));
        Assert.Equal(Months.Except(expected), await CurrentPeriodsStartedAtAsync(document.DocumentId, Baseline));
    }

    /// <summary>
    /// Нова правка джерела, що злилася в ланцюжок каскаду МІЖ кроками: березень (уже зроблений, але зі старим джерелом)
    /// дописується ще раз, травень іде за планом; січень, лютий, квітень, червень — ні; прогрес не скидається.
    /// </summary>
    [Fact]
    public async Task Нова_правка_джерела_між_кроками_дописує_зроблений_місяць_не_з_січня()
    {
        var (builder, document) = await ArrangeAsync();
        await using var host = NewHost();
        var scheduler = Scheduler(host);
        var target = CalculationTrigger.SourceCascadeTargetOf(document.DocumentId, Year);
        var jobId = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            target, CascadePayload(document, 202603, 202605), CancellationToken.None);
        var steps = new List<int>();
        var totals = new List<(int Done, int? Total)>();
        await StepAsync(builder, host, Press, steps, totals);
        Assert.Equal([202603], steps);

        Assert.Equal(jobId, await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            target, CascadePayload(document, 202603), CancellationToken.None));
        var merged = JsonSerializer.Deserialize<RecalculationRequest>((await RowAsync(jobId))!.Payload!, Web)!;
        Assert.Equal((202603, false), (merged.ChainAfter, merged.ChainRecheck));
        Assert.Equal([202603], merged.CascadeAppend!);
        Assert.Equal([202603, 202605], merged.CascadePeriods!);

        while (await StepAsync(builder, host, Press, steps, totals))
        {
        }

        Assert.Equal([202603, 202603, 202605], steps);
        Assert.Equal("Succeeded", (await RowAsync(jobId))!.State);
        Assert.Equal(totals.Select(t => t.Done).Order(), totals.Select(t => t.Done));
    }

    /// <summary>
    /// Правка джерела, що прийшла, поки крок ланцюжка каскаду ВИКОНУЄТЬСЯ (окремий рядок на ту саму ціль), поглинає ланцюжок
    /// з його прогресом: дописує свій місяць (лютий), решта плану (червень) лишається; січень і квітень — ні.
    /// </summary>
    [Fact]
    public async Task Правка_джерела_під_час_кроку_поглинає_ланцюжок_з_прогресом_і_дописує_місяць()
    {
        var (builder, document) = await ArrangeAsync();
        await using var host = NewHost();
        var scheduler = Scheduler(host);
        var target = CalculationTrigger.SourceCascadeTargetOf(document.DocumentId, Year);
        var first = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            target, CascadePayload(document, 202603, 202605, 202606), CancellationToken.None);

        var steps = new List<int>();
        await StepAsync(builder, host, Press, steps, null);
        var step = (await ClaimAsync(host))!;
        Assert.Equal(first, step.Claim.JobId);

        var behind = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            target, CascadePayload(document, 202602), CancellationToken.None);
        Assert.NotEqual(first, behind);

        await RunClaimedAsync(builder, host, step, Press, steps, null);
        Assert.Equal("Cancelled", (await RowAsync(first))!.State);

        var revived = JsonSerializer.Deserialize<RecalculationRequest>((await RowAsync(behind))!.Payload!, Web)!;
        Assert.Equal((202605, false), (revived.ChainAfter, revived.ChainRecheck));
        Assert.Equal([202602], revived.CascadeAppend!);

        while (await StepAsync(builder, host, Press, steps, null))
        {
        }

        Assert.Equal([202603, 202605, 202602, 202606], steps);
        Assert.Equal("Succeeded", (await RowAsync(behind))!.State);
    }

    /// <summary>
    /// Перший крок каскаду відкладено (лок документа зайнятий), а позаду вже стоїть нова правка: поглинута задача передає свої
    /// місяці тій, що позаду, — березень не губиться.
    /// </summary>
    [Fact]
    public async Task Відкладений_перший_крок_каскаду_передає_свої_місяці_задачі_позаду()
    {
        var (builder, document) = await ArrangeAsync();
        await using var host = NewHost();
        var scheduler = Scheduler(host);
        var target = CalculationTrigger.SourceCascadeTargetOf(document.DocumentId, Year);
        var first = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            target, CascadePayload(document, 202603), CancellationToken.None);
        var step = (await ClaimAsync(host))!;
        Assert.Equal(first, step.Claim.JobId);
        var behind = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            target, CascadePayload(document, 202605), CancellationToken.None);

        Assert.True(await host.Queue.DeferAsync(step.Claim, TimeSpan.Zero, CancellationToken.None));
        Assert.Equal("Cancelled", (await RowAsync(first))!.State);
        var absorbed = JsonSerializer.Deserialize<RecalculationRequest>((await RowAsync(behind))!.Payload!, Web)!;
        Assert.Equal([202603, 202605], absorbed.CascadePeriods!);

        var steps = new List<int>();
        while (await StepAsync(builder, host, Press, steps, null))
        {
        }

        Assert.Equal([202603, 202605], steps);
    }

    /// <summary>
    /// Без ланцюжка (немає оренди черги в базі) — той самий план однією задачею: лише березень, а з читачем його обчислених
    /// чисел (<c>[Period:#3]</c>) — і пізніші місяці (крок, що дописав би їх, тут не діє).
    /// </summary>
    [Theory]
    [InlineData(false, new[] { 202603 })]
    [InlineData(true, new[] { 202603, 202604, 202605, 202606 })]
    public async Task Каскад_без_ланцюжка_рахує_лише_свої_місяці(bool crossRead, int[] expected)
    {
        var (builder, document) = await ArrangeAsync();
        if (crossRead)
        {
            await CrossReadAsync(document, kind: 5, offset: 3);
        }

        await using var db = builder.CreateContext();
        var before = await MaxRunIdAsync();

        var job = NewJob(builder, db, Press, scheduler: null, lease: null, queue: null);
        await job.ExecuteAsync(
            JsonSerializer.Serialize(CascadePayload(document, 202603), Web), new Progress(null), CancellationToken.None);

        Assert.Equal(expected, await NewRunPeriodsAsync(document.DocumentId, before));
    }

    /// <summary>Payload каскаду — той самий, що ставить <see cref="CalculationTrigger.RequestSourceCascadeAsync"/>.</summary>
    private static object CascadePayload(TestDocument document, params int[] periods)
        => new
        {
            document.DocumentId,
            PeriodKey = (int?)null,
            Year,
            TriggeredByUserId = (int?)null,
            SheetDefId = (int?)null,
            RecalculateSubmitted = true,
            SourceHop = 1,
            CascadePeriods = periods,
        };
}
