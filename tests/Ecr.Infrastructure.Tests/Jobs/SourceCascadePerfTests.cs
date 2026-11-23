// tests/Ecr.Infrastructure.Tests/Jobs/SourceCascadePerfTests.cs
using System.Diagnostics;
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ PERF-06b (<c>D-572</c>, закриває «не зроблено» D-566 п. «вимір ~200 споживачів HSE400»): 1 документ-джерело, 200
/// документів-споживачів одного шаблону (12 відкритих місяців 2099), ребро «місяць» і ребро «рік». Вимір і стелі:
/// задачі на правку, злиття за вікно 10 с, звернення до SQL батьківської задачі, час каскаду до порожньої черги — черга в
/// базі (злиття об'єднанням, ланцюжки) і Quartz (поперіодно, як до D-566).
/// </summary>
/// <remarks>
/// <para>
/// Справжні SQL, граф, сховище, тригер, черга в базі (claim → задача → продовження/завершення) і Quartz (RAMJobStore, свій
/// інстанс, не запущений: задачі виконує тест по одній, як один потік Quartz). Оркестратор методологій і формули — фейки (вимір
/// черги й оркестрації каскаду, а не обчислення методологій). Правка — успіх власного перерахунку джерела (хук джерела, як
/// <c>RecalculationJob</c>).
/// </para>
/// <para>
/// Стелі — храповик (як <c>FixedCost</c>/<c>QueryCount</c>): виміряне + запас; перевищення — регресія. Числа — у D-572 і в
/// виводі тесту. МУТАЦІЯ (локально, 23.11): без пакетної постановки (<c>CalculationTrigger.PrefetchAsync</c> не читає
/// пакетом) — червоний рядок «рік» черги в базі (6012 звернень на правку &gt; 2000).
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "PERF-06")]
[Trait("Perf", "Cascade200")]
public sealed class SourceCascadePerfTests(SqlServerFixture sql, ITestOutputHelper output) : DbJobQueueTestsBase(sql)
{
    private const int Consumers = 200;

    private static readonly DateTime Now = new(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);

    private static readonly int[] EditedMonths = [3, 5, 7, 9, 11];

    /// <summary>`ECR_PERF06B_FULL=1` — вимір до порожньої черги (локально, числа D-572); інакше (CI) — перші 100 кроків: стелі звернень на крок.</summary>
    private static readonly bool Full = Environment.GetEnvironmentVariable("ECR_PERF06B_FULL") == "1";

    private static int DrainLimit => Full ? int.MaxValue : 100;

    /// <summary>
    /// Черга в базі: одна правка — одна задача зв'язки і рівно 200 задач каскаду (по одній на споживача й рік); п'ять правок за
    /// вікно — ті самі 200 (злиття об'єднанням); звернення батьківської задачі й час до порожньої черги — під стелями.
    /// </summary>
    [Theory]
    [InlineData("month", 5)]
    [InlineData("year", 12)]
    public async Task Черга_в_базі_200_споживачів(string edge, int periodsPerConsumer)
    {
        var world = await ArrangeAsync(edge);

        var total = Stopwatch.StartNew();
        var parentCommands = new List<int>();
        var enqueueCalls = 0;
        foreach (var month in EditedMonths)
        {
            await EditSourceAsync(world, month, DbScheduler);
            var (commands, calls) = await RunParentAsync(world);
            parentCommands.Add(commands);
            enqueueCalls += calls;
        }

        var rows = await CascadeRowsAsync(world);
        var periods = rows.Select(r => JsonDocument.Parse(r.Payload!).RootElement.GetProperty("cascadePeriods").GetArrayLength()).ToList();

        // Вікно 10 с від першої постановки — далі черга.
        await Task.Delay(CalculationTrigger.CascadeDebounce + TimeSpan.FromMilliseconds(500));
        var drain = Stopwatch.StartNew();
        var (steps, drainCommands) = await DrainDbQueueAsync(DrainLimit);
        drain.Stop();
        total.Stop();

        output.WriteLine(
            $"DB queue, edge={edge}: edits={EditedMonths.Length}, parent jobs={EditedMonths.Length}, cascade period requests={enqueueCalls}, " +
            $"cascade rows={rows.Count}, merges={enqueueCalls - rows.Count}, periods/row={periods.Distinct().Single()}, " +
            $"parent SQL round trips/edit={string.Join("/", parentCommands)}, steps={steps}, drain SQL round trips={drainCommands} " +
            $"({drainCommands / Math.Max(1, steps)}/step), drain={drain.ElapsedMilliseconds} ms ({drain.ElapsedMilliseconds / Math.Max(1, steps)} ms/step; full drain {Consumers * periodsPerConsumer} steps), total incl. window={total.ElapsedMilliseconds} ms");

        Assert.Equal(Consumers, rows.Count);
        Assert.All(periods, p => Assert.Equal(periodsPerConsumer, p));
        Assert.Equal(Consumers * (edge == "month" ? 1 : 12) * EditedMonths.Length, enqueueCalls);
        if (Full)
        {
            Assert.Equal(Consumers * periodsPerConsumer, steps);
            Assert.Empty(await LiveRowsAsync());
        }

        // Храповик (виміряне 23.11 + запас ~25 %, див. D-572): пакетна постановка каскаду — 8 звернень на споживача за будь-якого ребра.
        Assert.All(parentCommands, c => Assert.True(c <= 2_000, $"parent round trips {c} > 2000"));
        Assert.True(drainCommands <= steps * 60, $"drain round trips {drainCommands} > {steps * 60}");
    }

    /// <summary>
    /// Quartz (черга без об'єднання): каскад поперіодно, як до D-566 — задача на (споживач, період); повторна правка того самого
    /// періоду за вікно зливається (payload першої), різні місяці — окремі задачі.
    /// </summary>
    [Theory]
    [InlineData("month", 5)]
    [InlineData("year", 12)]
    public async Task Quartz_200_споживачів(string edge, int periodsPerConsumer)
    {
        var world = await ArrangeAsync(edge);
        var (quartzJobs, scheduler) = await QuartzAsync();
        try
        {
            await QuartzScenarioAsync(world, quartzJobs, scheduler, edge, periodsPerConsumer);
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false);
        }
    }

    private async Task QuartzScenarioAsync(World world, QuartzJobScheduler quartzJobs, IScheduler scheduler, string edge, int periodsPerConsumer)
    {
        var total = Stopwatch.StartNew();
        var parentCommands = new List<int>();
        var enqueueCalls = 0;
        foreach (var month in EditedMonths)
        {
            await EditSourceAsync(world, month, _ => quartzJobs);
            var parent = await TakeQuartzAsync<IRegistryImpactRecalculationJob>(scheduler);
            var (commands, calls) = await RunParentAsync(world, quartzJobs, Assert.Single(parent).Payload);
            parentCommands.Add(commands);
            enqueueCalls += calls;
        }

        var keys = await QuartzKeysAsync<IRecalculationJob>(scheduler);
        var drain = Stopwatch.StartNew();
        var (steps, drainCommands) = await DrainQuartzAsync(scheduler, DrainLimit);
        drain.Stop();
        total.Stop();

        output.WriteLine(
            $"Quartz, edge={edge}: edits={EditedMonths.Length}, cascade period requests={enqueueCalls}, cascade jobs={keys.Count}, " +
            $"merges={enqueueCalls - keys.Count}, parent SQL round trips/edit={string.Join("/", parentCommands)}, steps={steps}, " +
            $"drain SQL round trips={drainCommands} ({drainCommands / Math.Max(1, steps)}/step), drain={drain.ElapsedMilliseconds} ms " +
            $"({drain.ElapsedMilliseconds / Math.Max(1, steps)} ms/step; full drain {Consumers * periodsPerConsumer} steps), total={total.ElapsedMilliseconds} ms");

        Assert.Equal(Consumers * periodsPerConsumer, keys.Count);
        Assert.Equal(Math.Min(DrainLimit, Consumers * periodsPerConsumer), steps);
        // Quartz (поперіодно, як до D-566; лише розробка й малі інсталяції, D-563): 4 звернення на період споживача.
        Assert.All(parentCommands, c => Assert.True(c <= QuartzParentCeiling(edge), $"parent round trips {c} > {QuartzParentCeiling(edge)}"));
        Assert.True(drainCommands <= steps * 36, $"drain round trips {drainCommands} > {steps * 36}");
    }

    /// <summary>Стеля звернень батьківської задачі Quartz на одну правку (виміряне 812 / 9612 + запас).</summary>
    private static int QuartzParentCeiling(string edge) => edge == "month" ? 1_000 : 12_000;

    private Func<Host, IBackgroundJobScheduler> DbScheduler => host => Scheduler(host);

    private async Task<World> ArrangeAsync(string edge)
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, code) = await world.RegistryAsync();
        var source = await world.DocumentAsync([.. Enumerable.Range(1, 12)]);
        var first = await world.DocumentAsync(
            [.. Enumerable.Range(1, 12)], code, edge == "year" ? "[Period].Year" : "[Period].Year, [Period].Sequence");
        var bindingId = await world.BindingAsync(source.TemplateId, registryId);

        var consumers = new List<long> { first.DocumentId };
        await using (var db = Sql.CreateContext())
        {
            var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync();
            var loader = new BulkCellLoader(Sql.ConnectionString, 1000);
            var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            for (var i = 1; i < Consumers; i++)
            {
                var project = new Project(
                    EcrCode.Create($"P6BC{tag}{i}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "c" }),
                    new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), first.TemplateVersionId, PeriodKind.Monthly, policyId,
                    "Asia/Atyrau");
                db.Projects.Add(project);
                await db.SaveChangesAsync();

                var document = new Document(project.Id, $"P6BD{tag}{i}", 1, Now);
                db.Documents.Add(document);
                for (var m = 1; m <= 12; m++)
                {
                    var key = new PeriodKey(SourceDependencyWorld.Period(m));
                    var period = new Period(
                        project.Id, key, (byte)m, new DateOnly(2099, m, 1), new DateOnly(2099, m, DateTime.DaysInMonth(2099, m)));
                    period.AdvanceTo(PeriodState.Open, Now);
                    db.Periods.Add(period);
                }

                await db.SaveChangesAsync();
                var firstId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 12, CancellationToken.None);
                for (var m = 1; m <= 12; m++)
                {
                    db.TableInstances.Add(new TableInstance(
                        new PeriodKey(SourceDependencyWorld.Period(m)), firstId + m - 1, document.Id, first.TableDefId, Now));
                }

                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
                consumers.Add(document.Id);
            }
        }

        return new World(source, bindingId, consumers);
    }

    private async Task EditSourceAsync(World world, int month, Func<Host, IBackgroundJobScheduler> jobs)
    {
        await using var host = NewHost();
        await new SourceDocumentChangeHook(host.Db, jobs(host))
            .OnSucceededAsync(
                new Dictionary<long, int[]> { [world.Source.DocumentId] = [SourceDependencyWorld.Period(month)] }, 0,
                CancellationToken.None);
    }

    /// <summary>Батьківська задача з черги в базі (claim → виконати → завершити); повертає (звернення до SQL, постановки каскаду).</summary>
    private async Task<(int Commands, int Calls)> RunParentAsync(World world)
    {
        await using var host = NewHost();
        var claimed = await host.ClaimLaneOfAsync<IRegistryImpactRecalculationJob>();
        Assert.NotNull(claimed);
        var result = await RunParentAsync(world, null, claimed.PayloadJson!);
        Assert.True(await host.Queue.CompleteAsync(claimed.Claim, CancellationToken.None));
        return result;
    }

    private async Task<(int Commands, int Calls)> RunParentAsync(World world, IBackgroundJobScheduler? quartz, string payload)
    {
        _ = world;
        await using var db = Sql.CreateContext();
        var trips = await RoundTrips.OpenAsync(db);
        var queue = new DbJobQueue(db, new SystemClock());
        IBackgroundJobScheduler jobs = quartz
                                       ?? new DbBackgroundJobScheduler(
                                           queue, new QuartzJobScheduler(null, new JobProgressStore(db), new SystemClock()),
                                           new JobQueueSignal());
        var trigger = new CalculationTrigger(jobs, new PeriodStore(db), new WorkflowStore(db), new DocumentStore(db), new DocumentSheetCopyStore(db));
        var graph = SourceDependencyWorld.Graph(db);
        await graph.GetAsync(CancellationToken.None); // знімок графа — кеш процесу, у вимір не входить
        trips.Reset();
        var progress = new QueuedProgress();
        await new RegistryImpactRecalculationJob(new RegistryImpactStore(db), trigger, graph: graph)
            .ExecuteAsync(payload, progress, CancellationToken.None);
        return (trips.Count, progress.Queued);
    }

    private async Task<(int Steps, int Commands)> DrainDbQueueAsync(int limit)
    {
        var steps = 0;
        var total = 0;
        var (claimTime, jobTime, handoffTime) = (new Stopwatch(), new Stopwatch(), new Stopwatch());
        while (steps < limit)
        {
            await using var host = new Host(Sql.CreateContext());
            var queueTrips = await RoundTrips.OpenAsync(host.Db);
            claimTime.Start();
            var step = await host.Queue.ClaimAsync([JobLanes.Recalc], "test/perf06b", JobQueueLimits.DefaultLease, CancellationToken.None);
            claimTime.Stop();
            if (step is null)
            {
                break;
            }

            await using var db = Sql.CreateContext();
            var jobTrips = await RoundTrips.OpenAsync(db);
            var job = NewJob(db, Scheduler(host), new FixedLease(step.Claim), host.Queue);
            try
            {
                jobTime.Start();
                await job.ExecuteAsync(step.PayloadJson, Substitute.For<IJobProgress>(), CancellationToken.None);
                jobTime.Stop();
                handoffTime.Start();
                Assert.True(await host.Queue.CompleteAsync(step.Claim, CancellationToken.None));
                handoffTime.Stop();
            }
            catch (JobContinuedException continued)
            {
                jobTime.Stop();
                handoffTime.Start();
                Assert.True(await host.Queue.ContinueAsync(
                    step.Claim, continued.PayloadJson, continued.Delay, continued.SettleThrough, CancellationToken.None));
                handoffTime.Stop();
            }

            steps++;
            total += queueTrips.Count + jobTrips.Count;
            if (steps % 200 == 0)
            {
                output.WriteLine(
                    $"  steps {steps}: claim {claimTime.ElapsedMilliseconds} ms, job {jobTime.ElapsedMilliseconds} ms, " +
                    $"continue/complete {handoffTime.ElapsedMilliseconds} ms (cumulative)");
            }
        }

        return (steps, total);
    }

    private async Task<(int Steps, int Commands)> DrainQuartzAsync(IScheduler scheduler, int limit)
    {
        var steps = 0;
        var total = 0;
        foreach (var (key, payload) in (await TakeQuartzAsync<IRecalculationJob>(scheduler)).Take(limit))
        {
            _ = key;
            await using var db = Sql.CreateContext();
            var trips = await RoundTrips.OpenAsync(db);
            await NewJob(db, scheduler: null, lease: null, queue: null)
                .ExecuteAsync(payload, Substitute.For<IJobProgress>(), CancellationToken.None);
            steps++;
            total += trips.Count;
        }

        return (steps, total);
    }

    private async Task<List<(string Key, string Payload)>> TakeQuartzAsync<TJob>(IScheduler scheduler)
        where TJob : IBackgroundJob
    {
        var taken = new List<(string, string)>();
        foreach (var key in await QuartzKeysAsync<TJob>(scheduler))
        {
            var detail = await scheduler.GetJobDetail(key);
            taken.Add((key.Name, detail!.JobDataMap.GetString(QuartzJobScheduler.PayloadKey)!));
            await scheduler.DeleteJob(key);
        }

        return taken;
    }

    private static async Task<List<JobKey>> QuartzKeysAsync<TJob>(IScheduler scheduler)
        where TJob : IBackgroundJob
        => [.. (await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup()))
            .Where(k => k.Name.StartsWith(typeof(TJob).Name + "~", StringComparison.Ordinal))
            .OrderBy(k => k.Name, StringComparer.Ordinal)];

    private async Task<(QuartzJobScheduler Jobs, IScheduler Quartz)> QuartzAsync()
    {
        var factory = new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-perf06b-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
        });
        var scheduler = await factory.GetScheduler();
        return (new QuartzJobScheduler(factory), scheduler);
    }

    private async Task<List<JobProgress>> CascadeRowsAsync(World world)
    {
        await using var db = Sql.CreateContext();
        var keys = world.Consumers
            .Select(id => DbBackgroundJobScheduler.TargetKeyOf<IRecalculationJob>(CalculationTrigger.SourceCascadeTargetOf(id, SourceDependencyWorld.Year)))
            .ToList();
        return await db.JobProgresses.AsNoTracking().Where(p => keys.Contains(p.TargetKey!) && p.State == "Queued").ToListAsync();
    }

    private async Task<List<JobProgress>> LiveRowsAsync()
    {
        await using var db = Sql.CreateContext();
        return await db.JobProgresses.AsNoTracking()
            .Where(p => p.Lane != null && (p.State == "Queued" || p.State == "Running"))
            .ToListAsync();
    }

    private RecalculationJob NewJob(EcrDbContext db, IBackgroundJobScheduler? scheduler, IJobLeaseContext? lease, IJobQueue? queue)
    {
        var clock = new TestClock(Now);
        return new RecalculationJob(
            db, new NoopRunner(), RunHandler(db, clock), FormulaService(), clock, jobs: scheduler, lease: lease, queue: queue);
    }

    private static DbBackgroundJobScheduler Scheduler(Host host)
        => new(host.Queue, new QuartzJobScheduler(null, new JobProgressStore(host.Db), new SystemClock()), new JobQueueSignal());

    private static RunCalculationHandler RunHandler(EcrDbContext db, TestClock clock)
        => new(
            Substitute.For<IPeriodStore>(), Substitute.For<IWorkflowStore>(), new CalculationResultStore(db, clock),
            Substitute.For<IBackgroundJobScheduler>(), new UnitOfWork(db), Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(), clock, Substitute.For<IAuditWriter>());

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
            Substitute.For<ICellStore>(), rows, Substitute.For<IPeriodStore>(), Substitute.For<IMetadataCache>(),
            Substitute.For<ITemplateVersionStore>(), Substitute.For<IFormulaEngine>(), units, Substitute.For<IRegistryStore>(),
            header, Substitute.For<IAuditWriter>(), new TestClock(Now), Substitute.For<IUnitOfWork>(), Substitute.For<ISheetEditGate>());
    }

    private sealed record World(TestDocument Source, int BindingId, IReadOnlyList<long> Consumers);

    /// <summary>Підсумок батьківської задачі: скільки періодів-споживачів поставлено (<c>jobs.registryImpactDone</c>, <c>queued</c>).</summary>
    private sealed class QueuedProgress : IJobProgress
    {
        public int Queued { get; private set; }

        public Task ReportAsync(int percent, string? message, CancellationToken ct)
        {
            if (JobProgressMessageCodec.TryDecode(message, out var envelope) && envelope.Params?.TryGetValue("queued", out var queued) == true)
            {
                Queued = int.Parse(queued, System.Globalization.CultureInfo.InvariantCulture);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class NoopRunner : ICalculationRunner
    {
        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
            => Task.FromResult(new ModuleProfile());
    }

    private sealed class FixedLease(JobClaimToken claim) : IJobLeaseContext
    {
        public JobClaimToken? Current { get; } = claim;
    }

    /// <summary>
    /// Справжні звернення до SQL Server на з'єднанні контексту (<c>SqlConnection</c> статистика <c>ServerRoundtrips</c>): і запити
    /// EF, і сирі команди черги в базі на тому самому з'єднанні.
    /// </summary>
    private sealed class RoundTrips(Microsoft.Data.SqlClient.SqlConnection connection)
    {
        public int Count => (int)(long)connection.RetrieveStatistics()["ServerRoundtrips"]!;

        public static async Task<RoundTrips> OpenAsync(EcrDbContext db)
        {
            await db.Database.OpenConnectionAsync();
            var connection = (Microsoft.Data.SqlClient.SqlConnection)db.Database.GetDbConnection();
            connection.StatisticsEnabled = true;
            connection.ResetStatistics();
            return new RoundTrips(connection);
        }

        public void Reset() => connection.ResetStatistics();
    }
}
