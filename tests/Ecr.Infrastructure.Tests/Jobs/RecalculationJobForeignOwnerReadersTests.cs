// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationJobForeignOwnerReadersTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// MS (рецензія 2, п.1): читач результатів ІНШОГО періоду (скрипт, колонка <c>Calculated</c> з чужим власником) бачить
/// числа власника, перераховані тим самим прогоном, лише після перемикання актуальності — тож прогін ставить другий
/// прохід для періоду-читача (<c>ForeignOwnerPass</c>), а сам другий прохід нових не ставить.
/// </summary>
/// <remarks>
/// Оркестратор підмінено фейком, що повертає профіль із <see cref="ModuleProfile.ForeignOwnerReads"/>; перевіряється саме
/// рішення задачі. Мутаційні докази: не викликати <c>EnqueueForeignOwnerReadersAsync</c> — червоніє перший тест; прибрати
/// <c>request.ForeignOwnerPass</c> з умови — червоніє другий; не фільтрувати за <c>scope.Contains(owner)</c> — третій.
/// </remarks>
[Collection("SqlServer")]
public sealed class RecalculationJobForeignOwnerReadersTests(SqlServerFixture sql)
{
    private const int Year = 2099;
    private static readonly DateTime Now = new(2026, 11, 17, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читач_що_читав_власника_перерахованого_цим_прогоном_отримує_другий_прохід()
    {
        var world = await ArrangeAsync();
        var scheduler = Substitute.For<IBackgroundJobScheduler>();

        // Річний прогін: період 1 читав результати періодів 2 і 3 (їх цей же прогін перераховує).
        await RunAsync(world, scheduler, Request(world, periodKey: null, year: Year), reads: new() { [Period(1)] = [Period(2), Period(3)] });

        await scheduler.Received(1).EnqueueCoalescedAsync<IRecalculationJob>(
            RecalculateDocumentHandler.TargetOf(world.Doc.DocumentId, new PeriodKey(Period(1))),
            Arg.Is<object?>(p => p is RecalculationRequest
                                 && ((RecalculationRequest)p).ForeignOwnerPass
                                 && ((RecalculationRequest)p).PeriodKey == Period(1)
                                 && ((RecalculationRequest)p).DocumentId == world.Doc.DocumentId
                                 && ((RecalculationRequest)p).Year == null),
            Arg.Any<CancellationToken>(),
            Arg.Any<int?>());
        await scheduler.ReceivedWithAnyArgs(1).EnqueueCoalescedAsync<IRecalculationJob>(default!, default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Другий_прохід_нових_проходів_не_ставить()
    {
        var world = await ArrangeAsync();
        var scheduler = Substitute.For<IBackgroundJobScheduler>();

        await RunAsync(
            world, scheduler, Request(world, periodKey: null, year: Year) with { ForeignOwnerPass = true },
            reads: new() { [Period(1)] = [Period(2), Period(3)] });

        await scheduler.DidNotReceiveWithAnyArgs().EnqueueCoalescedAsync<IRecalculationJob>(default!, default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Власник_поза_прогоном_другого_проходу_не_потребує()
    {
        var world = await ArrangeAsync();
        var scheduler = Substitute.For<IBackgroundJobScheduler>();

        // Названий період 1: власники 2 і 3 цим прогоном не перераховуються — читач бачить їхні актуальні числа.
        await RunAsync(world, scheduler, Request(world, periodKey: Period(1), year: null), reads: new() { [Period(1)] = [Period(2), Period(3)] });

        await scheduler.DidNotReceiveWithAnyArgs().EnqueueCoalescedAsync<IRecalculationJob>(default!, default, default, default);
    }

    // ───── стенд ─────

    private static RecalculationRequest Request(World world, int? periodKey, int? year)
        => new(
            ProjectId: world.Doc.ProjectId,
            DocumentId: world.Doc.DocumentId,
            PeriodKey: periodKey,
            TriggeredByUserId: 7,
            Year: year);

    private async Task RunAsync(
        World world, IBackgroundJobScheduler scheduler, RecalculationRequest request, Dictionary<int, int[]> reads)
    {
        await using var db = world.Builder.CreateContext();
        var clock = new TestClock(Now);
        var job = new RecalculationJob(
            db,
            new Runner(reads),
            RunHandler(db, clock),
            FormulaService(),
            clock,
            jobs: scheduler)
        {
            CompletionBusyDelay = TimeSpan.Zero,
        };

        await job.ExecuteAsync(request, new NoOpProgress(), CancellationToken.None);
    }

    private async Task<World> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: Period(1), columnCount: 1, rowCount: 1);
        for (var month = 2; month <= 3; month++)
        {
            await builder.AddPeriodInstanceAsync(doc, new PeriodKey(Period(month)));
        }

        await using var db = builder.CreateContext();
        foreach (var month in new[] { 1, 2, 3 })
        {
            var period = await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == Period(month));
            period.AdvanceTo(PeriodState.Open, Now);
        }

        await db.SaveChangesAsync();
        return new World(builder, doc);
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

    private static Ecr.Application.Recalculation.RecalculationService FormulaService()
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
            new TestClock(Now),
            Substitute.For<IUnitOfWork>(), Substitute.For<ISheetEditGate>());
    }

    private static int Period(int month) => (Year * 100) + month;

    private sealed record World(TestDocumentBuilder Builder, TestDocument Doc);

    /// <summary>Фейк оркестратора: нічого не рахує, лише повідомляє, чиї результати читав період.</summary>
    private sealed class Runner(Dictionary<int, int[]> reads) : ICalculationRunner
    {
        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
        {
            var profile = new ModuleProfile();
            if (reads.TryGetValue(periodKey.Value, out var owners))
            {
                profile.RecordForeignOwnerReads(owners);
            }

            return Task.FromResult(profile);
        }
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
