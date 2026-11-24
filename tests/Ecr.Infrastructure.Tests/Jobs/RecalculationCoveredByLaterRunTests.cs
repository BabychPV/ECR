// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationCoveredByLaterRunTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ PSS-06 (аудит 20.11, <c>D-561</c>; рішення людини: «якщо в черзі накопичуються перерахунки одного документа й
/// періоду — зливати»): перерахунок документа за період, що стартував, «паркує» задачі, які чекають на той самий
/// документ і період під ІНШИМИ цілями (кнопка, область аркуша, авто, залежні, прохід читача, дитина розкладу) і чию
/// роботу він робить з надлишком. Запарковані claim не бере; закінчився він успіхом — вони <c>Succeeded</c>, ні —
/// знову в черзі. Нова постановка на ціль запаркованої повертає її (вона новіша за читання покривної).
/// </summary>
/// <remarks>
/// Детерміновано, без таймінгу: «виконується» — це claim.
/// <para>
/// МУТАЦІЇ (локально): (1) без виклику <c>ParkCoveredAsync</c> для названого періоду в <c>RecalculationJob.ExecuteAsync</c>
/// — червоний <c>Прогін_паркує_покриті_варіанти_цілей_до_першого_читання_входів</c>; (2) <c>Covers</c> без перевірки
/// <c>IncludeApproved</c> — червоні теорія й той самий тест (затверджена запаркована); (3) <c>CompleteAsync</c> без
/// <c>ParkedOutcome.Settle</c> — червоний <c>Успіх_покривної_закриває…</c>; (4) <c>FailAsync</c> без <c>Restore</c> —
/// червоний <c>Провал_покривної…</c>; (5) у <c>EnqueueSql</c> без зняття конверта/моменту паркування — червоний
/// <c>Нова_постановка…</c>; (6) <c>SettleParkedSql</c> без <c>@settleThrough</c> — червоний <c>Крок_ланцюжка…</c>.
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "PSS-06")]
public sealed class RecalculationCoveredByLaterRunTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private const long Doc = 9_500_001;

    [Theory]
    [InlineData(null, false, false, 0, null, false, false, 0, true)]   // ціле покриває ціле
    [InlineData(null, false, false, 0, 5, false, false, 0, true)]      // ціле покриває аркуш
    [InlineData(5, false, false, 0, null, false, false, 0, false)]     // аркуш не покриває ціле
    [InlineData(5, false, false, 0, 6, false, false, 0, false)]        // аркуш не покриває інший аркуш
    [InlineData(null, true, false, 0, null, false, false, 0, true)]    // «з затвердженими» покриває звичайну
    [InlineData(null, false, false, 0, null, true, false, 0, false)]   // звичайна не покриває «з затвердженими»
    [InlineData(null, false, false, 0, null, false, true, 0, true)]    // звичайний прогін покриває прохід читача
    [InlineData(null, false, true, 0, null, false, false, 0, false)]   // прохід не покриває звичайний (не ставить проходів)
    [InlineData(null, false, false, 1, null, false, false, 2, true)]   // менша ланка джерел покриває більшу
    [InlineData(null, false, false, 2, null, false, false, 1, false)]  // більша — ні (хук упреться в стелю раніше)
    public void Що_покриває_прогін_того_самого_документа_й_періоду(
        int? coverSheet, bool coverApproved, bool coverPass, int coverHop,
        int? sheet, bool approved, bool pass, int hop, bool covers)
    {
        var coverer = new RecalculationRequest(
            1, Doc, 202603, null, SheetDefId: coverSheet, ForeignOwnerPass: coverPass, SourceHop: coverHop,
            IncludeApproved: coverApproved);
        var candidate = new RecalculationRequest(
            1, Doc, 202603, null, SheetDefId: sheet, ForeignOwnerPass: pass, SourceHop: hop, IncludeApproved: approved);

        Assert.Equal(covers, RecalculationJob.Covers(coverer, 202603, candidate));

        // Інший період, інший документ, задача «за рік» — не покриваються ніколи.
        Assert.False(RecalculationJob.Covers(coverer, 202603, candidate with { PeriodKey = 202604 }));
        Assert.False(RecalculationJob.Covers(coverer, 202603, candidate with { DocumentId = Doc + 1 }));
        Assert.False(RecalculationJob.Covers(coverer, 202603, candidate with { PeriodKey = null, Year = 2026 }));
        Assert.False(RecalculationJob.Covers(coverer, 202603, candidate with { ChainAfter = 202602 }));
    }

    /// <summary>✎ Огляд L6: задачу людини покриває лише прогін тієї самої людини (журнал і задача, яку вона опитує).</summary>
    [Theory]
    [InlineData(null, null, true)]
    [InlineData(7, null, true)]
    [InlineData(7, 7, true)]
    [InlineData(null, 7, false)]
    [InlineData(8, 7, false)]
    public void Задачу_людини_покриває_лише_прогін_тієї_самої_людини(int? coverUser, int? candidateUser, bool covers)
    {
        var coverer = new RecalculationRequest(1, Doc, 202603, coverUser);
        Assert.Equal(covers, RecalculationJob.Covers(coverer, 202603, new RecalculationRequest(1, Doc, 202603, candidateUser)));

        // Перекриття адміністратора: те саме за автором перекриття.
        var approvedCoverer = coverer with { IncludeApproved = true, OverrideUserId = 9 };
        var approved = new RecalculationRequest(1, Doc, 202603, null, IncludeApproved: true, OverrideUserId: 9);
        Assert.True(RecalculationJob.Covers(approvedCoverer, 202603, approved));
        Assert.False(RecalculationJob.Covers(approvedCoverer, 202603, approved with { OverrideUserId = 10 }));
    }

    /// <remarks>
    /// ✎ Огляд M1: <c>Succeeded</c> — лише за періодом, який покривна явно назвала порахованим (<see cref="IJobQueue.SettleParkedAsync"/>);
    /// решту її кінець повертає в чергу — на колишнє місце FIFO (огляд L8).
    /// </remarks>
    [Fact]
    public async Task Запаркована_не_береться_закривається_лише_за_порахованим_періодом_решта_повертається_на_своє_місце()
    {
        await using var host = NewHost();
        var coverer = await ClaimCovererAsync(host);
        var covered = await EnqueueRecalcAsync("auto");
        var notComputed = await EnqueueRecalcAsync("other-period");
        var originalAt = (await RowAsync(notComputed))!.AvailableAt;

        Assert.Equal(
            new[] { covered, notComputed }.Order(),
            (await host.Queue.ParkCoveredAsync(
                coverer, [new CoveredJob(covered, 202603), new CoveredJob(notComputed, 202605)], CancellationToken.None)).Order());
        AssertParked(await RowAsync(covered), coverer.JobId);
        Assert.Null(await ClaimRecalcAsync(host));

        Assert.Equal(1, await host.Queue.SettleParkedAsync(coverer, 202603, CancellationToken.None));
        Assert.True(await host.Queue.CompleteAsync(coverer, CancellationToken.None));

        var done = await RowAsync(covered);
        Assert.Equal(("Succeeded", 100), (done!.State, done.Percent));
        Assert.True(JobProgressMessageCodec.TryDecode(done.Message, out var envelope));
        Assert.Equal(IJobQueue.ParkedMessageKey, envelope.Key);

        // Період 202605 покривна не рахувала — задача знову в черзі, на тому самому місці FIFO.
        var back = await RowAsync(notComputed);
        AssertQueuedPlain(back);
        Assert.Equal(originalAt, back!.AvailableAt);
    }

    [Fact]
    public async Task Провал_чи_скасування_покривної_повертає_запарковані_в_чергу()
    {
        await using var host = NewHost();
        var failing = await ClaimCovererAsync(host);
        var a = await EnqueueRecalcAsync("a");
        var aAt = (await RowAsync(a))!.AvailableAt;
        await host.Queue.ParkCoveredAsync(failing, [new CoveredJob(a, 202603)], CancellationToken.None);
        Assert.True(await host.Queue.FailAsync(failing, "впала", null, CancellationToken.None));
        AssertQueuedPlain(await RowAsync(a));
        Assert.Equal(aAt, (await RowAsync(a))!.AvailableAt);
        Assert.Equal(a, (await ClaimRecalcAsync(host))?.Claim.JobId);

        var cancelled = await ClaimCovererAsync(host);
        var b = await EnqueueRecalcAsync("b");
        await host.Queue.ParkCoveredAsync(cancelled, [new CoveredJob(b, 202603)], CancellationToken.None);
        Assert.True(await host.Queue.AcknowledgeCancelAsync(cancelled, CancellationToken.None));
        AssertQueuedPlain(await RowAsync(b));

        // Повернута в чергу ретраєм покривна теж віддає запарковані — вони не чекають її повтору.
        var retried = await ClaimCovererAsync(host);
        var c = await EnqueueRecalcAsync("c");
        await host.Queue.ParkCoveredAsync(retried, [new CoveredJob(c, 202603)], CancellationToken.None);
        Assert.True(await host.Queue.RequeueAsync(retried, TimeSpan.FromMinutes(1), CancellationToken.None));
        AssertQueuedPlain(await RowAsync(c));
    }

    [Fact]
    public async Task Нова_постановка_на_ціль_запаркованої_повертає_її_в_чергу()
    {
        await using var host = NewHost();
        var coverer = await ClaimCovererAsync(host);
        var target = Target();
        var covered = await EnqueueRecalcAsync("x", target);
        await host.Queue.ParkCoveredAsync(coverer, [new CoveredJob(covered, 202603)], CancellationToken.None);

        // Зміна, що прийшла ПІСЛЯ паркування (покривна могла вже прочитати входи), — робота запаркованої знову потрібна.
        var again = await EnqueueRecalcAsync("x", target);
        Assert.Equal(covered, again);
        AssertQueuedPlain(await RowAsync(covered));

        // І успіх покривної її вже не закриває.
        Assert.True(await host.Queue.CompleteAsync(coverer, CancellationToken.None));
        Assert.Equal("Queued", (await RowAsync(covered))!.State);
    }

    [Fact]
    public async Task Паркує_лише_покривна_з_живою_орендою_і_лише_звичайні_Queued()
    {
        await using var host = NewHost();
        var coverer = await ClaimCovererAsync(host);
        var running = await EnqueueRecalcAsync("running");
        var runningClaim = (await ClaimRecalcAsync(host))!.Claim;
        Assert.Equal(running, runningClaim.JobId);

        // Виконувана не паркується; чужий (протермінований) токен не паркує нічого.
        Assert.Empty(await host.Queue.ParkCoveredAsync(coverer, [new CoveredJob(running, 202603)], CancellationToken.None));
        var queued = await EnqueueRecalcAsync("queued");
        Assert.Empty(await host.Queue.ParkCoveredAsync(
            coverer with { Token = Guid.NewGuid() }, [new CoveredJob(queued, 202603)], CancellationToken.None));
        Assert.Equal("Queued", (await RowAsync(queued))!.State);
        AssertQueuedPlain(await RowAsync(queued));
    }

    [Fact]
    public async Task Крок_ланцюжка_закриває_лише_порахований_період_незроблені_раніші_повертає_пізніші_чекають()
    {
        await using var host = NewHost();
        var chain = await ClaimCovererAsync(host);
        var february = await EnqueueRecalcAsync("m2");
        var march = await EnqueueRecalcAsync("m3");
        var may = await EnqueueRecalcAsync("m5");
        await host.Queue.ParkCoveredAsync(
            chain,
            [new CoveredJob(february, 202602), new CoveredJob(march, 202603), new CoveredJob(may, 202605)],
            CancellationToken.None);

        // Крок порахував березень; лютий (запаркований раніше) ланцюжок не рахував — напр. період закрили.
        await host.Queue.SettleParkedAsync(chain, 202603, CancellationToken.None);
        Assert.True(await host.Queue.ContinueAsync(chain, "{\"step\":2}", TimeSpan.Zero, 202603, CancellationToken.None));
        Assert.Equal("Succeeded", (await RowAsync(march))!.State);
        AssertQueuedPlain(await RowAsync(february));
        AssertParked(await RowAsync(may), chain.JobId);

        // Ланцюжок між кроками — Queued; прибирання осиротілих його запаркованих не чіпає.
        Assert.Empty(await host.Queue.ExpireEndedAsync(JobQueueLimits.MaxReclaims, CancellationToken.None));
        AssertParked(await RowAsync(may), chain.JobId);

        // Наступний крок — той самий рядок, новий payload, спроба не рахується.
        var step2 = await ClaimDefaultAsync(host);
        Assert.Equal((chain.JobId, "{\"step\":2}", 1), (step2!.Claim.JobId, step2.PayloadJson, step2.Attempt));

        // Травень ланцюжок так і не порахував (закінчився раніше) — кінець повертає його в чергу, а не «Succeeded».
        Assert.True(await host.Queue.CompleteAsync(step2.Claim, CancellationToken.None));
        AssertQueuedPlain(await RowAsync(may));
    }

    [Fact]
    public async Task Ланцюжок_скасований_між_кроками_чи_втрачений_віддає_запарковані()
    {
        await using var host = NewHost();
        var chain = await ClaimCovererAsync(host);
        var later = await EnqueueRecalcAsync("later");
        await host.Queue.ParkCoveredAsync(chain, [new CoveredJob(later, 202605)], CancellationToken.None);
        Assert.True(await host.Queue.ContinueAsync(chain, "{}", TimeSpan.FromMinutes(5), 202603, CancellationToken.None));

        Assert.Equal(CancelOutcome.Cancelled, await host.Queue.RequestCancelAsync(chain.JobId, CancellationToken.None));
        AssertQueuedPlain(await RowAsync(later));

        // Осиротілі (покривна вже не Running і не Queued — напр. збій між її кінцем і долею запаркованих) повертає прибирання.
        var dead = await ClaimCovererAsync(host);
        var orphan = await EnqueueRecalcAsync("orphan");
        await host.Queue.ParkCoveredAsync(dead, [new CoveredJob(orphan, 202603)], CancellationToken.None);
        await ExecAsync("UPDATE itg.JobProgress SET [State] = 'Failed' WHERE JobId = @id;", dead.JobId);
        await host.Queue.ExpireEndedAsync(JobQueueLimits.MaxReclaims, CancellationToken.None);
        AssertQueuedPlain(await RowAsync(orphan));
    }

    [Fact]
    [Trait("Finding", "PSS-06-JOB")]
    public async Task Прогін_паркує_покриті_варіанти_цілей_до_першого_читання_входів()
    {
        var builder = new TestDocumentBuilder(Sql.ConnectionString);
        var document = await builder.BuildAsync();
        var period = new PeriodKey(document.PeriodKey!.Value);

        await using var host = NewHost();
        var scheduler = new DbBackgroundJobScheduler(
            host.Queue, new QuartzJobScheduler(null, new JobProgressStore(host.Db), new SystemClock()), new JobQueueSignal());

        // Покривна — справжній рядок черги з живою орендою (кнопка «Перерахувати» документа за період).
        var covererId = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            RecalculateDocumentHandler.TargetOf(document.DocumentId, period),
            new { document.DocumentId, PeriodKey = period.Value, TriggeredByUserId = 7 }, CancellationToken.None, 7);
        var claim = (await ClaimRecalcAsync(host))!.Claim;
        Assert.Equal(covererId, claim.JobId);

        // Ті самі документ і період під іншими цілями — поставлені, поки покривна чекала лок.
        var auto = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            CalculationTrigger.AutoTargetOf(document.DocumentId, period),
            new { document.DocumentId, PeriodKey = period.Value, RecalculateSubmitted = true }, CancellationToken.None);
        var dependents = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            CalculationTrigger.DependentsTargetOf(document.DocumentId, period),
            new { document.DocumentId, PeriodKey = period.Value, RecalculateSubmitted = true, SourceHop = 1 }, CancellationToken.None);
        var sheet = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            RecalculateDocumentHandler.ScopedTargetOf(document.DocumentId, period, 77),
            new { document.DocumentId, PeriodKey = period.Value, SheetDefId = 77 }, CancellationToken.None);

        // Не покриті: «з затвердженими» (ширша), інший період, кнопка ІНШОЇ людини (огляд L6).
        var otherUser = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            RecalculateDocumentHandler.ScopedTargetOf(document.DocumentId, period, 78),
            new { document.DocumentId, PeriodKey = period.Value, SheetDefId = 78, TriggeredByUserId = 8 }, CancellationToken.None, 8);
        var approved = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            CalculationTrigger.DependentsApprovedTargetOf(document.DocumentId, period),
            new { document.DocumentId, PeriodKey = period.Value, IncludeApproved = true, OverrideUserId = 9, OverrideReason = "Акт" },
            CancellationToken.None);
        var otherPeriod = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            RecalculateDocumentHandler.TargetOf(document.DocumentId, new PeriodKey(period.Value + 1)),
            new { document.DocumentId, PeriodKey = period.Value + 1 }, CancellationToken.None);

        var atFirstRead = new Dictionary<string, bool>();
        await RunAsync(builder, scheduler, host.Queue, document, period, claim, onFirstInputRead: () =>
        {
            if (atFirstRead.Count > 0)
            {
                return;
            }

            foreach (var id in new[] { auto, dependents, sheet, approved, otherPeriod, otherUser })
            {
                atFirstRead[id] = IsParked(RowAsync(id).GetAwaiter().GetResult(), claim.JobId);
            }
        });

        Assert.True(atFirstRead[auto], "авто не запарковано до першого читання");
        Assert.True(atFirstRead[dependents], "залежні не запарковано до першого читання");
        Assert.True(atFirstRead[sheet], "аркуш не запарковано до першого читання");
        Assert.False(atFirstRead[approved], "«з затвердженими» не покривається звичайним прогоном");
        Assert.False(atFirstRead[otherPeriod], "інший період не покривається");
        Assert.False(atFirstRead[otherUser], "кнопку іншої людини не покриває прогін цієї");

        // Прогін на заглушках падає — виконавець закрив би покривну Failed, і запарковані повертаються.
        Assert.True(await host.Queue.FailAsync(claim, "заглушки", null, CancellationToken.None));
        foreach (var id in new[] { auto, dependents, sheet })
        {
            AssertQueuedPlain(await RowAsync(id));
        }
    }

    private static async Task RunAsync(
        TestDocumentBuilder builder, IBackgroundJobScheduler scheduler, IJobQueue queue, TestDocument document,
        PeriodKey period, JobClaimToken claim, Action onFirstInputRead)
    {
        var clock = new TestClock(new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc));
        await using var db = builder.CreateContext();
        var job = new RecalculationJob(
            db, Substitute.For<ICalculationRunner>(), RunHandler(db, clock), FormulaService(onFirstInputRead), clock,
            jobs: scheduler, lease: new FixedLease(claim), queue: queue);

        try
        {
            await job.ExecuteAsync(
                new RecalculationRequest(document.ProjectId, document.DocumentId, period.Value, TriggeredByUserId: 7),
                new NoOpProgress(), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
        }
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
            clock);

    /// <summary>Фаза формул: перше звернення до екземплярів таблиць — «перше читання входів» прогону.</summary>
    private static RecalculationService FormulaService(Action onFirstInputRead)
    {
        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                onFirstInputRead();
                return Task.FromResult<IReadOnlyList<TableInstanceRef>>([]);
            });

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

    /// <summary>
    /// Покривна з живою орендою — у лейні <c>default</c>: запарковані й повернуті задачі лейна <c>recalc</c> не стають у FIFO
    /// перед нею (паркування звіряє лише оренду покривної, не її лейн).
    /// </summary>
    private async Task<JobClaimToken> ClaimCovererAsync(Host host)
    {
        string id;
        await using (var enqueue = NewHost())
        {
            id = (await enqueue.Queue.EnqueueAsync(
                new JobEnqueueRequest(Code, JobLanes.Default, "{\"coverer\":true}"), CancellationToken.None)).JobId;
        }

        var claimed = await ClaimDefaultAsync(host);
        Assert.Equal(id, claimed?.Claim.JobId);
        return claimed!.Claim;
    }

    private static Task<ClaimedJob?> ClaimDefaultAsync(Host host)
        => host.Queue.ClaimAsync([JobLanes.Default], "test/cover", JobQueueLimits.DefaultLease, CancellationToken.None);

    private static Task<ClaimedJob?> ClaimRecalcAsync(Host host)
        => host.Queue.ClaimAsync([JobLanes.Recalc], "test/cover", JobQueueLimits.DefaultLease, CancellationToken.None);

    private async Task<string> EnqueueRecalcAsync(string tag, string? target = null)
    {
        await using var host = NewHost();
        return (await host.Queue.EnqueueAsync(
                new JobEnqueueRequest(Code, JobLanes.Recalc, $"{{\"tag\":\"{tag}\"}}", target), CancellationToken.None))
            .JobId;
    }

    private static bool IsParked(Domain.Entities.Integration.JobProgress? row, string covererJobId)
        => row is { State: "Queued", AvailableAt: { Year: 9999 } }
           && JobProgressMessageCodec.TryDecode(row.Message, out var envelope)
           && envelope.Key == IJobQueue.ParkedMessageKey
           && envelope.Params?["jobId"] == covererJobId;

    private static void AssertParked(Domain.Entities.Integration.JobProgress? row, string covererJobId)
        => Assert.True(IsParked(row, covererJobId), $"{row?.JobId}: {row?.State} {row?.AvailableAt:O} {row?.Message}");

    private static void AssertQueuedPlain(Domain.Entities.Integration.JobProgress? row)
    {
        Assert.Equal("Queued", row?.State);
        Assert.NotEqual(9999, row!.AvailableAt!.Value.Year);
        Assert.Null(row.Message);
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
