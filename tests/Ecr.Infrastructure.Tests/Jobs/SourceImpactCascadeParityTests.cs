// tests/Ecr.Infrastructure.Tests/Jobs/SourceImpactCascadeParityTests.cs
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
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
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ Діалог «що буде перераховано» після правки HSE400 (людина: «змінила січень, а вікно показало ще 2 місяці»): оцінка впливу
/// (<see cref="SourceImpactAssessor"/>) мусить показувати РІВНО ті періоди споживачів, які порахує каскад, — і кожен з причиною.
/// </summary>
/// <remarks>
/// <para>
/// Корінь: оцінка брала ВСІ місяці річного (квартального) періоду джерела (<c>DocumentSourceGate.OwnedMonths(Yearly, 1)</c> —
/// 1..12) і лише періоди споживачів із рядком стану, а каскад PERF-06b (<c>D-572</c>) донесе лише місяці правки (якщо доказ
/// пройде) — і до всіх відкритих періодів (без рядка стану теж); пізніших читачів (<c>[Period:-1]</c>) оцінка не бачила взагалі.
/// </para>
/// <para>
/// Світ: річне джерело 2099 (колонки M1..M12, таблиця складу <c>FG_Internal</c>), зв'язка <c>Any</c>, місячний споживач за
/// січень–березень (ребро «місяць»). «Виконання» — справжній ланцюг: хук правки → <c>-auto</c> з місяцями →
/// <see cref="RecalculationJob"/> (доказ) → хук успіху → задачі зв'язки → <see cref="RegistryImpactRecalculationJob"/> → задача
/// каскаду споживача (<c>cascadePeriods</c>) → план першого кроку (<c>CascadeMonthsAsync</c>).
/// </para>
/// <para>
/// МУТАЦІЇ (локально, не комітяться): (1) <c>SourceImpactAssessor.CascadeMonthsAsync</c> завжди «увесь рік» (стара поведінка) —
/// червоний <c>Доведено…</c>; (2) доказ не питається (пропустити <c>SourceKeepsMonthsAsync</c>) — червоний <c>Не_доведено…</c>;
/// (3) план каскаду не питається (лише цілі) — червоний <c>Пізніші_читачі…</c>.
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "PERF-06")]
public sealed partial class SourceImpactCascadeParityTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private const int YearKey = (SourceDependencyWorld.Year * 100) + 1;

    private static readonly DateTime Now = new(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc);

    private static readonly int[] FirstQuarter = [1, 2, 3];

    /// <summary>
    /// Правка січня річного джерела, доказ проходить: вікно — лише січень споживача («володіє зміненим місяцем»), і каскад рахує
    /// лише січень. Доти вікно показувало січень, лютий і березень (усі періоди з рядком стану).
    /// </summary>
    [Fact]
    public async Task Доведено_правка_січня_вікно_і_каскад_лише_січень()
    {
        var (source, consumer, sheetDefId, bindingId) = await ArrangeAsync(spreadSource: false, crossMonth: false, approveFebruary: false);

        var impact = await AssessAsync(source, sheetDefId, [1]);
        var executed = await ExecuteAsync(source, consumer, bindingId);

        Assert.Equal([1], impact.CascadeMonths);
        Assert.Null(impact.WholeYearReason);
        Assert.Equal([SourceDependencyWorld.Period(1)], executed);
        Assert.Equal(executed, Recalculated(impact, consumer));
        Assert.Equal(SourceImpactReasons.SourceMonth, Assert.Single(impact.Consumers).Reason);
    }

    /// <summary>
    /// Формула джерела переносить місяць (M5 читає M4) — доказ не проходить: і вікно, і каскад — увесь рік споживача (січень,
    /// березень; затверджений лютий у вікні, але «не буде перераховано»), причина вікна — «джерело переносить зміну місяця».
    /// </summary>
    [Fact]
    public async Task Не_доведено_вікно_і_каскад_увесь_рік_з_причиною()
    {
        var (source, consumer, sheetDefId, bindingId) = await ArrangeAsync(spreadSource: true, crossMonth: false, approveFebruary: true);

        var impact = await AssessAsync(source, sheetDefId, [1]);
        var executed = await ExecuteAsync(source, consumer, bindingId);

        Assert.Null(impact.CascadeMonths);
        Assert.Equal(SourceImpactReasons.WholeYearSourceSpreadsMonths, impact.WholeYearReason);
        Assert.Equal([SourceDependencyWorld.Period(1), SourceDependencyWorld.Period(3)], executed);
        Assert.Equal(executed, Recalculated(impact, consumer));
        var february = Assert.Single(impact.Consumers, c => c.PeriodKey == SourceDependencyWorld.Period(2));
        Assert.False(february.WillRecalculate);
        Assert.Equal(SourceImpactReasons.SkipApproved, february.SkipReason);
        Assert.All(impact.Consumers, c => Assert.Equal(SourceImpactReasons.SourceYear, c.Reason));
    }

    /// <summary>
    /// Доказ проходить, але формули споживача читають обчислене число попереднього місяця (<c>[Period:-1]</c>): каскад рахує
    /// січень і транзитивно лютий і березень — вікно показує їх з причиною «читає січень / лютий».
    /// </summary>
    [Fact]
    public async Task Пізніші_читачі_у_вікні_з_причиною_як_у_плані_каскаду()
    {
        var (source, consumer, sheetDefId, bindingId) = await ArrangeAsync(spreadSource: false, crossMonth: true, approveFebruary: false);

        var impact = await AssessAsync(source, sheetDefId, [1]);
        var targets = await ExecuteAsync(source, consumer, bindingId);
        Assert.Equal([SourceDependencyWorld.Period(1)], targets);

        // План першого кроку задачі каскаду — тим самим кодом задачі.
        await using var db = Sql.CreateContext();
        var job = NewJob(db);
        var open = FirstQuarter.Select(SourceDependencyWorld.Period).ToList();
        var plan = await job.CascadeMonthsAsync(
            new RecalculationRequest(consumer.ProjectId, consumer.DocumentId, null, null)
            {
                Year = SourceDependencyWorld.Year, RecalculateSubmitted = true, CascadePeriods = [.. targets],
            },
            open, targets, new Dictionary<int, IReadOnlyList<int>>(), [], [], CancellationToken.None);

        Assert.Equal(plan.Order(), Recalculated(impact, consumer));
        Assert.Equal(open, plan.Order());
        Assert.Equal(
            [(SourceImpactReasons.SourceMonth, (int?)null),
             (SourceImpactReasons.CrossMonth, SourceDependencyWorld.Period(1)),
             (SourceImpactReasons.CrossMonth, SourceDependencyWorld.Period(2))],
            impact.Consumers.OrderBy(c => c.PeriodKey).Select(c => (c.Reason, c.ReadsPeriodKey)));
    }

    /// <summary>Правка без відомих місяців (не-місячна колонка, місяці не передано) — увесь рік, причина «місяці правки невідомі».</summary>
    [Fact]
    public async Task Без_місяців_правки_увесь_рік_з_причиною()
    {
        var (source, consumer, sheetDefId, _) = await ArrangeAsync(spreadSource: false, crossMonth: false, approveFebruary: false);

        var impact = await AssessAsync(source, sheetDefId, null);

        Assert.Equal(SourceImpactReasons.WholeYearEditedMonthsUnknown, impact.WholeYearReason);
        Assert.Equal(FirstQuarter.Select(SourceDependencyWorld.Period), Recalculated(impact, consumer));
    }

    private static int[] Recalculated(SourceImpact impact, TestDocument consumer)
        => [.. impact.Consumers.Where(c => c.DocumentId == consumer.DocumentId && c.WillRecalculate).Select(c => c.PeriodKey).Order()];

    private async Task<(TestDocument Source, TestDocument Consumer, int SheetDefId, int BindingId)> ArrangeAsync(
        bool spreadSource, bool crossMonth, bool approveFebruary,
        DocumentSourceState state = DocumentSourceState.Any, bool closeApprovedJanuary = false)
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, code) = await world.RegistryAsync();
        var source = await YearlySourceAsync();
        var consumer = await world.DocumentAsync([1, 2, 3], code);
        var bindingId = await world.BindingAsync(source.TemplateId, registryId, state);
        await using (var policyDb = Sql.CreateContext())
        {
            await policyDb.DocumentSourceBindings.Where(b => b.Id == bindingId)
                .ExecuteUpdateAsync(b => b.SetProperty(x => x.Policy, DocumentSourcePolicy.Warn));
        }

        await using var db = Sql.CreateContext();
        if (spreadSource)
        {
            // Формула джерела M5 = M4: зміна квітня переходить у травень — доказ PERF-06b не проходить.
            var formula = new FormulaDef(source.TableDefId, FormulaScope.Column, "1", ExpressionDialect.Template);
            formula.AssignColumn(source.MonthColumnDefIds[4]);
            db.FormulaDefs.Add(formula);
            await db.SaveChangesAsync();
            db.FormulaDependencies.Add(FormulaDependency.ForFormula(formula.Id, 0, source.TableDefId, "R1", source.MonthColumnDefIds[3], null, null, 0));
        }

        if (crossMonth)
        {
            await CrossMonthAsync(db, consumer);
        }

        await StreamAsync(db, source);
        if (approveFebruary)
        {
            await world.SetSheetAsync(consumer, 2, DocumentStatus.Approved);
        }

        if (closeApprovedJanuary)
        {
            await world.SetSheetAsync(consumer, 1, DocumentStatus.Approved);
            await world.CloseAsync(consumer, 1);
        }

        return (source, consumer, source.SheetDefId, bindingId);
    }

    /// <summary>Споживач: колонка 3 = колонка 2 попереднього місяця (обчислена — читає довідник), <c>[Period:-1]</c>.</summary>
    private static async Task CrossMonthAsync(EcrDbContext db, TestDocument consumer)
    {
        await db.ColumnDefs.Where(c => c.Id == consumer.ColumnDefIds[1])
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.DataType, CellDataType.Formula));
        var formula = new FormulaDef(consumer.TableDefId, FormulaScope.Column, "1", ExpressionDialect.Template);
        formula.AssignColumn(consumer.ColumnDefIds[2]);
        db.FormulaDefs.Add(formula);
        await db.SaveChangesAsync();
        db.FormulaDependencies.Add(FormulaDependency.ForFormula(formula.Id, 0, consumer.TableDefId, "R1", consumer.ColumnDefIds[1], null, -1, 0));
        await db.SaveChangesAsync();
    }

    /// <summary>Таблиця складу HSE400 на аркуші джерела (зв'язка читає її) з екземпляром року.</summary>
    private async Task StreamAsync(EcrDbContext db, TestDocument source)
    {
        var stream = new TableDef(
            source.SheetDefId, EcrCode.Create("FG_Internal"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "FG_Internal" }), 50,
            TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.Add(stream);
        await db.SaveChangesAsync();
        var instanceId = await new BulkCellLoader(Sql.ConnectionString, 1000)
            .ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        db.TableInstances.Add(new TableInstance(new PeriodKey(YearKey), instanceId, source.DocumentId, stream.Id, Now));
        await db.SaveChangesAsync();
    }

    private Task<SourceImpact> AssessAsync(TestDocument source, int sheetDefId, int[]? months)
        => AssessAsync(source, sheetDefId, SourceApprovalChange.Edited, months, withPlan: true);

    /// <summary>Оцінка: з планом (GET, 6 аргументів) чи лише факти політики (транзакція Approve/Reopen, 5 аргументів).</summary>
    private async Task<SourceImpact> AssessAsync(
        TestDocument source, int sheetDefId, SourceApprovalChange change, int[]? months, bool withPlan,
        ISourceCascadePlanner? planner = null)
    {
        await using var host = NewHost();
        var assessor = new SourceImpactAssessor(
            host.Db, SourceDependencyWorld.Graph(host.Db), planner ?? NewJob(host.Db), Trigger(host.Db, Scheduler(host)));
        return withPlan
            ? await assessor.AssessAsync(source.DocumentId, sheetDefId, YearKey, change, months, CancellationToken.None)
            : await assessor.AssessAsync(source.DocumentId, sheetDefId, YearKey, change, CancellationToken.None);
    }

    /// <summary>Справжній ланцюг правки джерела до задачі каскаду споживача; повертає її <c>cascadePeriods</c>.</summary>
    private async Task<int[]> ExecuteAsync(TestDocument source, TestDocument consumer, int bindingId)
    {
        // (1) Правка комірки січня → власний перерахунок джерела з місяцями (D-572).
        await using (var host = NewHost())
        {
            var jobs = Scheduler(host);
            await new SourceDocumentChangeHook(host.Db, jobs, Trigger(host.Db, jobs))
                .OnCellsEditedAsync(
                    new ChangedPeriodsDictionary
                    {
                        [source.DocumentId] = [YearKey],
                        ChangedColumns = new Dictionary<int, IReadOnlySet<int>> { [YearKey] = new HashSet<int> { source.MonthColumnDefIds[0] } },
                    },
                    CancellationToken.None);
        }

        var auto = Assert.Single(await RowsAsync($"{nameof(IRecalculationJob)}~doc{source.DocumentId}-"));
        var root = JsonDocument.Parse(auto.Payload!).RootElement;
        int[]? sourceMonths = root.TryGetProperty("sourceMonths", out var m) && m.ValueKind == JsonValueKind.Array
            ? [.. m.EnumerateArray().Select(e => e.GetInt32())]
            : null;

        // (2) Власний перерахунок джерела (доказ місяців) → хук успіху → задачі зв'язки.
        await using (var host = NewHost())
        {
            await NewJob(host.Db, new SourceDocumentChangeHook(host.Db, Scheduler(host)))
                .ExecuteAsync(
                    new RecalculationRequest(source.ProjectId, source.DocumentId, YearKey, TriggeredByUserId: null)
                    {
                        RecalculateSubmitted = true,
                        SourceMonths = sourceMonths,
                    },
                    Substitute.For<IJobProgress>(),
                    CancellationToken.None);
        }

        // (3) Задачі зв'язки → задача каскаду споживача.
        foreach (var parent in await RowsAsync($"{nameof(IRegistryImpactRecalculationJob)}~source-change-b{bindingId}-"))
        {
            await using var host = NewHost();
            await new RegistryImpactRecalculationJob(
                    new RegistryImpactStore(host.Db), Trigger(host.Db, Scheduler(host)), graph: SourceDependencyWorld.Graph(host.Db))
                .ExecuteAsync(parent.Payload!, Substitute.For<IJobProgress>(), CancellationToken.None);
        }

        var cascade = Assert.Single(await RowsAsync($"{nameof(IRecalculationJob)}~doc{consumer.DocumentId}-"));
        return [.. JsonDocument.Parse(cascade.Payload!).RootElement.GetProperty("cascadePeriods").EnumerateArray()
            .Select(e => e.GetInt32()).Order()];
    }

    private async Task<TestDocument> YearlySourceAsync()
    {
        var document = await new TestDocumentBuilder(Sql.ConnectionString).BuildAsync(periodKey: YearKey, monthColumns: 12);
        await using var db = Sql.CreateContext();
        await db.Projects.Where(p => p.Id == document.ProjectId).ExecuteUpdateAsync(s => s.SetProperty(p => p.PeriodKind, PeriodKind.Yearly));
        foreach (var period in await db.Periods.Where(p => p.ProjectId == document.ProjectId).ToListAsync())
        {
            period.AdvanceTo(PeriodState.Open, Now);
        }

        await db.SaveChangesAsync();
        return document;
    }

    private async Task<List<JobProgress>> RowsAsync(string prefix)
    {
        await using var db = Sql.CreateContext();
        return await db.JobProgresses.AsNoTracking().Where(p => p.TargetKey!.StartsWith(prefix)).ToListAsync();
    }

    private static RecalculationJob NewJob(EcrDbContext db, IRecalculationCompletionHook? hook = null)
        => new(db, new NoopRunner(), RunHandler(), FormulaService(), new TestClock(Now), completionHook: hook);

    private static CalculationTrigger Trigger(EcrDbContext db, IBackgroundJobScheduler jobs)
        => new(jobs, new PeriodStore(db), new WorkflowStore(db), new DocumentStore(db), new DocumentSheetCopyStore(db));

    private static DbBackgroundJobScheduler Scheduler(Host host)
        => new(host.Queue, new QuartzJobScheduler(null, new JobProgressStore(host.Db), new SystemClock()), new JobQueueSignal());

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

    private static RunCalculationHandler RunHandler()
        => new(
            Substitute.For<IPeriodStore>(), Substitute.For<IWorkflowStore>(), Substitute.For<ICalculationResultStore>(),
            Substitute.For<IBackgroundJobScheduler>(), Substitute.For<IUnitOfWork>(),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(), Substitute.For<ICurrentUser>(), new TestClock(Now));

    private sealed class NoopRunner : ICalculationRunner
    {
        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
            => Task.FromResult(new ModuleProfile());
    }
}
