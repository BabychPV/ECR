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
/// (3) <c>ContinuationDelayAsync</c> завжди нуль — червоний перший тест (правку взяв би наступний крок, а не воркер Api);
/// (4) без <c>ParkRemainingAsync</c> — червоний перший тест (залежні березня порахувались би окремо перед ланцюжком).
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

        // ── Крок 1: січень.
        var step = (await ClaimAsync(host, JobLanes.Recalc))!;
        Assert.Equal(jobId, step.Claim.JobId);

        // Правка комірки під час кроку: задачу формул документа claim не бере, поки крок виконується.
        var edit = await EnqueueEditAsync(document.DocumentId);
        Assert.Null(await ClaimAsync(host, JobLanes.Interactive));

        var continued = await Assert.ThrowsAsync<JobContinuedException>(
            () => RunStepAsync(builder, scheduler, host.Queue, step));
        Assert.Equal(january.Value, continued.SettleThrough);
        Assert.Equal(RecalculationJob.ContinuationYield, continued.Delay);
        var next = JsonSerializer.Deserialize<RecalculationRequest>(continued.PayloadJson, Web)!;
        Assert.Equal(january.Value, next.ChainAfter);
        Assert.Equal([january.Value], next.ChainDone!);

        // Січень уже актуальний, лютий і березень ще не рахувались.
        Assert.Equal([january.Value], await CurrentPeriodsAsync(document.DocumentId));

        Assert.True(await host.Queue.ContinueAsync(
            step.Claim, continued.PayloadJson, continued.Delay, continued.SettleThrough, CancellationToken.None));

        // Залежні березня — запарковані ланцюжком до його кроку березня.
        AssertParked(await RowAsync(marchDependents), jobId);

        // Між місяцями: правку бере воркер Api (наступний крок поступився їй), лок документа вільний.
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
        await ExecAsync("UPDATE itg.JobProgress SET AvailableAt = SYSUTCDATETIME() WHERE JobId = @id;", jobId);

        // ── Крок 2: лютий — той самий рядок черги, спроба не рахується.
        step = (await ClaimAsync(host, JobLanes.Recalc))!;
        Assert.Equal((jobId, 1), (step.Claim.JobId, step.Attempt));
        continued = await Assert.ThrowsAsync<JobContinuedException>(() => RunStepAsync(builder, scheduler, host.Queue, step));
        Assert.Equal((February.Value, TimeSpan.Zero), (continued.SettleThrough, continued.Delay));
        Assert.Equal([january.Value, February.Value], await CurrentPeriodsAsync(document.DocumentId));
        Assert.True(await host.Queue.ContinueAsync(
            step.Claim, continued.PayloadJson, continued.Delay, continued.SettleThrough, CancellationToken.None));
        AssertParked(await RowAsync(marchDependents), jobId);

        // ── Крок 3: березень — останній, задача закінчується.
        step = (await ClaimAsync(host, JobLanes.Recalc))!;
        Assert.Equal(jobId, step.Claim.JobId);
        await RunStepAsync(builder, scheduler, host.Queue, step);
        Assert.True(await host.Queue.CompleteAsync(step.Claim, CancellationToken.None));

        Assert.Equal([january.Value, February.Value, March.Value], await CurrentPeriodsAsync(document.DocumentId));
        Assert.Equal("Succeeded", (await RowAsync(jobId))!.State);

        // Залежні березня закрито разом із кроком березня, а не пораховано вдруге.
        Assert.Equal("Succeeded", (await RowAsync(marchDependents))!.State);

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

    private async Task RunStepAsync(
        TestDocumentBuilder builder, IBackgroundJobScheduler scheduler, IJobQueue queue, ClaimedJob step)
    {
        await using var db = builder.CreateContext();
        var clock = new TestClock(Now);
        var job = new RecalculationJob(
            db,
            new WritingRunner(builder, _methodologyVersionId, _unitId),
            RunHandler(db, clock),
            FormulaService(),
            clock,
            jobs: scheduler,
            lease: new FixedLease(step.Claim),
            queue: queue);

        await job.ExecuteAsync(step.PayloadJson, new NoOpProgress(), CancellationToken.None);
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

    private static RunCalculationHandler RunHandler(EcrDbContext db, TestClock clock)
        => new(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            new CalculationResultStore(db, clock),
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
    private sealed class WritingRunner(TestDocumentBuilder builder, int methodologyVersionId, int unitId) : ICalculationRunner
    {
        public async Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
        {
            if (bindings.Count == 0)
            {
                return new ModuleProfile();
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
            return new ModuleProfile();
        }
    }

    private sealed class FixedLease(JobClaimToken claim) : IJobLeaseContext
    {
        public JobClaimToken? Current { get; } = claim;
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
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
