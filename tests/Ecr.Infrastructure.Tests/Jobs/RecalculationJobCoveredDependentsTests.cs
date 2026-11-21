// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationJobCoveredDependentsTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
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
/// Перф-аудит 20.11 (B80 N5): задача «залежні з затвердженими» після взяття лока документа і ДО читання входів скасовує
/// звичайну задачу залежних того самого документа й періоду, що встигла стати в чергу, поки затверджена чекала лок
/// (постановкою така не покрита) — з конвертом «покрито задачею {jobId}».
/// </summary>
/// <remarks>
/// ⛔ ДОКАЗ (локально): прибрати виклик <c>CancelCoveredDependentsAsync</c> з <c>RecalculationJob.ExecuteAsync</c> —
/// червоний <c>Затверджена_на_старті_скасовує_звичайну_що_чекає_до_читання_входів</c>.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "PERF-DEPENDENTS-DUP")]
public sealed class RecalculationJobCoveredDependentsTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private const string ApprovedJobId = "IRecalculationJob-approved-under-test";

    [Fact]
    public async Task Затверджена_на_старті_скасовує_звичайну_що_чекає_до_читання_входів()
    {
        var builder = new TestDocumentBuilder(Sql.ConnectionString);
        var document = await builder.BuildAsync();
        var period = new PeriodKey(document.PeriodKey!.Value);

        await using var host = NewHost();
        var scheduler = Scheduler(host);
        var regular = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            CalculationTrigger.DependentsTargetOf(document.DocumentId, period),
            new { document.DocumentId, PeriodKey = period.Value, RecalculateSubmitted = true },
            CancellationToken.None);

        string? stateAtFirstInputRead = null;
        await RunAsync(builder, scheduler, document, period, includeApproved: true, onFirstInputRead: () =>
            stateAtFirstInputRead ??= RowAsync(regular).GetAwaiter().GetResult()?.State);

        var cancelled = await RowAsync(regular);
        Assert.Equal("Cancelled", cancelled?.State);
        Assert.True(JobProgressMessageCodec.TryDecode(cancelled?.Message, out var envelope), cancelled?.Message);
        Assert.Equal(IJobQueue.CoveredByMessageKey, envelope.Key);
        Assert.Equal(ApprovedJobId, envelope.Params?["jobId"]);

        // Скасовано ДО першого читання входів прогону: інакше «покриття» не гарантувало б, що зміна, яка поставила
        // звичайну, прочитана.
        Assert.Equal("Cancelled", stateAtFirstInputRead);
    }

    /// <summary>Звичайна задача (без перекриття) чужих задач не скасовує.</summary>
    [Fact]
    public async Task Звичайна_на_старті_нічого_не_скасовує()
    {
        var builder = new TestDocumentBuilder(Sql.ConnectionString);
        var document = await builder.BuildAsync();
        var period = new PeriodKey(document.PeriodKey!.Value);

        await using var host = NewHost();
        var scheduler = Scheduler(host);
        var regular = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            CalculationTrigger.DependentsTargetOf(document.DocumentId, period),
            new { document.DocumentId, PeriodKey = period.Value, RecalculateSubmitted = true },
            CancellationToken.None);

        await RunAsync(builder, scheduler, document, period, includeApproved: false, onFirstInputRead: () => { });

        Assert.Equal("Queued", (await RowAsync(regular))?.State);
    }

    private static async Task RunAsync(
        TestDocumentBuilder builder, IBackgroundJobScheduler scheduler, TestDocument document, PeriodKey period,
        bool includeApproved, Action onFirstInputRead)
    {
        var clock = new TestClock(new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc));
        await using var db = builder.CreateContext();
        var job = new RecalculationJob(
            db, Substitute.For<ICalculationRunner>(), RunHandler(db, clock), FormulaService(onFirstInputRead), clock,
            jobs: scheduler, lease: new FixedLease(new JobClaimToken(ApprovedJobId, Guid.NewGuid())));

        var request = new RecalculationRequest(
            document.ProjectId, document.DocumentId, period.Value, TriggeredByUserId: null, RecalculateSubmitted: true,
            IncludeApproved: includeApproved, OverrideUserId: includeApproved ? 9 : null,
            OverrideReason: includeApproved ? "Акт 12" : null);

        // Тест про постановки, а не про прогін: збій пізніших фаз на заглушках тут не важить.
        try
        {
            await job.ExecuteAsync(request, new NoOpProgress(), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
        }
    }

    private static DbBackgroundJobScheduler Scheduler(Host host)
        => new(host.Queue, new QuartzJobScheduler(null, new JobProgressStore(host.Db), new SystemClock()), new JobQueueSignal());

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

    private sealed class FixedLease(JobClaimToken claim) : IJobLeaseContext
    {
        public JobClaimToken? Current { get; } = claim;
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
