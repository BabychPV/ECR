// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationJobOwnerPeriodReadersTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// MS (рецензія 3, п.2): прогін ЛИШЕ періоду-власника місячних колонок <c>Calculated</c> ставить coalesced-прохід
/// (<c>ForeignOwnerPass</c>) читачам інших періодів документа — їх визначають матеріалізовані дані
/// (<c>calc.MethodologyReadTable</c>, прив'язки, роутер місяців), нової таблиці-індексу немає. Затверджені аркуші читача не
/// перераховуються (D-328, D-493): період, де затверджено ВСЕ, проходу не отримує, а лишає позначку «пропущено».
/// </summary>
/// <remarks>
/// Справжні БД, <see cref="MonthColumnRouter"/> і <see cref="RecalculationJob"/>; оркестратор — фейк, що записує, які прив'язки
/// йому дали (що саме перерахується). Мутаційні докази: не викликати <c>EnqueueOwnerPeriodReadersAsync</c> — червоніють
/// перші два тести; не перевіряти <c>ReaderPassSkipAsync</c> — червоніє тест про затверджений період.
/// </remarks>
[Collection("SqlServer")]
public sealed class RecalculationJobOwnerPeriodReadersTests(SqlServerFixture sql)
{
    private const int Year = 2099;
    private const byte CalculatedType = 8;
    private static readonly DateTime Now = new(2026, 11, 17, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прогін_лише_власника_ставить_прохід_читачам_інших_періодів_і_цей_прохід_рахує_читача()
    {
        var world = await ArrangeAsync(withReader: true);
        var scheduler = Substitute.For<IBackgroundJobScheduler>();
        var runner = new Runner();

        // Названий період 2 — власник місячної колонки M2; читачі у періодах 1 і 3 (їх цей прогін не рахував).
        await RunAsync(world, scheduler, runner, Request(world, Period(2)));

        Assert.Equal([Period(2)], runner.Periods);
        foreach (var reader in new[] { 1, 3 })
        {
            await scheduler.Received(1).EnqueueCoalescedAsync<IRecalculationJob>(
                RecalculateDocumentHandler.TargetOf(world.Doc.DocumentId, new PeriodKey(Period(reader))),
                Arg.Is<object?>(p => p is RecalculationRequest
                                     && ((RecalculationRequest)p).ForeignOwnerPass
                                     && ((RecalculationRequest)p).PeriodKey == Period(reader)
                                     && ((RecalculationRequest)p).DocumentId == world.Doc.DocumentId
                                     && ((RecalculationRequest)p).SheetDefId == null),
                Arg.Any<CancellationToken>(),
                Arg.Any<int?>());
        }

        await scheduler.ReceivedWithAnyArgs(2).EnqueueCoalescedAsync<IRecalculationJob>(default!, default, default, default);

        // Виконання проходу періоду-читача: читача (його прив'язку) перераховано від числа власника, нових проходів немає.
        var passes = scheduler.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IBackgroundJobScheduler.EnqueueCoalescedAsync))
            .Select(c => (RecalculationRequest)c.GetArguments()[1]!)
            .ToList();
        var next = Substitute.For<IBackgroundJobScheduler>();
        var second = new Runner();
        foreach (var pass in passes)
        {
            await RunAsync(world, next, second, pass);
        }

        Assert.Equal([Period(1), Period(3)], second.Periods.Order());
        Assert.All(second.BindingsByPeriod.Values, b => Assert.Contains(world.ReaderMethodologyId, b));
        await next.DidNotReceiveWithAnyArgs().EnqueueCoalescedAsync<IRecalculationJob>(default!, default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Другий_прохід_нових_проходів_не_ставить()
    {
        var world = await ArrangeAsync(withReader: true);
        var scheduler = Substitute.For<IBackgroundJobScheduler>();

        await RunAsync(world, scheduler, new Runner(), Request(world, Period(2)) with { ForeignOwnerPass = true });

        await scheduler.DidNotReceiveWithAnyArgs().EnqueueCoalescedAsync<IRecalculationJob>(default!, default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_читача_таблиці_з_колонками_власника_прохід_не_ставиться()
    {
        var world = await ArrangeAsync(withReader: false);
        var scheduler = Substitute.For<IBackgroundJobScheduler>();

        await RunAsync(world, scheduler, new Runner(), Request(world, Period(2)));

        await scheduler.DidNotReceiveWithAnyArgs().EnqueueCoalescedAsync<IRecalculationJob>(default!, default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Затверджений_період_читача_проходу_не_отримує_а_лишає_позначку_і_після_повернення_в_роботу_отримує()
    {
        var world = await ArrangeAsync(withReader: true);

        // Єдиний аркуш періоду 1 затверджено (аркуш читача = аркуш власника в цьому стенді).
        await using (var db = world.Builder.CreateContext())
        {
            var state = new ApprovalState(world.Doc.DocumentId, world.Doc.SheetDefId, Period(1));
            state.Submit(userId: 5, Now);
            state.Approve(userId: 6, Now);
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync();
        }

        var scheduler = Substitute.For<IBackgroundJobScheduler>();
        await RunAsync(world, scheduler, new Runner(), Request(world, Period(2)));

        await scheduler.DidNotReceive().EnqueueCoalescedAsync<IRecalculationJob>(
            RecalculateDocumentHandler.TargetOf(world.Doc.DocumentId, new PeriodKey(Period(1))),
            Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>());
        await scheduler.Received(1).EnqueueCoalescedAsync<IRecalculationJob>(
            RecalculateDocumentHandler.TargetOf(world.Doc.DocumentId, new PeriodKey(Period(3))),
            Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>());

        // Позначка «пропущено, затверджений» — та сама, що в усякого затвердженого залежного.
        Assert.Equal(1, await SkippedMarksAsync(world, Period(1)));
        Assert.Equal(0, await SkippedMarksAsync(world, Period(3)));

        // Повернення в роботу → наступний прогін власника ставить прохід і періоду 1.
        await using (var db = world.Builder.CreateContext())
        {
            var state = await db.ApprovalStates.SingleAsync(s => s.DocumentId == world.Doc.DocumentId && s.PeriodKey == Period(1));
            state.Reopen(userId: 6, "правка", Now);
            await db.SaveChangesAsync();
        }

        var again = Substitute.For<IBackgroundJobScheduler>();
        await RunAsync(world, again, new Runner(), Request(world, Period(2)));
        await again.Received(1).EnqueueCoalescedAsync<IRecalculationJob>(
            RecalculateDocumentHandler.TargetOf(world.Doc.DocumentId, new PeriodKey(Period(1))),
            Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>());
    }

    // ───── стенд ─────

    private static RecalculationRequest Request(World world, int periodKey)
        => new(
            ProjectId: world.Doc.ProjectId,
            DocumentId: world.Doc.DocumentId,
            PeriodKey: periodKey,
            TriggeredByUserId: 7);

    private async Task RunAsync(World world, IBackgroundJobScheduler scheduler, Runner runner, RecalculationRequest request)
    {
        await using var db = world.Builder.CreateContext();
        var clock = new TestClock(Now);
        var registry = Substitute.For<ITemplateLayoutRegistry>();
        registry.HasAny.Returns(true);
        registry.IsYearVersion(Arg.Any<int>()).Returns(true);
        registry.IsYearSheet(Arg.Any<int>()).Returns(true);

        var job = new RecalculationJob(
            db,
            runner,
            RunHandler(db, clock),
            FormulaService(),
            clock,
            jobs: scheduler,
            audit: new AuditWriter(db),
            monthRouter: new MonthColumnRouter(db, Substitute.For<IConfiguration>(), registry))
        {
            CompletionBusyDelay = TimeSpan.Zero,
        };

        await job.ExecuteAsync(request, new NoOpProgress(), CancellationToken.None);
    }

    private async Task<World> ArrangeAsync(bool withReader)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            periodKey: Period(1), columnCount: 0, rowCount: 1, layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);
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

        // Місячні колонки таблиці — Calculated (їх заповнюють результати методологій).
        var columnIds = doc.MonthColumnDefIds.ToList();
        await db.ColumnDefs.Where(c => columnIds.Contains(c.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.DataType, CellDataType.Calculated));
        await db.SaveChangesAsync();

        var readerId = 0;
        if (withReader)
        {
            var table = await db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == doc.TableDefId);
            var methodology = new Methodology(EcrCode.Create("RDR_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()), Name("reader"));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();

            var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
            version.Publish(2, "test", new DateOnly(2099, 1, 1), testsPassed: true, Now);
            db.MethodologyVersions.Add(version);
            await db.SaveChangesAsync();

            db.MethodologyReadTables.Add(new MethodologyReadTable(version.Id, table.Code));
            db.CalculationBindings.Add(new CalculationBinding(doc.TableDefId, columnIds[0], methodology.Id, "Z", "{}"));
            await db.SaveChangesAsync();
            readerId = methodology.Id;
        }

        return new World(builder, doc, readerId);
    }

    private async Task<int> SkippedMarksAsync(World world, int period)
    {
        await using var db = world.Builder.CreateContext();
        var correlation = RecalculationSkips.AuditCorrelation(world.Doc.DocumentId, world.Doc.SheetDefId, period);
        var entityType = RecalculationSkips.AuditEntityType;
        var operation = RecalculationSkips.AuditSkippedApproved;
        return await db.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS Value FROM aud.StructureChange
                 WHERE EntityType = {entityType} AND Operation = {operation} AND CorrelationId = {correlation}
                """)
            .SingleAsync();
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

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record World(TestDocumentBuilder Builder, TestDocument Doc, int ReaderMethodologyId);

    /// <summary>Фейк оркестратора: нічого не рахує, лише записує, які прив'язки (методології) отримав кожен період.</summary>
    private sealed class Runner : ICalculationRunner
    {
        public List<int> Periods { get; } = [];

        public Dictionary<int, List<int>> BindingsByPeriod { get; } = [];

        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
        {
            Periods.Add(periodKey.Value);
            BindingsByPeriod[periodKey.Value] = [.. bindings.Select(b => b.MethodologyId).Distinct()];
            return Task.FromResult(new ModuleProfile());
        }
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
