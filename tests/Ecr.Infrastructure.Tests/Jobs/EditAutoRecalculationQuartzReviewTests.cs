// tests/Ecr.Infrastructure.Tests/Jobs/EditAutoRecalculationQuartzReviewTests.cs
using System.Collections.Specialized;
using System.Text.Json.Nodes;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using Quartz.Spi;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ D-577 (огляд M4, L4, H2 (a), M5) у режимі Quartz (типовий на live): відкладений автоперерахунок після правки не тримає
/// пізніші задачі документа (FIFO), негайна постановка в нього бере ранній старт, натискання знімає його, злиття зливає поля
/// payload як черга в базі, правка під час прогону дає рівно одну наступну задачу, а залежні відкладаються на вікно каскаду.
/// </summary>
/// <remarks>
/// ⚠ Обмеження (задокументовано в D-577): черга Quartz — у пам'яті процесу; відкладена задача, що чекає вікна, губиться на
/// рестарті (як і будь-яка задача Quartz до старту). Застарілість лишається видимою в чипі, «Перерахувати» — доступне.
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait("Requirement", "D-577")]
public sealed class EditAutoRecalculationQuartzReviewTests : IAsyncLifetime
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(30);

    private IScheduler? quartz;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (quartz is not null)
        {
            await quartz.Shutdown(waitForJobsToComplete: false);
        }
    }

    /// <summary>✎ Огляд L4: негайна постановка (хук джерела, збір), злита в задачу, що чекає вікна відкладення, — старт одразу.</summary>
    /// <remarks>Мутація (локально): прибрати виклик <c>StartNowIfDelayedAsync</c> — червоний (старт через 20 с).</remarks>
    [Fact]
    public async Task L4_негайна_постановка_в_відкладену_бере_ранній_старт()
    {
        var jobs = await StandbyAsync();
        var document = NewDocument();
        var target = CalculationTrigger.AutoTargetOf(document, new PeriodKey(202604));

        var id = await jobs.EnqueueCoalescedDelayedAsync<IRecalculationJob>(
            target, Auto(document), CalculationTrigger.EditDebounce, CancellationToken.None);
        var delayed = Assert.Single(await quartz!.GetTriggersOfJob(new JobKey(id)));
        Assert.True(delayed.GetNextFireTimeUtc() > SystemTime.UtcNow().AddSeconds(10));

        Assert.Equal(id, await jobs.EnqueueCoalescedAsync<IRecalculationJob>(target, Auto(document), CancellationToken.None));

        var now = Assert.Single(await quartz.GetTriggersOfJob(new JobKey(id)));
        Assert.True(now.GetNextFireTimeUtc() <= SystemTime.UtcNow().AddSeconds(1));
    }

    /// <summary>✎ Огляд M4: залежні (каскад) у Quartz — теж відкладені на вікно каскаду від першої постановки.</summary>
    [Fact]
    public async Task M4_залежні_відкладаються_на_вікно_каскаду()
    {
        var jobs = await StandbyAsync();
        var document = NewDocument();
        var before = SystemTime.UtcNow();

        var id = await jobs.EnqueueCoalescedDelayedAsync<IRecalculationJob>(
            CalculationTrigger.DependentsTargetOf(document, new PeriodKey(202604)), Auto(document),
            CalculationTrigger.CascadeDebounce, CancellationToken.None);

        var trigger = Assert.Single(await quartz!.GetTriggersOfJob(new JobKey(id)));
        Assert.InRange(
            trigger.StartTimeUtc,
            before + CalculationTrigger.CascadeDebounce - TimeSpan.FromSeconds(1),
            SystemTime.UtcNow() + CalculationTrigger.CascadeDebounce + TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// ✎ Огляд M4: раніша задача документа, що ще чекає вікна відкладення, не тримає пізнішу негайну (FIFO документа); задача,
    /// що чекає без відкладення, — тримає, як доти.
    /// </summary>
    /// <remarks>Мутація (локально): у <c>QuartzDocumentFifo.EarlierPendingAsync</c> прибрати пропуск вікна — червоний перший крок.</remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task M4_відкладена_раніша_задача_не_тримає_пізнішу(bool delayedFirst)
    {
        var jobs = await StandbyAsync();
        var document = NewDocument();

        var first = delayedFirst
            ? await jobs.EnqueueCoalescedDelayedAsync<IRecalculationJob>(
                CalculationTrigger.AutoTargetOf(document, new PeriodKey(202604)), Auto(document),
                CalculationTrigger.EditDebounce, CancellationToken.None)
            : await jobs.EnqueueCoalescedAsync<IRecalculationJob>(
                CalculationTrigger.AutoTargetOf(document, new PeriodKey(202604)), Auto(document), CancellationToken.None);
        var later = await jobs.EnqueueCoalescedAsync<IRecalculationJob>(
            CalculationTrigger.DependentsTargetOf(document, new PeriodKey(202605)), Auto(document), CancellationToken.None);

        var detail = (await quartz!.GetJobDetail(new JobKey(later)))!;
        var blocking = await QuartzDocumentFifo.EarlierPendingAsync(quartz, detail, CancellationToken.None);
        Assert.Equal(delayedFirst ? null : first, blocking);
    }

    /// <summary>✎ Огляд H2 (a): натискання знімає автоперерахунок, що чекає (Quartz теж уміє <c>CancelQueuedAsync</c>).</summary>
    /// <remarks>Мутація (локально): видалити <c>QuartzJobScheduler.CancelQueuedAsync</c> (типова реалізація порту — false) — червоний.</remarks>
    [Fact]
    public async Task H2_Quartz_знімає_задачу_що_чекає()
    {
        var jobs = await StandbyAsync();
        var document = NewDocument();
        var target = CalculationTrigger.AutoTargetOf(document, new PeriodKey(202604));
        await jobs.EnqueueCoalescedDelayedAsync<IRecalculationJob>(
            target, Auto(document), CalculationTrigger.EditDebounce, CancellationToken.None);

        Assert.True(await ((IBackgroundJobScheduler)jobs).CancelQueuedAsync<IRecalculationJob>(target, "press", CancellationToken.None));
        Assert.Empty(await quartz!.GetJobKeys(GroupMatcher<JobKey>.AnyGroup()));
        Assert.False(await ((IBackgroundJobScheduler)jobs).CancelQueuedAsync<IRecalculationJob>(target, "press", CancellationToken.None));
    }

    /// <summary>
    /// ✎ Огляд M4/M5: злиття в задачу, що чекає, — як у базі: ланка джерел МЕНША, звільнення погоджувача — перетин,
    /// <c>skipIfFresh</c> — лише коли обидві.
    /// </summary>
    /// <remarks>Мутація (локально): у <c>CoalesceAsync</c> повернути <c>QuartzSourceMonths.Merge</c> — червоний (ланка 3).</remarks>
    [Fact]
    public async Task M4_M5_злиття_полів_payload_як_у_базі()
    {
        var jobs = await StandbyAsync();
        var document = NewDocument();
        var target = CalculationTrigger.AutoTargetOf(document, new PeriodKey(202604));

        var id = await jobs.EnqueueCoalescedDelayedAsync<IRecalculationJob>(
            target, Auto(document, hop: 3, approver: [7, 8], skipIfFresh: true), CalculationTrigger.EditDebounce,
            CancellationToken.None);
        Assert.Equal(id, await jobs.EnqueueCoalescedDelayedAsync<IRecalculationJob>(
            target, Auto(document, hop: 1, approver: [8], skipIfFresh: true), CalculationTrigger.EditDebounce,
            CancellationToken.None));

        var merged = JsonNode.Parse(Payload(id))!.AsObject();
        Assert.Equal(1, merged["sourceHop"]!.GetValue<int>());
        Assert.Equal([8], merged["approverSheets"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.True(merged["skipIfFresh"]!.GetValue<bool>());

        Assert.Equal(id, await jobs.EnqueueCoalescedAsync<IRecalculationJob>(target, Auto(document), CancellationToken.None));
        merged = JsonNode.Parse(Payload(id))!.AsObject();
        Assert.Equal(0, merged["sourceHop"]!.GetValue<int>());
        Assert.Null(merged["approverSheets"]);
        Assert.False(merged["skipIfFresh"]!.GetValue<bool>());
    }

    /// <summary>
    /// ✎ Огляд «тестові прогалини»: правки під час прогону автоперерахунку дають РІВНО одну наступну задачу (злиті), не по
    /// задачі на правку і не жодної.
    /// </summary>
    [Fact]
    public async Task Правки_під_час_прогону_дають_рівно_одну_наступну_задачу()
    {
        var job = new CountingRecalculationJob();
        var factory = Factory();
        quartz = await factory.GetScheduler();
        var services = new ServiceCollection();
        services.AddSingleton<Ecr.Domain.Abstractions.IClock, SystemClock>();
        services.AddSingleton<IRecalculationJob>(job);
        await using var provider = services.BuildServiceProvider();
        quartz.JobFactory = new AdapterFactory(provider);
        await quartz.Start();

        var jobs = new QuartzJobScheduler(factory, null, new SystemClock());
        var document = NewDocument();
        var target = CalculationTrigger.AutoTargetOf(document, new PeriodKey(202604));

        var running = await jobs.EnqueueCoalescedAsync<IRecalculationJob>(target, Auto(document), CancellationToken.None);
        await job.FirstStarted.Task.WaitAsync(Safety);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(running, await jobs.EnqueueCoalescedDelayedAsync<IRecalculationJob>(
                target, Auto(document), TimeSpan.FromMilliseconds(1), CancellationToken.None));
        }

        job.Release.TrySetResult();
        await job.SecondStarted.Task.WaitAsync(Safety);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(2, job.Runs);
    }

    private static long NewDocument() => Random.Shared.NextInt64(1_000_000_000, 9_000_000_000);

    private static object Auto(long document, int hop = 0, int[]? approver = null, bool skipIfFresh = false)
        => new
        {
            DocumentId = document,
            PeriodKey = 202604,
            RecalculateSubmitted = true,
            SourceHop = hop,
            SkipIfFresh = skipIfFresh,
            ApproverSheets = approver,
        };

    private static StdSchedulerFactory Factory()
        => new(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-d577-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "2",
        });

    private async Task<QuartzJobScheduler> StandbyAsync()
    {
        var factory = Factory();
        quartz = await factory.GetScheduler();
        return new QuartzJobScheduler(factory, null, new SystemClock());
    }

    private static string Payload(string jobId)
    {
        var merged = QuartzPayloadMerges.Take(jobId, out _);
        QuartzPayloadMerges.Reopen(jobId);
        return merged!;
    }

    private sealed class AdapterFactory(IServiceProvider provider) : IJobFactory
    {
        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler)
            => new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance);

        public void ReturnJob(IJob job)
        {
        }
    }

    private sealed class CountingRecalculationJob : IRecalculationJob
    {
        private int runs;

        public int Runs => Volatile.Read(ref runs);

        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                FirstStarted.TrySetResult();
                await Release.Task.WaitAsync(ct).ConfigureAwait(false);
                return;
            }

            SecondStarted.TrySetResult();
        }
    }
}
