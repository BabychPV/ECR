// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationJobFanOutTests.cs
using System.Collections.Concurrent;
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
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
using NSubstitute;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// P4 ФВ-9.8 (D-206, CAL-01): перерахунок проєкту — ОРКЕСТРАТОР-ФАН-АУТ, який
/// ставить по документній задачі на документ і сам не рахує; документна задача
/// рахує лише свій документ і створює прогін «документ × період».
/// </summary>
/// <remarks>
/// Мутації, на яких ці тести червоніють:
/// <list type="bullet">
/// <item>дочірній payload без погодження (<c>new RecalculationRequest(ProjectId,
/// documentId, PeriodKey, TriggeredByUserId)</c> замість <c>request with { DocumentId }</c>)
/// — <see cref="Перерахунок_закритого_періоду_переносить_погодження_в_КОЖНУ_документну_задачу"/>;</item>
/// <item><c>NewRun</c> без <c>documentId</c> (прогін «проєкт × період») —
/// <see cref="Два_документи_паралельно_не_витісняють_актуальність_один_одного"/>;</item>
/// <item><c>EnqueueAsync</c> замість <c>EnqueueCoalescedAsync</c> — обидва тести коалесценції.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class RecalculationJobFanOutTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);

    private static readonly PeriodKey February = new(202602);

    private const decimal Nightly = 1_000m;

    private static readonly System.Text.Json.JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ПРД-13")]
    public async Task Нічний_перерахунок_проєкту_з_трьох_документів_ставить_три_документні_задачі_і_сам_не_рахує()
    {
        var (builder, document) = await ArrangeAsync(extraDocuments: 2);
        var documents = await DocumentIdsAsync(builder, document.ProjectId);
        Assert.Equal(3, documents.Count);

        var scheduler = new RecordingScheduler();
        var runner = new WritingRunner(builder, _methodologyVersionId, _unitId, Nightly);
        var progress = new RecordingProgress();

        await using (var db = builder.CreateContext())
        {
            await Job(db, runner, scheduler.Substitute).ExecuteAsync(
                new RecalculationRequest(document.ProjectId, 0, PeriodKey: null, TriggeredByUserId: null),
                progress,
                CancellationToken.None);
        }

        // ⛔ Три задачі — по одній на документ, з ціллю «документ, увесь рік».
        Assert.Equal(documents.Select(RecalculateDocumentHandler.YearTargetOf), scheduler.Calls.Select(c => c.Target));
        Assert.All(scheduler.Calls, c =>
        {
            Assert.Equal(document.ProjectId, c.Request.ProjectId);
            Assert.Null(c.Request.PeriodKey);
            Assert.Null(c.CreatedBy);
        });
        Assert.Equal(documents, scheduler.Calls.Select(c => c.Request.DocumentId));

        // ⛔ Батько не рахує: ні оркестратора, ні прогону.
        Assert.Empty(runner.Calls);
        await using (var verify = builder.CreateContext())
        {
            Assert.False(await verify.CalculationRuns.AnyAsync(r => r.ProjectId == document.ProjectId));
        }

        var last = progress.Reports[^1];
        Assert.Equal(100, last.Percent);
        Assert.True(JobProgressMessageCodec.TryDecode(last.Message, out var envelope));
        Assert.Equal("jobs.recalcFannedOut", envelope.Key);
        Assert.Equal("3", envelope.Params!["count"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дочірні_задачі_несуть_ідентифікатор_батька_для_похідного_стану()
    {
        var (builder, document) = await ArrangeAsync(extraDocuments: 1);
        var scheduler = new RecordingScheduler();

        await using (var db = builder.CreateContext())
        {
            await Job(db, new WritingRunner(builder, _methodologyVersionId, _unitId, Nightly), scheduler.Substitute)
                .ExecuteAsync(
                    new RecalculationRequest(document.ProjectId, 0, null, null),
                    new IdentifiedProgress("IRecalculationJob-parent1"),
                    CancellationToken.None);
        }

        // ⛔ Без цього поля статус батька не знайде дітей і покаже «виконано» на розкладі.
        Assert.Equal(2, scheduler.Calls.Count);
        Assert.All(scheduler.Calls, c => Assert.Equal("IRecalculationJob-parent1", c.Request.FanOutParentJobId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.7")]
    public async Task Перерахунок_закритого_періоду_переносить_погодження_в_КОЖНУ_документну_задачу()
    {
        var (builder, document) = await ArrangeAsync(extraDocuments: 2);
        var documents = await DocumentIdsAsync(builder, document.ProjectId);
        var scheduler = new RecordingScheduler();

        // Payload рівно тієї форми, яку кладе в чергу `RunCalculationHandler`, — JSON із черги.
        var payload = JsonSerializer.Serialize(
            new
            {
                projectId = document.ProjectId,
                periodKey = document.PeriodKey.Value,
                triggeredByUserId = 7,
                approvedBy = 11,
                approvalReason = "виправлення подання",
                approvalId = 42L,
                requestedAt = Now,
            },
            WebJson);

        await using (var db = builder.CreateContext())
        {
            await Job(db, new WritingRunner(builder, _methodologyVersionId, _unitId, Nightly), scheduler.Substitute)
                .ExecuteAsync(payload, new RecordingProgress(), CancellationToken.None);
        }

        Assert.Equal(
            documents.Select(id => RecalculateDocumentHandler.TargetOf(id, document.PeriodKey)),
            scheduler.Calls.Select(c => c.Target));

        // ⛔ Чотири ока (аудит S1): без погодження в дочірній задачі гейт стану
        // періоду відмовив би кожному документу законно погодженого перерахунку.
        Assert.All(scheduler.Calls, c =>
        {
            Assert.Equal(11, c.Request.ApprovedBy);
            Assert.Equal(42L, c.Request.ApprovalId);
            Assert.Equal("виправлення подання", c.Request.ApprovalReason);
            Assert.Equal(7, c.Request.TriggeredByUserId);
            Assert.Equal(document.PeriodKey.Value, c.Request.PeriodKey);

            // Автор бачить дочірні задачі у «Моїх задачах» (Q-156).
            Assert.Equal(7, c.CreatedBy);
        });
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.11")]
    public async Task Документні_задачі_рахують_лише_свій_документ_і_дають_один_Current_на_документ_і_період()
    {
        var (builder, document) = await ArrangeAsync(extraDocuments: 2);
        var documents = await DocumentIdsAsync(builder, document.ProjectId);
        var scheduler = new RecordingScheduler();

        await using (var db = builder.CreateContext())
        {
            await Job(db, new WritingRunner(builder, _methodologyVersionId, _unitId, Nightly), scheduler.Substitute)
                .ExecuteAsync(
                    new RecalculationRequest(document.ProjectId, 0, null, null),
                    new RecordingProgress(),
                    CancellationToken.None);
        }

        foreach (var call in scheduler.Calls)
        {
            var runner = new WritingRunner(builder, _methodologyVersionId, _unitId, Nightly);

            await using var db = builder.CreateContext();
            await Job(db, runner, scheduler.Substitute)
                .ExecuteAsync(call.Request, new RecordingProgress(), CancellationToken.None);

            // ⛔ Лише свій документ — обидва періоди, по порядку.
            Assert.Equal(
                [(call.Request.DocumentId, document.PeriodKey.Value), (call.Request.DocumentId, February.Value)],
                runner.Calls.Select(c => (c.DocumentId, c.Period)));
        }

        // Документна задача НЕ ставить нових задач — розклад лише в батька.
        Assert.Equal(3, scheduler.Calls.Count);

        await AssertVisibleAndSingleCurrentAsync(builder, document, documents);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ПРД-13")]
    public async Task Два_документи_паралельно_не_витісняють_актуальність_один_одного()
    {
        var (builder, document) = await ArrangeAsync(extraDocuments: 1);
        var documents = await DocumentIdsAsync(builder, document.ProjectId);
        var scheduler = new RecordingScheduler();

        await using (var db = builder.CreateContext())
        {
            await Job(db, new WritingRunner(builder, _methodologyVersionId, _unitId, Nightly), scheduler.Substitute)
                .ExecuteAsync(
                    new RecalculationRequest(document.ProjectId, 0, null, null),
                    new RecordingProgress(),
                    CancellationToken.None);
        }

        // ⛔ Два виконання одночасно: кожен оркестратор чекає, доки обидва не
        // ввійдуть у свій перший період, — інакше «паралельно» було б лише назвою.
        var rendezvous = new Rendezvous(scheduler.Calls.Count);

        async Task RunAsync(RecalculationRequest request)
        {
            await using var db = builder.CreateContext();
            var runner = new WritingRunner(builder, _methodologyVersionId, _unitId, Nightly) { Rendezvous = rendezvous };
            await Job(db, runner, scheduler.Substitute).ExecuteAsync(request, new RecordingProgress(), CancellationToken.None);
        }

        await Task.WhenAll(scheduler.Calls.Select(c => Task.Run(() => RunAsync(c.Request))));

        Assert.True(rendezvous.Met, "документні задачі не виконувались одночасно");

        await AssertVisibleAndSingleCurrentAsync(builder, document, documents);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повторний_нічний_запуск_зливається_з_документними_задачами_в_черзі_Quartz()
    {
        var (builder, document) = await ArrangeAsync(extraDocuments: 2);

        var factory = new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-p4-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
        });

        // Планувальник НЕ стартує: документні задачі лишаються в черзі.
        var quartz = await factory.GetScheduler();

        try
        {
            var jobs = new QuartzJobScheduler(factory);

            for (var run = 0; run < 2; run++)
            {
                await using var db = builder.CreateContext();
                await Job(db, new WritingRunner(builder, _methodologyVersionId, _unitId, Nightly), jobs)
                    .ExecuteAsync(
                        new RecalculationRequest(document.ProjectId, 0, null, null),
                        new RecordingProgress(),
                        CancellationToken.None);
            }

            var keys = (await quartz.GetJobKeys(GroupMatcher<JobKey>.AnyGroup())).Select(k => k.Name).ToList();

            // ⛔ Три, а не шість: друга постановка на кожну ціль злилася з першою.
            Assert.Equal(3, keys.Count);
            foreach (var documentId in await DocumentIdsAsync(builder, document.ProjectId))
            {
                Assert.Single(keys, k => k.StartsWith(
                    $"{nameof(IRecalculationJob)}~{RecalculateDocumentHandler.YearTargetOf(documentId)}~",
                    StringComparison.Ordinal));
            }
        }
        finally
        {
            await quartz.Shutdown(waitForJobsToComplete: false);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повторний_нічний_запуск_зливається_з_документними_задачами_в_черзі_бази()
    {
        var (builder, document) = await ArrangeAsync(extraDocuments: 2);
        var documents = await DocumentIdsAsync(builder, document.ProjectId);
        var targets = documents
            .Select(id => $"{nameof(IRecalculationJob)}~{RecalculateDocumentHandler.YearTargetOf(id)}")
            .ToList();

        try
        {
            for (var run = 0; run < 2; run++)
            {
                await using var db = builder.CreateContext();
                var jobs = new DbBackgroundJobScheduler(
                    new DbJobQueue(db, new SystemClock()),
                    new QuartzJobScheduler(null, new JobProgressStore(db), new SystemClock()),
                    new JobQueueSignal());

                await Job(db, new WritingRunner(builder, _methodologyVersionId, _unitId, Nightly), jobs)
                    .ExecuteAsync(
                        new RecalculationRequest(document.ProjectId, 0, null, null),
                        new RecordingProgress(),
                        CancellationToken.None);
            }

            await using var verify = builder.CreateContext();
            var queued = await verify.JobProgresses.AsNoTracking()
                .Where(p => targets.Contains(p.TargetKey!))
                .Select(p => new { p.TargetKey, p.State, p.DocumentId })
                .ToListAsync();

            // ⛔ По ОДНІЙ задачі в черзі на документ.
            Assert.Equal(targets.Order(StringComparer.Ordinal), queued.Select(q => q.TargetKey!).Order(StringComparer.Ordinal));
            Assert.All(queued, q => Assert.Equal("Queued", q.State));
            Assert.Equal(documents, queued.Select(q => q.DocumentId!.Value).Order());
        }
        finally
        {
            await using var cleanup = builder.CreateContext();
            await cleanup.JobProgresses.Where(p => targets.Contains(p.TargetKey!)).ExecuteDeleteAsync();
        }
    }

    /// <summary>Результати видно в кожному періоді кожного документа; Current — рівно один на документ×період.</summary>
    private static async Task AssertVisibleAndSingleCurrentAsync(
        TestDocumentBuilder builder, TestDocument document, List<long> documents)
    {
        await using var db = builder.CreateContext();
        var store = new CalculationResultStore(db, new TestClock(Now));

        foreach (var documentId in documents)
        {
            foreach (var period in new[] { document.PeriodKey, February })
            {
                Assert.Equal(
                    Expected(Nightly, period),
                    Shape(await store.ReadCurrentAsync(documentId, period.Value, default)));
            }
        }

        var current = await db.CalculationRuns.AsNoTracking()
            .Where(r => r.ProjectId == document.ProjectId && r.Status == CalculationRun.CurrentStatus)
            .Select(r => new { r.DocumentId, r.PeriodKey })
            .ToListAsync();

        Assert.Equal(documents.Count * 2, current.Count);
        Assert.All(current, r => Assert.NotNull(r.DocumentId));
        Assert.All(current.GroupBy(r => (r.DocumentId, r.PeriodKey)), g => Assert.Single(g));
    }

    private static List<string> Expected(decimal label, PeriodKey period)
        =>
        [
            $"R-1|W_COMP|{label + period.Sequence:0.####}",
            $"R-1|tons|{label + period.Sequence:0.####}",
        ];

    private static List<string> Shape(IReadOnlyList<CalculationResultRow> rows)
        => [.. rows
            .Select(r => $"{r.SourceRowKey}|{r.OutputCode}|{r.Value:0.####}")
            .Order(StringComparer.Ordinal)];

    private static async Task<List<long>> DocumentIdsAsync(TestDocumentBuilder builder, int projectId)
    {
        await using var db = builder.CreateContext();
        return await db.Documents.AsNoTracking()
            .Where(d => d.ProjectId == projectId)
            .OrderBy(d => d.Id)
            .Select(d => d.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Проєкт із <c>1 + extraDocuments</c> документами, два періоди (січень, лютий),
    /// у кожного документа — екземпляр таблиці в кожному періоді, активна прив'язка
    /// методології до таблиці.
    /// </summary>
    private async Task<(TestDocumentBuilder Builder, TestDocument Document)> ArrangeAsync(int extraDocuments)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        await using var db = builder.CreateContext();

        db.Periods.Add(new Period(
            document.ProjectId, February, (byte)February.Sequence,
            new DateOnly(February.Year, February.Sequence, 1),
            new DateOnly(February.Year, February.Sequence, DateTime.DaysInMonth(February.Year, February.Sequence))));

        var extra = new List<Document>();
        for (var i = 0; i < extraDocuments; i++)
        {
            var added = new Document(document.ProjectId, $"P4-{Guid.NewGuid():N}"[..20], 1, Now);
            extra.Add(added);
            db.Documents.Add(added);
        }

        await db.SaveChangesAsync();

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceIds = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1 + (extraDocuments * 2), CancellationToken.None);

        db.TableInstances.Add(new TableInstance(February, instanceIds++, document.DocumentId, document.TableDefId, Now));
        foreach (var added in extra)
        {
            db.TableInstances.Add(new TableInstance(document.PeriodKey, instanceIds++, added.Id, document.TableDefId, Now));
            db.TableInstances.Add(new TableInstance(February, instanceIds++, added.Id, document.TableDefId, Now));
        }

        var tag = Guid.NewGuid().ToString("N")[..8];
        var methodology = new Methodology(
            EcrCode.Create($"P4_{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
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

    private int _methodologyVersionId;

    private int _unitId;

    private static RecalculationJob Job(EcrDbContext db, ICalculationRunner runner, IBackgroundJobScheduler jobs)
    {
        var clock = new TestClock(Now);
        return new RecalculationJob(db, runner, RunHandler(db, clock), FormulaService(), clock, jobs);
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
            Substitute.For<IRecalculationApprovalStore>(),
            Substitute.For<IAuditWriter>());

    /// <summary>Фаза формул шаблону нічого не пише — тести про розклад і прогони.</summary>
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

    /// <summary>Планувальник, що запам'ятовує документні постановки.</summary>
    private sealed class RecordingScheduler
    {
        public RecordingScheduler()
        {
            Substitute.EnqueueCoalescedAsync<IRecalculationJob>(
                    Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
                .Returns(ci =>
                {
                    lock (Calls)
                    {
                        Calls.Add(new EnqueueCall(
                            ci.ArgAt<string>(0), Assert.IsType<RecalculationRequest>(ci.ArgAt<object?>(1)), ci.ArgAt<int?>(3)));
                    }

                    return Task.FromResult($"IRecalculationJob~{ci.ArgAt<string>(0)}~x");
                });
        }

        public IBackgroundJobScheduler Substitute { get; } = NSubstitute.Substitute.For<IBackgroundJobScheduler>();

        public List<EnqueueCall> Calls { get; } = [];
    }

    private sealed record EnqueueCall(string Target, RecalculationRequest Request, int? CreatedBy);

    /// <summary>Точка зустрічі: кожен учасник чекає, доки не прийдуть усі.</summary>
    private sealed class Rendezvous(int parties)
    {
        private readonly TaskCompletionSource all = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int arrived;

        public bool Met => all.Task.IsCompletedSuccessfully;

        public async Task ArriveAsync()
        {
            if (Interlocked.Increment(ref arrived) == parties)
            {
                all.TrySetResult();
            }

            await all.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
    }

    /// <summary>
    /// Фейк оркестратора: пише вихід і проміжне значення через СПРАВЖНЄ сховище,
    /// власним контекстом — як <c>CalculationOutputWriter</c>.
    /// </summary>
    private sealed class WritingRunner(
        TestDocumentBuilder builder, int methodologyVersionId, int unitId, decimal label) : ICalculationRunner
    {
        private int entered;

        public Rendezvous? Rendezvous { get; init; }

        public ConcurrentQueue<(long DocumentId, int Period)> Calls { get; } = new();

        public async Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
        {
            Calls.Enqueue((documentId, periodKey.Value));

            if (Rendezvous is { } meet && Interlocked.Increment(ref entered) == 1)
            {
                await meet.ArriveAsync();
            }

            if (bindings.Count == 0)
            {
                return new ModuleProfile();
            }

            var value = label + periodKey.Sequence;

            await using var db = builder.CreateContext();
            var store = new CalculationResultStore(db, new TestClock(Now));

            await store.WriteResultsAsync(
                calculationRunId,
                [
                    new CalculationOutput(documentId, "R-1",
                    [
                        new CalculationOutputValue(methodologyVersionId, null, "tons", value, unitId),
                        new CalculationOutputValue(
                            methodologyVersionId, null, "W_COMP", value, unitId, CalculationResultKind.Intermediate),
                    ], []),
                ],
                ct);
            await db.SaveChangesAsync(ct);

            return new ModuleProfile();
        }
    }

    private sealed class IdentifiedProgress(string jobId) : IJobProgress, IJobIdentity
    {
        public string JobId => jobId;

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingProgress : IJobProgress
    {
        public List<(int Percent, string? Message)> Reports { get; } = [];

        public Task ReportAsync(int percent, string? message, CancellationToken ct)
        {
            Reports.Add((percent, message));
            return Task.CompletedTask;
        }
    }
}
