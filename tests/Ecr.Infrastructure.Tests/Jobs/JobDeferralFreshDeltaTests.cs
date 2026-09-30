// tests/Ecr.Infrastructure.Tests/Jobs/JobDeferralFreshDeltaTests.cs
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using Quartz.Spi;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Д-1 огляду O1: свіжа дельта не успадковує чужий відлік стелі відкладень, а
/// вичерпана стеля не губить комірок задачі злиття.
/// </summary>
/// <remarks>
/// ⛔ Дефект: інкрементна задача A відкладається з t0 (лок документа зайнятий); PATCH B
/// о t0+29 хв зливається в A, і о t0+31 хв A падала <c>Failed</c> з
/// <c>jobs.deferralExhausted</c> — комірки B не рахувалися до нічного прогону.
/// <para>
/// Мутації (в описі коміту): без скидання моменту в <c>EnqueueSql</c> — червоний
/// «Database свіжа дельта»; без <c>Take(…, out fresh)</c> в адаптері — червоний
/// «Quartz свіжа дельта»; без перепостановки в <c>JobWorker</c>/<c>QuartzJobAdapter</c>
/// — червоні «вичерпана стеля»; без позначки <c>ecrDeferralRequeued</c> — червоний
/// «друга стеля — без перепостановки».
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "ФВ-9.8")]
public sealed class JobDeferralFreshDeltaTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private static readonly string FormulaCode = typeof(IFormulaRecalculationJob).FullName!;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Database_свіжа_дельта_о_t0_29_скидає_відлік_і_комірки_B_не_губляться_о_t0_31()
    {
        var documentId = LockedDocumentJob.NewDocumentId();
        var held = await HoldAsync(documentId);
        var clock = new TestClock(new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc));
        var t0 = clock.UtcNow;
        var target = Target(documentId);
        var runs = new FormulaRuns();

        await using var provider = BuildHost(clock, documentId, runs);
        var jobA = await EnqueueFormulaAsync(target, rowId: 1);
        var worker = StartWorker(provider);

        try
        {
            var row = await WaitAsync(jobA, r => r.State == "Queued" && JobDeferral.SinceOf(r.Payload) is not null);
            Assert.Equal(t0, JobDeferral.SinceOf(row.Payload));

            // PATCH B о t0+29 хв — нова комірка зливається в задачу, що чекає.
            clock.Advance(TimeSpan.FromMinutes(29));
            Assert.Equal(jobA, await EnqueueFormulaAsync(target, rowId: 2));

            // ⛔ О t0+31 хв лок ще зайнятий: задача з коміркою B чекає з НОВИМ відліком, а не Failed.
            clock.Advance(TimeSpan.FromMinutes(2));
            await MakeAvailableAsync(jobA);
            row = await WaitAsync(jobA, r => r.State != "Running" && r.UpdatedAt == clock.UtcNow);

            Assert.Equal("Queued", row.State);
            Assert.Equal(clock.UtcNow, JobDeferral.SinceOf(row.Payload));
            Assert.Equal([1L, 2L], RowIdsOf(row.Payload));

            // Лок звільнено — обидві комірки пораховано.
            await held.DisposeAsync();
            await MakeAvailableAsync(jobA);
            await WaitAsync(jobA, r => r.State == "Succeeded");
            Assert.Equal([1L, 2L], RowIdsOf(Assert.Single(runs.Payloads)));
        }
        finally
        {
            await held.DisposeAsync();
            await StopAsync(worker);
        }
    }

    [Fact]
    public async Task Database_вичерпана_стеля_Failed_і_комірки_перепоставлено_раз_друга_стеля_без_перепостановки()
    {
        var documentId = LockedDocumentJob.NewDocumentId();
        await using var held = await HoldAsync(documentId);
        var clock = new TestClock(new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc));
        var target = Target(documentId);

        await using var provider = BuildHost(clock, documentId, new FormulaRuns());
        var jobA = await EnqueueFormulaAsync(target, rowId: 1);
        var worker = StartWorker(provider);

        try
        {
            await WaitAsync(jobA, r => r.State == "Queued" && JobDeferral.SinceOf(r.Payload) is not null);

            clock.Advance(TimeSpan.FromMinutes(31));
            await MakeAvailableAsync(jobA);
            var failed = await WaitAsync(jobA, r => r.State is "Failed" or "Succeeded" or "Cancelled");
            Assert.Equal("Failed", failed.State);
            Assert.True(JobProgressMessageCodec.TryDecode(failed.Message, out var envelope), failed.Message);
            Assert.Equal(JobDeferral.ExhaustedKey, envelope.Key);

            // ⛔ Комірки не загублено: нова задача на ту саму ціль, без чужого відліку, з позначкою.
            var requeued = await WaitSingleOtherAsync(target, jobA);
            Assert.Equal([1L], RowIdsOf(requeued.Payload));
            Assert.True(IsRequeued(requeued.Payload));
            Assert.Equal(documentId, requeued.DocumentId);

            // Вона відклалась сама — відлік її власний.
            var own = await WaitAsync(
                requeued.JobId, r => r.State == "Queued" && JobDeferral.SinceOf(r.Payload) == clock.UtcNow);
            Assert.True(IsRequeued(own.Payload));

            // Друга стеля — Failed без нової перепостановки: лок, що не звільняється, не дає ланцюга.
            clock.Advance(TimeSpan.FromMinutes(31));
            await MakeAvailableAsync(requeued.JobId);
            Assert.Equal("Failed", (await WaitAsync(requeued.JobId, r => r.State is "Failed" or "Succeeded")).State);
            Assert.Equal(0, await ActiveOnTargetAsync(target));
        }
        finally
        {
            await StopAsync(worker);
        }
    }

    [Fact]
    public async Task Database_злиття_без_нових_комірок_відлік_не_скидає_поглинання_свій_відлік_не_переносить()
    {
        var clock = new TestClock(new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc));
        var target = Target(LockedDocumentJob.NewDocumentId());

        await using var db = Sql.CreateContext();
        var queue = new DbJobQueue(db, clock);
        var jobA = (await queue.EnqueueAsync(Formula(target, 1), CancellationToken.None)).JobId;
        var claim = (await queue.ClaimAsync(DefaultLane, "test/host", JobQueueLimits.DefaultLease, CancellationToken.None))!;
        Assert.True(await queue.DeferAsync(claim.Claim, TimeSpan.Zero, CancellationToken.None));
        var t0 = clock.UtcNow;

        // Та сама комірка ще раз — не нова: відлік лишається t0.
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(jobA, (await queue.EnqueueAsync(Formula(target, 1), CancellationToken.None)).JobId);
        Assert.Equal(t0, JobDeferral.SinceOf((await RowAsync(jobA))!.Payload));

        // Поглинання: A відкладається, позаду вже B — B не успадковує відліку A.
        claim = (await queue.ClaimAsync(DefaultLane, "test/host", JobQueueLimits.DefaultLease, CancellationToken.None))!;
        var jobB = (await queue.EnqueueAsync(Formula(target, 2), CancellationToken.None)).JobId;
        Assert.NotEqual(jobA, jobB);
        Assert.True(await queue.DeferAsync(claim.Claim, TimeSpan.Zero, CancellationToken.None));

        Assert.Equal("Cancelled", (await RowAsync(jobA))!.State);
        var behind = (await RowAsync(jobB))!;
        Assert.Null(JobDeferral.SinceOf(behind.Payload));
        Assert.Equal([1L, 1L, 2L], RowIdsOf(behind.Payload));
    }

    [Fact]
    public async Task Quartz_свіжа_дельта_о_t0_29_скидає_відлік_і_задача_чекає_о_t0_31()
    {
        // Годинник — справжній «зараз»: триґер відкладення (clock + 5 с) спрацьовує
        // через 5 с реального часу, і між спрацюваннями тест переводить годинник.
        var clock = new TestClock(new SystemClock().UtcNow);
        var t0 = clock.UtcNow;
        var job = new DeferringFormulaJob();
        var (jobs, quartz) = await QuartzAsync(clock, job);
        var target = $"doc{LockedDocumentJob.NewDocumentId()}-p202601-formula-u7";

        try
        {
            var id = await jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(target, Payload(1), CancellationToken.None);
            await quartz.Start();
            Assert.Equal([1L], RowIdsOf(await job.NextAsync()));
            await IdleWithTriggerAsync(quartz, id);

            clock.Advance(TimeSpan.FromMinutes(29));
            Assert.Equal(id, await jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(target, Payload(2), CancellationToken.None));

            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal([1L, 2L], RowIdsOf(await job.NextAsync()));

            // ⛔ Не Failed: новий триґер з відліком від t0+31 хв.
            var trigger = await IdleWithTriggerAsync(quartz, id);
            Assert.Equal(
                clock.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture),
                trigger.JobDataMap.GetString(JobDeferral.QuartzSinceKey));
            Assert.NotEqual(t0, clock.UtcNow);
        }
        finally
        {
            await quartz.Shutdown(waitForJobsToComplete: false);
        }
    }

    [Fact]
    public async Task Quartz_вичерпана_стеля_Failed_і_комірки_перепоставлено_новою_задачею()
    {
        var clock = new TestClock(new SystemClock().UtcNow);
        var job = new DeferringFormulaJob();
        var (jobs, quartz) = await QuartzAsync(clock, job);
        var target = $"doc{LockedDocumentJob.NewDocumentId()}-p202601-formula-u7";

        try
        {
            var id = await jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(target, Payload(1), CancellationToken.None);
            await quartz.Start();
            await job.NextAsync();
            await IdleWithTriggerAsync(quartz, id);

            clock.Advance(TimeSpan.FromMinutes(31));
            await job.NextAsync();

            // Перепоставлена задача виконується одразу й несе ті самі комірки з позначкою.
            var requeuedPayload = await job.NextAsync();
            Assert.Equal([1L], RowIdsOf(requeuedPayload));
            Assert.True(IsRequeued(requeuedPayload));

            var keys = (await quartz.GetJobKeys(GroupMatcher<JobKey>.AnyGroup())).Select(k => k.Name).ToList();
            var requeued = Assert.Single(keys, k => k != id);
            Assert.StartsWith(id[..^32], requeued, StringComparison.Ordinal);
            Assert.Empty(await quartz.GetTriggersOfJob(new JobKey(id)));
            await IdleWithTriggerAsync(quartz, requeued);
        }
        finally
        {
            await quartz.Shutdown(waitForJobsToComplete: false);
        }
    }

    [Fact]
    public void RequeuePayload_знімає_відлік_ставить_позначку_і_вдруге_не_перепоставляє()
    {
        var once = JobDeferral.RequeuePayload("""{"cells":[{"rowId":1}],"ecrDeferredSince":"2026-09-30T12:00:00.0000000Z"}""");

        Assert.NotNull(once);
        Assert.Null(JobDeferral.SinceOf(once));
        Assert.True(IsRequeued(once));
        Assert.Null(JobDeferral.RequeuePayload(once));
        Assert.Null(JobDeferral.RequeuePayload("[1]"));
    }

    // ── Світ ─────────────────────────────────────────────────────────────────

    private static string Target(long documentId) => $"{nameof(IFormulaRecalculationJob)}~doc{documentId}-p202601-formula-u1";

    private static JobEnqueueRequest Formula(string target, long rowId, long documentId = 1)
        => new(
            FormulaCode, JobLanes.Default,
            $$"""{"documentId":{{documentId}},"tableInstanceId":1,"periodKey":202601,"cells":[{"rowId":{{rowId}},"columnDefId":7}]}""",
            target, DocumentId: documentId);

    private static object Payload(params long[] rowIds)
        => new
        {
            DocumentId = 1L,
            TableInstanceId = 1L,
            PeriodKey = 202601,
            Cells = rowIds.Select(r => new { RowId = r, ColumnDefId = 7 }).ToArray(),
        };

    private static List<long> RowIdsOf(string? payload)
    {
        using var json = JsonDocument.Parse(payload!);
        return [.. json.RootElement.GetProperty("cells").EnumerateArray().Select(c => c.GetProperty("rowId").GetInt64()).Order()];
    }

    private static bool IsRequeued(string? payload)
    {
        using var json = JsonDocument.Parse(payload!);
        return json.RootElement.TryGetProperty(JobDeferral.RequeuedProperty, out var flag) && flag.ValueKind == JsonValueKind.True;
    }

    private async Task<string> EnqueueFormulaAsync(string target, long rowId)
    {
        await using var host = NewHost();
        var documentId = long.Parse(target.Split("~doc")[1].Split('-')[0], CultureInfo.InvariantCulture);
        return (await host.Queue.EnqueueAsync(Formula(target, rowId, documentId), CancellationToken.None)).JobId;
    }

    private async Task<SqlDistributedLock> HoldAsync(long documentId)
        => (await SqlDistributedLock.AcquireAsync(
            Sql.ConnectionString, RecalculationDocumentLock.Resource(documentId), TimeSpan.Zero, CancellationToken.None))!;

    private Task MakeAvailableAsync(string jobId)
        => ExecAsync(
            "UPDATE itg.JobProgress SET AvailableAt = DATEADD(second, -1, SYSUTCDATETIME()) WHERE JobId = @id AND [State] = 'Queued';",
            jobId);

    private async Task<int> ActiveOnTargetAsync(string target)
    {
        await using var db = Sql.CreateContext();
        return await db.JobProgresses.CountAsync(p => p.Lane != null && p.TargetKey == target && (p.State == "Queued" || p.State == "Running"));
    }

    private async Task<JobProgress> WaitSingleOtherAsync(string target, string except)
    {
        var watch = Stopwatch.StartNew();

        while (true)
        {
            await using (var db = Sql.CreateContext())
            {
                var rows = await db.JobProgresses.AsNoTracking()
                    .Where(p => p.Lane != null && p.JobCode == FormulaCode && p.JobId != except)
                    .ToListAsync();
                if (rows.Count > 0)
                {
                    var single = Assert.Single(rows);
                    Assert.Equal(target, single.TargetKey ?? target);
                    return single;
                }
            }

            Assert.True(watch.Elapsed < Patience, "Перепоставленої задачі немає.");
            await Task.Delay(50);
        }
    }

    private ServiceProvider BuildHost(TestClock clock, long documentId, FormulaRuns runs)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Sql.CreateContext());
        services.AddSingleton<Ecr.Domain.Abstractions.IClock>(clock);
        services.AddScoped<IJobQueue>(sp => new DbJobQueue(sp.GetRequiredService<EcrDbContext>(), sp.GetRequiredService<Ecr.Domain.Abstractions.IClock>()));
        services.AddScoped<IJobProgressStore>(sp => new JobProgressStore(sp.GetRequiredService<EcrDbContext>()));
        services.AddScoped<JobLeaseContext>();
        services.AddScoped<IJobLeaseContext>(sp => sp.GetRequiredService<JobLeaseContext>());
        services.AddSingleton<JobQueueSignal>();
        services.AddSingleton(runs);
        services.AddScoped<IFormulaRecalculationJob>(sp => new LockedFormulaJob(sp.GetRequiredService<EcrDbContext>(), documentId, runs));
        return services.BuildServiceProvider();
    }

    private static JobWorker StartWorker(ServiceProvider provider)
    {
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
        worker.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return worker;
    }

    private static async Task StopAsync(JobWorker worker)
    {
        await worker.StopAsync(CancellationToken.None);
        worker.Dispose();
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

    private static async Task<(QuartzJobScheduler Jobs, IScheduler Quartz)> QuartzAsync(TestClock clock, DeferringFormulaJob job)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Ecr.Domain.Abstractions.IClock>(clock);
        services.AddSingleton<IFormulaRecalculationJob>(job);
        var provider = services.BuildServiceProvider();

        var factory = new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-tests-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
        });
        var quartz = await factory.GetScheduler();
        quartz.JobFactory = new AdapterFactory(provider);

        return (new QuartzJobScheduler(factory, clock: clock), quartz);
    }

    /// <summary>Задача вже не виконується і чекає рівно одного триґера; повертає його.</summary>
    private static async Task<ITrigger> IdleWithTriggerAsync(IScheduler quartz, string jobId)
    {
        var watch = Stopwatch.StartNew();

        while (true)
        {
            var executing = (await quartz.GetCurrentlyExecutingJobs()).Any(c => c.JobDetail.Key.Name == jobId);
            var triggers = await quartz.GetTriggersOfJob(new JobKey(jobId));
            if (!executing && triggers.Count == 1 && triggers.Single().JobDataMap.ContainsKey(JobDeferral.QuartzSinceKey))
            {
                return triggers.Single();
            }

            Assert.True(watch.Elapsed < Patience, $"{jobId}: задача не стала в очікування з одним триґером.");
            await Task.Delay(20);
        }
    }

    private sealed class AdapterFactory(IServiceProvider provider) : IJobFactory
    {
        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler)
            => new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance);

        public void ReturnJob(IJob job)
        {
        }
    }

    /// <summary>Payload-и успішних прогонів задачі формул.</summary>
    private sealed class FormulaRuns
    {
        private readonly List<string?> payloads = [];

        public IReadOnlyList<string?> Payloads
        {
            get
            {
                lock (payloads)
                {
                    return [.. payloads];
                }
            }
        }

        public void Add(string? payload)
        {
            lock (payloads)
            {
                payloads.Add(payload);
            }
        }
    }

    /// <summary>Задача формул, що бере лок документа справжнім шляхом і запам'ятовує payload успіху.</summary>
    private sealed class LockedFormulaJob(EcrDbContext db, long documentId, FormulaRuns runs) : IFormulaRecalculationJob
    {
        public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        {
            await using var held = await RecalculationDocumentLock
                .AcquireAsync(db, documentId, RecalculationDocumentLock.BusyWait, ct)
                .ConfigureAwait(false);
            runs.Add(payload as string);
        }
    }

    /// <summary>Задача формул Quartz, що завжди відкладається (лок зайнятий), віддаючи payload кожного прогону.</summary>
    private sealed class DeferringFormulaJob : IFormulaRecalculationJob
    {
        private readonly Channel<string?> runs = Channel.CreateUnbounded<string?>();

        public async Task<string?> NextAsync()
            => await runs.Reader.ReadAsync().AsTask().WaitAsync(Patience);

        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        {
            runs.Writer.TryWrite(payload as string);
            throw new JobDeferredException(RecalculationDocumentLock.DeferDelay, "зайнято", "ecr:recalc:doc:test");
        }
    }
}
