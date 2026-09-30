// tests/Ecr.Infrastructure.Tests/Jobs/JobDeferralCapTests.cs
using System.Diagnostics;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Quartz.Impl;
using Quartz.Spi;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Стеля відкладень (борг O1): задача, чий лок документа не звільняється ніколи,
/// після <see cref="JobDeferral.MaxDeferral"/> від першого відкладення закривається
/// <c>Failed</c> з конвертом <c>jobs.deferralExhausted</c>, а не відкладається вічно.
/// </summary>
/// <remarks>
/// Лок тримає тест і не відпускає; час — <see cref="TestClock"/>, який тест
/// переводить на 29 хв (ще відкладення) і далі на 31 хв (Failed). Мутації — в
/// описі коміту: без перевірки стелі в <c>JobWorker</c> / <c>QuartzJobAdapter</c>
/// червоніють «Database» / «Quartz» (стан так і не Failed); момент першого
/// відкладення, що скидається на кожному триґері, — червоний «триґер несе».
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "ФВ-9.8")]
public sealed class JobDeferralCapTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Database_лок_вічно_зайнятий_після_стелі_Failed_з_конвертом_до_стелі_відкладається()
    {
        var documentId = LockedDocumentJob.NewDocumentId();
        await using var held = await HoldAsync(documentId);
        var clock = new TestClock(new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc));
        var t0 = clock.UtcNow;

        var probe = new WorkerProbe();
        await using var provider = BuildHost(clock, documentId, probe);
        string jobId;
        await using (var host = NewHost())
        {
            jobId = (await host.Queue.EnqueueAsync(
                new JobEnqueueRequest(typeof(LockedDocumentJob).FullName!, JobLanes.Default, "{\"n\":1}"),
                CancellationToken.None)).JobId;
        }

        var worker = new JobWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new JobWorkerOptions
            {
                Lanes = JobLanes.All,
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
            // Перше відкладення: момент — у payload, спроба не зарахована.
            var row = await WaitAsync(jobId, r => r.State == "Queued" && JobDeferral.SinceOf(r.Payload) is not null);
            Assert.Equal(t0, JobDeferral.SinceOf(row.Payload));
            Assert.Equal(0, row.Attempt ?? 0);

            // До стелі (29 хв) — відкладення як раніше, відлік не скидається.
            clock.Advance(TimeSpan.FromMinutes(29));
            await MakeAvailableAsync(jobId);
            row = await WaitAsync(jobId, r => r.State == "Queued" && r.UpdatedAt == clock.UtcNow);
            Assert.Equal(t0, JobDeferral.SinceOf(row.Payload));
            Assert.Equal(0, row.Attempt ?? 0);

            // ⛔ Понад стелю (31 хв) — Failed з конвертом, без ретраю.
            clock.Advance(TimeSpan.FromMinutes(2));
            await MakeAvailableAsync(jobId);
            row = await WaitAsync(jobId, r => r.State is "Failed" or "Succeeded" or "Cancelled");

            Assert.Equal("Failed", row.State);
            AssertExhausted(row.Message, documentId, "00:31:00");
            Assert.Equal(1, row.Attempt);
            Assert.Equal(0, row.ReclaimCount);

            // Єдиний слот вільний: наступна задача виконується, поки лок і далі тримають.
            string witness;
            await using (var host = NewHost())
            {
                witness = (await host.Queue.EnqueueAsync(
                    new JobEnqueueRequest(typeof(WorkerProbeJob).FullName!, JobLanes.Default, "{}"),
                    CancellationToken.None)).JobId;
            }

            await WaitAsync(witness, r => r.State == "Succeeded");
            Assert.Single(probe.Runs);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    [Fact]
    public async Task Quartz_лок_вічно_зайнятий_після_стелі_Failed_з_конвертом_потік_вільний()
    {
        var documentId = LockedDocumentJob.NewDocumentId();
        await using var held = await HoldAsync(documentId);

        // Годинник позаду реального: триґери відкладення (clock + 5 с) у минулому й
        // спрацьовують одразу — тест не чекає справжніх відступів.
        // Ціла секунда — UpdatedAt у базі округлюється до мілісекунд.
        var start = new SystemClock().UtcNow.AddHours(-2);
        var clock = new TestClock(new DateTime(start.Ticks - (start.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc));
        var t0 = clock.UtcNow;
        var witness = new WitnessJob();
        var jobId = $"deferral-cap-{Guid.NewGuid():N}";

        // ⚠ Триґер відкладення в минулому спрацьовує одразу, і Queued живе лише
        // мілісекунди між записом і наступним стартом (Running на BusyWait 1 с).
        // Опитування раз на 50 мс ловило це вікно не завжди: у CI 30 с поспіль
        // бачило лише Running (runs 36660742577, 36664182265). Шлюз тримає
        // єдиний потік Quartz одразу після запису Queued — стан стоїть, доки
        // тест його не перевірить і не посуне годинник.
        using var gate = new QueuedGate(jobId);

        var services = new ServiceCollection();
        services.AddScoped(_ => Sql.CreateContext());
        services.AddSingleton<IClock>(clock);
        services.AddScoped(sp => gate.Wrap(new JobProgressStore(sp.GetRequiredService<EcrDbContext>())));
        services.AddScoped(sp => new LockedDocumentJob(sp.GetRequiredService<EcrDbContext>(), documentId));
        services.AddSingleton(witness);
        await using var provider = services.BuildServiceProvider();

        var factory = new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-tests-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
        });
        var quartz = await factory.GetScheduler();
        quartz.JobFactory = new AdapterFactory(provider);

        try
        {
            await quartz.ScheduleJob(Detail(jobId, typeof(LockedDocumentJob)), TriggerBuilder.Create().StartNow().Build());
            await quartz.Start();

            // Відкладення показується як Queued, не Running.
            Assert.Equal(t0, await gate.NextAsync(Patience));
            var queued = await RowAsync(jobId);
            Assert.Equal("Queued", queued?.State);
            Assert.Equal(t0, queued?.UpdatedAt);

            clock.Advance(TimeSpan.FromMinutes(29));
            gate.Release();
            Assert.Equal(clock.UtcNow, await gate.NextAsync(Patience));
            queued = await RowAsync(jobId);
            Assert.Equal("Queued", queued?.State);
            Assert.Equal(clock.UtcNow, queued?.UpdatedAt);

            clock.Advance(TimeSpan.FromMinutes(2));
            gate.Open();
            var row = await WaitAsync(jobId, r => r.State is "Failed" or "Succeeded" or "Cancelled");

            Assert.Equal("Failed", row.State);
            AssertExhausted(row.Message, documentId, "00:31:00");
            Assert.Equal(1, row.Attempt);

            // Єдиний потік вільний; нових триґерів задачі не лишилось.
            await quartz.ScheduleJob(
                Detail($"witness-{Guid.NewGuid():N}", typeof(WitnessJob)), TriggerBuilder.Create().StartNow().Build());
            await witness.Executed.Task.WaitAsync(Patience);
            Assert.Empty(await quartz.GetTriggersOfJob(new JobKey(jobId)));
        }
        finally
        {
            gate.Open();
            await quartz.Shutdown(waitForJobsToComplete: false);
        }
    }

    [Fact]
    public async Task Quartz_триґер_відкладення_несе_момент_першого_відкладення_а_не_поточний()
    {
        var now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        var since = now.AddMinutes(-29);
        var progress = Substitute.For<IJobProgressStore>();
        var services = new ServiceCollection();
        services.AddSingleton(progress);
        services.AddSingleton<IClock>(new TestClock(now));
        services.AddScoped<DeferringJob>();
        await using var provider = services.BuildServiceProvider();

        var detail = Substitute.For<IJobDetail>();
        detail.Key.Returns(new JobKey("deferred-cap-1"));
        detail.JobDataMap.Returns(new JobDataMap
        {
            { QuartzJobScheduler.JobCodeKey, typeof(DeferringJob).FullName! },
            { QuartzJobScheduler.PayloadKey, "null" },
        });
        var trigger = Substitute.For<ITrigger>();
        trigger.JobDataMap.Returns(new JobDataMap
        {
            { QuartzJobScheduler.RetryAttemptKey, "0" },
            { JobDeferral.QuartzSinceKey, since.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        });
        var scheduler = Substitute.For<IScheduler>();
        var context = Substitute.For<IJobExecutionContext>();
        context.JobDetail.Returns(detail);
        context.Trigger.Returns(trigger);
        context.Scheduler.Returns(scheduler);

        await new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance).Execute(context);

        var scheduled = (ITrigger)Assert.Single(
            scheduler.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IScheduler.ScheduleJob)).GetArguments()[0]!;
        Assert.Equal(
            since.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            scheduled.JobDataMap.GetString(JobDeferral.QuartzSinceKey));
        await progress.Received(1).QueueAsync(
            "deferred-cap-1", typeof(DeferringJob).FullName!, now, Arg.Any<CancellationToken>(),
            Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<long?>());
    }

    [Fact]
    public async Task DbJobQueue_відкладення_ставить_момент_раз_ретрай_після_провалу_знімає()
    {
        var clock = new TestClock(new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc));
        var jobId = await EnqueueAsync();

        await using (var db = Sql.CreateContext())
        {
            var queue = new DbJobQueue(db, clock);
            var claim = (await queue.ClaimAsync(DefaultLane, "test/host", JobQueueLimits.DefaultLease, CancellationToken.None))!;
            Assert.True(await queue.DeferAsync(claim.Claim, TimeSpan.Zero, CancellationToken.None));
            Assert.Equal(clock.UtcNow, JobDeferral.SinceOf((await RowAsync(jobId))!.Payload));

            // Друге відкладення відліку не скидає.
            clock.Advance(TimeSpan.FromMinutes(10));
            claim = (await queue.ClaimAsync(DefaultLane, "test/host", JobQueueLimits.DefaultLease, CancellationToken.None))!;
            Assert.True(await queue.DeferAsync(claim.Claim, TimeSpan.Zero, CancellationToken.None));
            Assert.Equal(clock.UtcNow.AddMinutes(-10), JobDeferral.SinceOf((await RowAsync(jobId))!.Payload));

            // Задача взяла ресурс і впала: ретрай — нова серія без моменту відкладення.
            claim = (await queue.ClaimAsync(DefaultLane, "test/host", JobQueueLimits.DefaultLease, CancellationToken.None))!;
            Assert.True(await queue.RequeueAsync(claim.Claim, TimeSpan.Zero, CancellationToken.None));
            Assert.Null(JobDeferral.SinceOf((await RowAsync(jobId))!.Payload));
        }
    }

    // ── Світ ─────────────────────────────────────────────────────────────────

    private static void AssertExhausted(string? message, long documentId, string waited)
    {
        Assert.True(JobProgressMessageCodec.TryDecode(message, out var envelope), message);
        Assert.Equal(JobDeferral.ExhaustedKey, envelope.Key);
        Assert.Equal(RecalculationDocumentLock.Resource(documentId), envelope.Params!["resource"]);
        Assert.Equal(waited, envelope.Params["waited"]);
    }

    private async Task<SqlDistributedLock> HoldAsync(long documentId)
        => (await SqlDistributedLock.AcquireAsync(
            Sql.ConnectionString, RecalculationDocumentLock.Resource(documentId), TimeSpan.Zero, CancellationToken.None))!;

    private Task MakeAvailableAsync(string jobId)
        => ExecAsync(
            "UPDATE itg.JobProgress SET AvailableAt = DATEADD(second, -1, SYSUTCDATETIME()) WHERE JobId = @id AND [State] = 'Queued';",
            jobId);

    private ServiceProvider BuildHost(TestClock clock, long documentId, WorkerProbe probe)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Sql.CreateContext());
        services.AddSingleton<IClock>(clock);
        services.AddScoped<IJobQueue>(sp => new DbJobQueue(sp.GetRequiredService<EcrDbContext>(), sp.GetRequiredService<IClock>()));
        services.AddScoped<IJobProgressStore>(sp => new JobProgressStore(sp.GetRequiredService<EcrDbContext>()));
        services.AddScoped<JobLeaseContext>();
        services.AddScoped<IJobLeaseContext>(sp => sp.GetRequiredService<JobLeaseContext>());
        services.AddSingleton<JobQueueSignal>();
        services.AddSingleton(probe);
        services.AddScoped<WorkerProbeJob>();
        services.AddScoped(sp => new LockedDocumentJob(sp.GetRequiredService<EcrDbContext>(), documentId));
        return services.BuildServiceProvider();
    }

    private async Task<JobProgress> WaitAsync(string jobId, Func<JobProgress, bool> done)
    {
        var watch = Stopwatch.StartNew();

        while (true)
        {
            var row = await RowAsync(jobId);
            if (row is not null && done(row))
            {
                return row;
            }

            Assert.True(watch.Elapsed < Patience, $"{jobId}: умови не досягнуто, стан {row?.State ?? "—"}.");
            await Task.Delay(50);
        }
    }

    private static IJobDetail Detail(string jobId, Type job)
        => JobBuilder.Create<QuartzJobAdapter>()
            .WithIdentity(jobId)
            .StoreDurably()
            .UsingJobData(QuartzJobScheduler.JobCodeKey, job.FullName!)
            .UsingJobData(QuartzJobScheduler.PayloadKey, "null")
            .Build();

    private sealed class AdapterFactory(IServiceProvider provider) : IJobFactory
    {
        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler)
            => new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance);

        public void ReturnJob(IJob job)
        {
        }
    }
}

/// <summary>Задача перерахунку документа, чий лок бере справжнім шляхом (<see cref="RecalculationDocumentLock"/>).</summary>
public sealed class LockedDocumentJob(EcrDbContext db, long documentId) : IBackgroundJob
{
    /// <summary>Документ поза діапазоном тестових: лок не перетинається з чужими.</summary>
    public static long NewDocumentId() => 9_000_000_000L + Random.Shared.Next(1, int.MaxValue);

    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        await using var held = await RecalculationDocumentLock
            .AcquireAsync(db, documentId, RecalculationDocumentLock.BusyWait, ct)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Шлюз відкладення: після кожного запису <c>Queued</c> задачі <c>jobId</c>
/// (<see cref="IJobProgressStore.QueueAsync"/>) віддає момент запису й тримає
/// виконавця, доки тест не відпустить (<see cref="Release"/>) чи не відчинить
/// шлюз назавжди (<see cref="Open"/>). Решта викликів сховища — наскрізні.
/// </summary>
internal sealed class QueuedGate(string jobId) : IDisposable
{
    private readonly System.Threading.Channels.Channel<DateTime> queued =
        System.Threading.Channels.Channel.CreateUnbounded<DateTime>();

    private readonly SemaphoreSlim release = new(0);
    private volatile bool open;

    /// <summary>Обгортає справжнє сховище.</summary>
    public IJobProgressStore Wrap(IJobProgressStore inner)
    {
        var proxy = System.Reflection.DispatchProxy.Create<IJobProgressStore, GatedProgressStore>();
        ((GatedProgressStore)(object)proxy).Attach(inner, this);
        return proxy;
    }

    /// <summary>Момент наступного запису <c>Queued</c> (виконавець уже тримається).</summary>
    public async Task<DateTime> NextAsync(TimeSpan patience)
        => await queued.Reader.ReadAsync().AsTask().WaitAsync(patience).ConfigureAwait(false);

    /// <summary>Відпускає виконавця один раз.</summary>
    public void Release() => release.Release();

    /// <summary>Більше не тримає нікого.</summary>
    public void Open()
    {
        open = true;
        release.Release(64);
    }

    /// <inheritdoc />
    public void Dispose() => release.Dispose();

    internal async Task AfterQueuedAsync(string id, DateTime utcNow)
    {
        if (open || !string.Equals(id, jobId, StringComparison.Ordinal))
        {
            return;
        }

        await queued.Writer.WriteAsync(utcNow).ConfigureAwait(false);
        await release.WaitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
    }
}

/// <summary>Проксі <see cref="IJobProgressStore"/> для <see cref="QueuedGate"/>.</summary>
public class GatedProgressStore : System.Reflection.DispatchProxy
{
    private IJobProgressStore inner = null!;
    private QueuedGate gate = null!;

    internal void Attach(IJobProgressStore target, QueuedGate owner)
    {
        inner = target;
        gate = owner;
    }

    /// <inheritdoc />
    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        object? result;
        try
        {
            result = targetMethod.Invoke(inner, args);
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerException);
            throw;
        }

        return targetMethod.Name == nameof(IJobProgressStore.QueueAsync)
            ? GateAsync((Task)result!, (string)args![0]!, (DateTime)args[2]!)
            : result;
    }

    private async Task GateAsync(Task write, string jobId, DateTime utcNow)
    {
        await write.ConfigureAwait(false);
        await gate.AfterQueuedAsync(jobId, utcNow).ConfigureAwait(false);
    }
}
