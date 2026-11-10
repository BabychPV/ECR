// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationDeferralRequeueTests.cs
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using Quartz.Spi;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// AN-13 D4 (рекурсія перерахунку): перерахунок документа, що вичерпав стелю відкладень, перепоставляється
/// злиттям на ту саму ціль раз на серію, а стеля береться з конфігурації.
/// </summary>
/// <remarks>
/// ⛔ Дефект: <c>RecalculationJob</c>, чий лок документа 30 хв тримав інший прогін, закривався <c>Failed</c>
/// без перепостановки (перепоставлялась лише задача злиття формул, Д-1 огляду O1), і документ лишався
/// неперерахованим до ручної команди чи нічного прогону. Стеля ж була константою — ключ
/// <c>Jobs:Recalculation:MaxDeferral</c> існував лише в коментарі.
/// <para>
/// Мутації (в описі коміту): без перерахунку в <c>JobDeferral.RequeuesOnExhaustion</c> — червоні «Database
/// вичерпана стеля» і «Quartz вичерпана стеля»; адаптер зі сталою <c>JobDeferral.MaxDeferral</c> — червоний
/// «Quartz стеля з конфігурації».
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "ФВ-9.8")]
public sealed class RecalculationDeferralRequeueTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private static readonly string RecalcCode = typeof(IRecalculationJob).FullName!;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Database_вичерпана_стеля_перерахунку_Failed_і_ціль_перепоставлено_раз_злиттям()
    {
        var documentId = LockedDocumentJob.NewDocumentId();
        await using var held = await HoldAsync(documentId);
        var clock = new TestClock(new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc));
        var target = $"{nameof(IRecalculationJob)}~doc{documentId}-year";

        await using var provider = BuildHost(clock, documentId);
        var jobA = await EnqueueRecalcAsync(target, documentId);
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

            // ⛔ Документ не загублено: нова задача на ту саму ціль, той самий документ, з позначкою серії.
            var requeued = await WaitOtherOnTargetAsync(target, jobA);
            Assert.Equal(RecalcCode, requeued.JobCode);
            Assert.Equal(documentId, requeued.DocumentId);
            Assert.True(IsRequeued(requeued.Payload));
            Assert.Equal(documentId, DocumentIdOf(requeued.Payload));

            var own = await WaitAsync(
                requeued.JobId, r => r.State == "Queued" && JobDeferral.SinceOf(r.Payload) == clock.UtcNow);
            Assert.True(IsRequeued(own.Payload));

            // Друга стеля — Failed без нової перепостановки: лок, що не звільняється, не дає ланцюга.
            clock.Advance(TimeSpan.FromMinutes(31));
            await MakeAvailableAsync(requeued.JobId);
            Assert.Equal("Failed", (await WaitAsync(requeued.JobId, r => r.State is "Failed" or "Succeeded")).State);
            Assert.Equal(0, await ActiveOnTargetAsync(target));
            Assert.Equal(2, await RowsOnTargetAsync(target));
        }
        finally
        {
            await StopAsync(worker);
        }
    }

    [Fact]
    public async Task Database_перерахунок_без_цілі_після_стелі_не_перепоставляється()
    {
        // Без цілі злиття немає — перепостановка дала б копію поруч із будь-якою іншою постановкою.
        var documentId = LockedDocumentJob.NewDocumentId();
        await using var held = await HoldAsync(documentId);
        var clock = new TestClock(new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc));

        await using var provider = BuildHost(clock, documentId);
        var jobA = await EnqueueRecalcAsync(target: null, documentId);
        var worker = StartWorker(provider);

        try
        {
            await WaitAsync(jobA, r => r.State == "Queued" && JobDeferral.SinceOf(r.Payload) is not null);

            clock.Advance(TimeSpan.FromMinutes(31));
            await MakeAvailableAsync(jobA);
            Assert.Equal("Failed", (await WaitAsync(jobA, r => r.State is "Failed" or "Succeeded")).State);

            await using var db = Sql.CreateContext();
            Assert.Equal(1, await db.JobProgresses.CountAsync(p => p.Lane != null && p.DocumentId == documentId));
        }
        finally
        {
            await StopAsync(worker);
        }
    }

    [Fact]
    public async Task Quartz_вичерпана_стеля_перерахунку_Failed_і_ціль_перепоставлено_новою_задачею()
    {
        var clock = new TestClock(new SystemClock().UtcNow);
        var job = new DeferringRecalcJob();
        var (jobs, quartz) = await QuartzAsync(clock, job);
        var documentId = LockedDocumentJob.NewDocumentId();

        try
        {
            var id = await jobs.EnqueueExclusiveAsync<IRecalculationJob>(
                $"doc{documentId}-year", new { DocumentId = documentId }, CancellationToken.None);
            await quartz.Start();
            await job.NextAsync();
            await IdleWithTriggerAsync(quartz, id);

            clock.Advance(TimeSpan.FromMinutes(31));
            await job.NextAsync();

            // Перепоставлена задача виконується одразу: той самий документ, позначка серії.
            var requeuedPayload = await job.NextAsync();
            Assert.Equal(documentId, DocumentIdOf(requeuedPayload));
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
    public async Task Quartz_стеля_з_конфігурації_а_не_стала_30_хв()
    {
        // Стеля 20 хв, відкладається з t-21 хв: за конфігурацією — вичерпано (Failed), за сталою 30 хв — ще ні.
        var now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        var progress = Substitute.For<IJobProgressStore>();
        var services = new ServiceCollection();
        services.AddSingleton(progress);
        services.AddSingleton<IClock>(new TestClock(now));
        services.AddSingleton(new JobWorkerOptions { Lanes = JobLanes.All, MaxDeferral = TimeSpan.FromMinutes(20) });
        services.AddScoped<DeferringJob>();
        await using var provider = services.BuildServiceProvider();

        var detail = Substitute.For<IJobDetail>();
        detail.Key.Returns(new JobKey("deferred-config-1"));
        detail.JobDataMap.Returns(new JobDataMap
        {
            { QuartzJobScheduler.JobCodeKey, typeof(DeferringJob).FullName! },
            { QuartzJobScheduler.PayloadKey, "null" },
        });
        var trigger = Substitute.For<ITrigger>();
        trigger.JobDataMap.Returns(new JobDataMap
        {
            { QuartzJobScheduler.RetryAttemptKey, "0" },
            { JobDeferral.QuartzSinceKey, now.AddMinutes(-21).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        });
        var scheduler = Substitute.For<IScheduler>();
        var context = Substitute.For<IJobExecutionContext>();
        context.JobDetail.Returns(detail);
        context.Trigger.Returns(trigger);
        context.Scheduler.Returns(scheduler);

        await Assert.ThrowsAsync<JobExecutionException>(
            () => new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance).Execute(context));

        Assert.DoesNotContain(scheduler.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IScheduler.ScheduleJob));
        await progress.Received(1).ReportAsync(
            "deferred-config-1", 0, Arg.Is<string?>(m => m != null && m.Contains(JobDeferral.ExhaustedKey)), now,
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData("", 30)]
    [InlineData("45", 45)]
    [InlineData("0", 30)]
    [InlineData("-5", 30)]
    [InlineData("abc", 30)]
    public void ReadMaxDeferral_ціле_хвилин_не_менше_одної_інакше_типова(string? value, int expectedMinutes)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [JobDeferral.MaxDeferralMinutesKey] = value })
            .Build();

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), JobDeferral.ReadMaxDeferral(configuration));
    }

    [Theory]
    [InlineData("Ecr.Application.Ports.IRecalculationJob", "IRecalculationJob~doc1-year", true)]
    [InlineData("Ecr.Application.Ports.IRecalculationJob", null, false)]
    [InlineData("Ecr.Application.Ports.IRecalculationJob", " ", false)]
    [InlineData("Ecr.Application.Ports.IFormulaRecalculationJob", null, true)]
    [InlineData("Ecr.Application.Ports.IExcelImportJob", "IExcelImportJob~x", false)]
    public void Перепостановка_лише_злиття_формул_і_перерахунку_з_ціллю(string code, string? target, bool expected)
        => Assert.Equal(expected, JobDeferral.RequeuesOnExhaustion(code, target));

    [Theory]
    [InlineData("IRecalculationJob~doc1-year~0123456789abcdef0123456789abcdef", "IRecalculationJob~doc1-year~")]
    [InlineData("deferral-cap-0123456789abcdef0123456789abcdef", null)]
    [InlineData("IRecalculationJob~0123456789abcdef0123456789abcdef", null)]
    [InlineData("short", null)]
    [InlineData(null, null)]
    public void Префікс_цілі_з_ключа_Quartz(string? jobId, string? expected)
        => Assert.Equal(expected, QuartzJobScheduler.TargetPrefixOfJobId(jobId));

    // ── Світ ─────────────────────────────────────────────────────────────────

    private static bool IsRequeued(string? payload)
    {
        using var json = JsonDocument.Parse(payload!);
        return json.RootElement.TryGetProperty(JobDeferral.RequeuedProperty, out var flag) && flag.ValueKind == JsonValueKind.True;
    }

    private static long DocumentIdOf(string? payload)
    {
        using var json = JsonDocument.Parse(payload!);
        return json.RootElement.GetProperty("documentId").GetInt64();
    }

    private async Task<string> EnqueueRecalcAsync(string? target, long documentId)
    {
        await using var host = NewHost();
        return (await host.Queue.EnqueueAsync(
                new JobEnqueueRequest(
                    RecalcCode, JobLanes.Recalc, $$"""{"documentId":{{documentId}}}""", target, DocumentId: documentId),
                CancellationToken.None))
            .JobId;
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

    private async Task<int> RowsOnTargetAsync(string target)
    {
        await using var db = Sql.CreateContext();
        return await db.JobProgresses.CountAsync(p => p.Lane != null && p.TargetKey == target);
    }

    private async Task<JobProgress> WaitOtherOnTargetAsync(string target, string except)
    {
        var watch = Stopwatch.StartNew();

        while (true)
        {
            await using (var db = Sql.CreateContext())
            {
                var rows = await db.JobProgresses.AsNoTracking()
                    .Where(p => p.Lane != null && p.TargetKey == target && p.JobId != except)
                    .ToListAsync();
                if (rows.Count > 0)
                {
                    return Assert.Single(rows);
                }
            }

            Assert.True(watch.Elapsed < Patience, "Перепоставленої задачі немає.");
            await Task.Delay(50);
        }
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

    private ServiceProvider BuildHost(TestClock clock, long documentId)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Sql.CreateContext());
        services.AddSingleton<IClock>(clock);
        services.AddScoped<IJobQueue>(sp => new DbJobQueue(sp.GetRequiredService<EcrDbContext>(), sp.GetRequiredService<IClock>()));
        services.AddScoped<IJobProgressStore>(sp => new JobProgressStore(sp.GetRequiredService<EcrDbContext>()));
        services.AddScoped<JobLeaseContext>();
        services.AddScoped<IJobLeaseContext>(sp => sp.GetRequiredService<JobLeaseContext>());
        services.AddSingleton<JobQueueSignal>();
        services.AddScoped<IRecalculationJob>(sp => new LockedRecalcJob(sp.GetRequiredService<EcrDbContext>(), documentId));
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

    private static async Task<(QuartzJobScheduler Jobs, IScheduler Quartz)> QuartzAsync(TestClock clock, DeferringRecalcJob job)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<IRecalculationJob>(job);
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

    /// <summary>Задача вже не виконується і чекає рівно одного триґера відкладення.</summary>
    private static async Task IdleWithTriggerAsync(IScheduler quartz, string jobId)
    {
        var watch = Stopwatch.StartNew();

        while (true)
        {
            var executing = (await quartz.GetCurrentlyExecutingJobs()).Any(c => c.JobDetail.Key.Name == jobId);
            var triggers = await quartz.GetTriggersOfJob(new JobKey(jobId));
            if (!executing && triggers.Count == 1 && triggers.Single().JobDataMap.ContainsKey(JobDeferral.QuartzSinceKey))
            {
                return;
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

    /// <summary>Перерахунок документа, що бере лок справжнім шляхом (<see cref="RecalculationDocumentLock"/>).</summary>
    private sealed class LockedRecalcJob(EcrDbContext db, long documentId) : IRecalculationJob
    {
        public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        {
            await using var held = await RecalculationDocumentLock
                .AcquireAsync(db, documentId, RecalculationDocumentLock.BusyWait, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Перерахунок Quartz, що завжди відкладається, віддаючи payload кожного прогону.</summary>
    private sealed class DeferringRecalcJob : IRecalculationJob
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
