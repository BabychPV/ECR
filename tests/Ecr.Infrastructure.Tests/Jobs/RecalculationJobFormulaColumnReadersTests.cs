// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationJobFormulaColumnReadersTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
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
/// ✎ F7 (огляд PERF-06, D-566): скрипт місяця q, що читає <c>row.F[m]</c> (F — місячна колонка <c>Formula</c> річної розкладки,
/// власник — період m), отримує прохід читача (<c>ForeignOwnerPass</c>), коли прогін чи каскад переписав F[m]. Доти прохід
/// читача (<c>EnqueueOwnerPeriodReadersAsync</c>) знав лише колонки <c>Calculated</c>, і скрипт лишався зі старим числом.
/// </summary>
/// <remarks>
/// Справжні SQL, <see cref="MonthColumnRouter"/> і <see cref="RecalculationJob"/>; оркестратор — фейк. Читач — з метаданих
/// публікації (<c>calc.MethodologyReadTable</c>, псевдонім таблиці), текст скрипту не розбирається. ⚠ Читач ВЛАСНОЇ таблиці
/// (<c>rows from T; row.F[m]</c>) у метаданих не записаний — відкрите питання, тут не покрито.
/// <para>
/// Шляхи через змінену функцію (<c>ReaderPeriodsOfOwnersAsync</c>): прогін названого періоду (кнопка місяця, залежні,
/// «з затвердженими», нічні діти) — тести 1–2; каскад від правки джерела (PERF-06; без ланцюжка й крок ланцюжка — та сама
/// постановка) — тест 3; гілка 5 плану кнопки року (<c>ChangedMonthsAsync</c>) — тест 4.
/// </para>
/// <para>
/// МУТАЦІЯ (локально, не комітиться): <c>OwnerReadColumn</c> → лише <c>Calculated</c> — червоні тести 1, 3 і 4 (з читачем).
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "PERF-06")]
public sealed class RecalculationJobFormulaColumnReadersTests(SqlServerFixture sql)
{
    private const int Year = 2099;
    private static readonly DateTime Now = new(2026, 11, 22, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Прогін_власника_формульної_колонки_ставить_прохід_скрипту_що_читає_таблицю_псевдонімом()
    {
        var world = await ArrangeAsync(Reader.Alias);
        var scheduler = Substitute.For<IBackgroundJobScheduler>();

        await RunAsync(world, scheduler, new Runner(), Request(world, Period(2)));

        await AssertPassesAsync(world, scheduler, 1, 3);
    }

    [Fact]
    public async Task Прив_язана_формульна_методологія_без_псевдоніма_читачем_не_є()
    {
        // Формульні методології місячних колонок рахують лише свій місяць (D-545): прохід не ставиться.
        var world = await ArrangeAsync(Reader.BoundFormulas);
        var scheduler = Substitute.For<IBackgroundJobScheduler>();

        await RunAsync(world, scheduler, new Runner(), Request(world, Period(2)));

        await scheduler.DidNotReceiveWithAnyArgs().EnqueueCoalescedAsync<IRecalculationJob>(default!, default, default, default);
    }

    [Fact]
    public async Task Каскад_від_правки_джерела_за_місяць_2_ставить_прохід_скриптам_інших_місяців()
    {
        var world = await ArrangeAsync(Reader.Alias);
        var scheduler = Substitute.For<IBackgroundJobScheduler>();
        var runner = new Runner();

        // PERF-06: задача каскаду року з місяцем 2 (без ланцюжка — оренди черги немає).
        await RunAsync(
            world, scheduler, runner,
            new RecalculationRequest(world.Doc.ProjectId, world.Doc.DocumentId, PeriodKey: null, TriggeredByUserId: null)
            {
                Year = Year,
                RecalculateSubmitted = true,
                SourceHop = 1,
                CascadePeriods = [Period(2)],
            });

        Assert.Equal([Period(2)], runner.Periods);
        await AssertPassesAsync(world, scheduler, 1, 3);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Кнопка_року_рахує_місяць_скрипта_якщо_власник_формульної_колонки_має_новіший_прогін(bool withReader)
    {
        // Гілка 5 плану (ChangedMonthsAsync): прохід читача загубився (місяць 2 переписано пізніше за прогони 1 і 3) —
        // місяці 1 і 3 у план. Годинник — у майбутньому, щоб гілки входів (правки після прогону) нічого не бачили; контроль без
        // читача (формульна методологія) — план порожній, тобто місяці додала саме гілка читачів.
        var at = new DateTime(2099, 12, 1, 8, 0, 0, DateTimeKind.Utc);
        var world = await ArrangeAsync(withReader ? Reader.Alias : Reader.BoundFormulas);
        foreach (var month in new[] { 1, 3, 2 })
        {
            await RunAsync(world, Substitute.For<IBackgroundJobScheduler>(), new Runner(), Request(world, Period(month)), at);
        }

        await using var db = world.Builder.CreateContext();
        var press = new RecalculationRequest(world.Doc.ProjectId, world.Doc.DocumentId, PeriodKey: null, TriggeredByUserId: 7)
        {
            Year = Year,
            SkipUnchanged = true,
        };

        var flagged = await Job(db, Substitute.For<IBackgroundJobScheduler>(), new Runner(), at).ChangedMonthsAsync(
            press, [Period(1), Period(3)], [Period(1), Period(2), Period(3)], [], new Dictionary<int, IReadOnlyList<int>>(),
            CancellationToken.None);

        Assert.Equal(withReader ? [Period(1), Period(3)] : [], flagged.Order());
    }

    // ───── стенд ─────

    private enum Reader
    {
        Alias,
        BoundFormulas,
    }

    private static async Task AssertPassesAsync(World world, IBackgroundJobScheduler scheduler, params int[] months)
    {
        foreach (var month in months)
        {
            await scheduler.Received(1).EnqueueCoalescedAsync<IRecalculationJob>(
                RecalculateDocumentHandler.TargetOf(world.Doc.DocumentId, new PeriodKey(Period(month))),
                Arg.Is<object?>(p => p is RecalculationRequest
                                     && ((RecalculationRequest)p).ForeignOwnerPass
                                     && ((RecalculationRequest)p).PeriodKey == Period(month)
                                     && ((RecalculationRequest)p).CascadePeriods == null),
                Arg.Any<CancellationToken>(),
                Arg.Any<int?>());
        }

        await scheduler.ReceivedWithAnyArgs(months.Length).EnqueueCoalescedAsync<IRecalculationJob>(default!, default, default, default);
    }

    private static RecalculationRequest Request(World world, int periodKey)
        => new(ProjectId: world.Doc.ProjectId, DocumentId: world.Doc.DocumentId, PeriodKey: periodKey, TriggeredByUserId: 7);

    private static async Task RunAsync(
        World world, IBackgroundJobScheduler scheduler, Runner runner, RecalculationRequest request, DateTime? at = null)
    {
        await using var db = world.Builder.CreateContext();
        await Job(db, scheduler, runner, at ?? Now).ExecuteAsync(request, new NoOpProgress(), CancellationToken.None);
    }

    private static RecalculationJob Job(EcrDbContext db, IBackgroundJobScheduler scheduler, Runner runner, DateTime at)
    {
        var clock = new TestClock(at);
        var registry = Substitute.For<ITemplateLayoutRegistry>();
        registry.HasAny.Returns(true);
        registry.IsYearVersion(Arg.Any<int>()).Returns(true);
        registry.IsYearSheet(Arg.Any<int>()).Returns(true);

        return new RecalculationJob(
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
    }

    /// <summary>
    /// Документ річної розкладки, періоди 1–3: місячні колонки M1..M12 — <c>Formula</c>, звичайна колонка — <c>Calculated</c>
    /// (вихід методології-читача).
    /// </summary>
    private async Task<World> ArrangeAsync(Reader reader)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            periodKey: Period(1), columnCount: 1, rowCount: 1, layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);
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

        var monthColumns = doc.MonthColumnDefIds.ToList();
        await db.ColumnDefs.Where(c => monthColumns.Contains(c.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.DataType, CellDataType.Formula));
        var output = doc.ColumnDefIds.First(id => !monthColumns.Contains(id));
        await db.ColumnDefs.Where(c => c.Id == output)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.DataType, CellDataType.Calculated));
        await db.SaveChangesAsync();

        var table = await db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == doc.TableDefId);
        var methodology = new Methodology(EcrCode.Create("F7R_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()), Name("reader"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(
            methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now,
            reader == Reader.BoundFormulas ? MethodologyEngine.Formulas : MethodologyEngine.Script);
        version.Publish(2, "test", new DateOnly(Year, 1, 1), testsPassed: true, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        if (reader == Reader.Alias)
        {
            db.MethodologyReadTables.Add(new MethodologyReadTable(version.Id, table.Code));
        }

        db.CalculationBindings.Add(new CalculationBinding(doc.TableDefId, output, methodology.Id, "Z", "{}"));
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

    private sealed record World(TestDocumentBuilder Builder, TestDocument Doc);

    /// <summary>Фейк оркестратора: нічого не рахує, лише записує періоди.</summary>
    private sealed class Runner : ICalculationRunner
    {
        public List<int> Periods { get; } = [];

        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
        {
            Periods.Add(periodKey.Value);
            return Task.FromResult(new ModuleProfile());
        }
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
