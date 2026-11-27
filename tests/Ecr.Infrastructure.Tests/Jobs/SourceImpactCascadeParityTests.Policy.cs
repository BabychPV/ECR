// tests/Ecr.Infrastructure.Tests/Jobs/SourceImpactCascadeParityTests.Policy.cs
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ D-581, огляд H1/H2/M2/M3/M4: політика D-554 рахується окремо від «буде перераховано» (затверджене, що застаріє, — і в
/// закритому місяці, і в пізнішому читачі), відбиток GET і транзакції той самий, збій прогнозу не валить дію, наскрізна
/// звірка з виконаною задачею каскаду споживача, кілька споживачів і зв'язок.
/// </summary>
/// <remarks>
/// МУТАЦІЇ (локально, не комітяться): H2 — відображення лише відкритих періодів (<c>anyPeriodState: false</c>) — червоний
/// <c>Закритий…</c>; H2 — без затверджених пізніших читачів (<c>StaleReadersAsync</c> → порожньо) — червоні
/// <c>Затверджений_пізніший…</c>, <c>Закритий…</c>; M2 — без <c>catch</c> навколо прогнозу — червоний <c>Збій…</c>; H1 — у
/// <c>RecalculateApprovedDependentsWholeYearTests</c>. M3 (без <c>tables.Add</c>) — ЕКВІВАЛЕНТНИЙ мутант, лишається зеленим: насіння —
/// усі місячні колонки місяця правки, тож похідна місячна колонка цього місяця і її таблиця вже в наборі, а похідна іншого
/// місяця чи не-місячна одразу дає «річну колонку». <c>Ребро_вся_таблиця…</c> стереже поведінку, не рядок.
/// </remarks>
public sealed partial class SourceImpactCascadeParityTests
{
    /// <summary>
    /// Затвердження річного джерела (увесь рік): затверджений ЗАКРИТИЙ січень споживача каскад не чіпає, але він застаріє —
    /// у переліку з <c>closed</c> і лічиться в політиці; лютий затверджений (<c>approved</c>), березень перерахується. Шлях
    /// транзакції (без плану) дає ТОЙ САМИЙ відбиток і ту саму політику, що й GET.
    /// </summary>
    [Fact]
    public async Task Закритий_затверджений_місяць_і_затверджений_читач_у_політиці_відбиток_GET_дорівнює_транзакції()
    {
        var (source, consumer, sheetDefId, _) = await ArrangeAsync(
            spreadSource: false, crossMonth: true, approveFebruary: true, DocumentSourceState.ApprovedOnly, closeApprovedJanuary: true);

        var get = await AssessAsync(source, sheetDefId, SourceApprovalChange.Approved, null, withPlan: true);
        var check = await AssessAsync(source, sheetDefId, SourceApprovalChange.Approved, null, withPlan: false);

        var january = Assert.Single(get.Consumers, c => c.PeriodKey == SourceDependencyWorld.Period(1));
        Assert.False(january.WillRecalculate);
        Assert.Equal(SourceImpactReasons.SkipClosed, january.SkipReason);
        Assert.True(january.PolicyRelevant);
        var february = Assert.Single(get.Consumers, c => c.PeriodKey == SourceDependencyWorld.Period(2));
        Assert.Equal(SourceImpactReasons.SkipApproved, february.SkipReason);
        Assert.Equal([SourceDependencyWorld.Period(3)], Recalculated(get, consumer));

        Assert.Equal(2, get.ApprovedCount);
        Assert.True(get.Required);
        Assert.Equal(get.Hash, check.Hash);
        Assert.Equal(get.ApprovedCount, check.ApprovedCount);
        Assert.Equal(get.Required, check.Required);
        Assert.Equal(get.Policy, check.Policy);
    }

    /// <summary>
    /// Правка січня (доведено): лютий затверджений і читає січень (<c>[Period:-1]</c>) — каскад його не перерахує, але він застаріє:
    /// у переліку «затверджено», причина «читає січень», лічиться в політиці. Березень читає лише лютий (не змінився) — його немає.
    /// </summary>
    [Fact]
    public async Task Затверджений_пізніший_читач_у_політиці_але_не_в_перерахунку()
    {
        var (source, consumer, sheetDefId, bindingId) = await ArrangeAsync(spreadSource: false, crossMonth: true, approveFebruary: true);

        var impact = await AssessAsync(source, sheetDefId, [1]);
        var executed = await ExecuteAsync(source, consumer, bindingId);

        Assert.Equal([SourceDependencyWorld.Period(1)], executed);
        Assert.Equal(executed, Recalculated(impact, consumer));
        var february = Assert.Single(impact.Consumers, c => c.PeriodKey == SourceDependencyWorld.Period(2));
        Assert.True(february.PolicyRelevant);
        Assert.Equal(SourceImpactReasons.SkipApproved, february.SkipReason);
        Assert.Equal((SourceImpactReasons.CrossMonth, (int?)SourceDependencyWorld.Period(1)), (february.Reason, february.ReadsPeriodKey));
        Assert.DoesNotContain(impact.Consumers, c => c.PeriodKey == SourceDependencyWorld.Period(3));
        Assert.Equal(1, impact.ApprovedCount);
    }

    /// <summary>
    /// Прогноз падає (план і пізніші читачі) — оцінка не падає: цілі відображення перераховуються, кожен пізніший період —
    /// читач (затверджений лютий у політиці), тобто «більше», а не 500 на Approve/Reopen.
    /// </summary>
    [Fact]
    public async Task Збій_прогнозу_не_валить_оцінку_а_дає_більше()
    {
        var (source, consumer, sheetDefId, _) = await ArrangeAsync(spreadSource: false, crossMonth: true, approveFebruary: true);

        var impact = await AssessAsync(source, sheetDefId, SourceApprovalChange.Edited, [1], withPlan: true, new FailingPlanner());

        Assert.Equal([1], impact.CascadeMonths);
        Assert.Equal([SourceDependencyWorld.Period(1)], Recalculated(impact, consumer));
        Assert.Equal(1, impact.ApprovedCount);
        Assert.True(Assert.Single(impact.Consumers, c => c.PeriodKey == SourceDependencyWorld.Period(2)).PolicyRelevant);
    }

    /// <summary>
    /// Огляд M3: правка січня змінює похідну M1 іншої таблиці, а формула річної колонки читає ту таблицю ЦІЛКОМ (ребро без
    /// колонки) — прохід формул змінить річну колонку, тож хук місяців не дасть: увесь рік, причина «річна колонка».
    /// </summary>
    [Fact]
    public async Task Ребро_вся_таблиця_через_похідну_колонку_дає_річну_колонку()
    {
        var (source, _, sheetDefId, _) = await ArrangeAsync(spreadSource: false, crossMonth: false, approveFebruary: false);
        await using (var db = Sql.CreateContext())
        {
            var table = new TableDef(
                source.SheetDefId, EcrCode.Create($"TB{Guid.NewGuid():N}"[..12]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Derived" }), 60,
                TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
            db.TableDefs.Add(table);
            await db.SaveChangesAsync();
            var january = new ColumnDef(table.Id, EcrCode.Create($"DM1{Guid.NewGuid():N}"[..12]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "M1" }), 1, CellDataType.Formula);
            january.SetMonth(true, 1);
            db.ColumnDefs.Add(january);
            await db.SaveChangesAsync();

            var derived = new FormulaDef(table.Id, FormulaScope.Column, "1", ExpressionDialect.Template);
            derived.AssignColumn(january.Id);
            var annual = new FormulaDef(source.TableDefId, FormulaScope.Column, "1", ExpressionDialect.Template);
            annual.AssignColumn(source.ColumnDefIds[1]);
            db.FormulaDefs.AddRange(derived, annual);
            await db.SaveChangesAsync();
            db.FormulaDependencies.Add(FormulaDependency.ForFormula(derived.Id, 0, source.TableDefId, "R1", source.MonthColumnDefIds[0], null, null, 0));
            db.FormulaDependencies.Add(FormulaDependency.ForFormula(annual.Id, 0, table.Id, null, null, null, null, 0));
            await db.SaveChangesAsync();
        }

        var impact = await AssessAsync(source, sheetDefId, [1]);

        Assert.Null(impact.CascadeMonths);
        Assert.Equal(SourceImpactReasons.WholeYearAnnualColumn, impact.WholeYearReason);
    }

    /// <summary>
    /// Огляд M4: НЕ кругова звірка — справжня задача каскаду споживача (payload <c>cascadePeriods</c> з ланцюга) виконується, і
    /// періоди, які вона порахувала (хук успіху), — рівно ті, що вікно позначило «буде перераховано».
    /// </summary>
    [Fact]
    public async Task Виконана_задача_каскаду_рахує_рівно_те_що_показало_вікно()
    {
        var (source, consumer, sheetDefId, bindingId) = await ArrangeAsync(spreadSource: false, crossMonth: true, approveFebruary: false);

        var impact = await AssessAsync(source, sheetDefId, [1]);
        await ExecuteAsync(source, consumer, bindingId);

        var cascade = Assert.Single(await RowsAsync($"{nameof(IRecalculationJob)}~doc{consumer.DocumentId}-"));
        var payload = JsonDocument.Parse(cascade.Payload!).RootElement;
        var hook = Substitute.For<IRecalculationCompletionHook>();
        IReadOnlyDictionary<long, int[]>? ran = null;
        hook.OnSucceededAsync(Arg.Do<IReadOnlyDictionary<long, int[]>>(r => ran = r), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        await using (var db = Sql.CreateContext())
        {
            await NewJob(db, hook).ExecuteAsync(
                new RecalculationRequest(consumer.ProjectId, consumer.DocumentId, PeriodKey: null, TriggeredByUserId: null)
                {
                    Year = payload.GetProperty("year").GetInt32(),
                    RecalculateSubmitted = true,
                    SourceHop = payload.GetProperty("sourceHop").GetInt32(),
                    CascadePeriods = [.. payload.GetProperty("cascadePeriods").EnumerateArray().Select(e => e.GetInt32())],
                },
                Substitute.For<IJobProgress>(),
                CancellationToken.None);
        }

        Assert.NotNull(ran);
        Assert.Equal(ran[consumer.DocumentId].Order(), Recalculated(impact, consumer));
        Assert.Equal(FirstQuarter.Select(SourceDependencyWorld.Period), ran[consumer.DocumentId].Order());
    }

    /// <summary>
    /// Кілька споживачів і зв'язок: документ A читає <c>[Period:-1]</c> (січень–березень), документ B того самого довідника — ні
    /// (лише січень: правило міжмісячних читань — за документом, не перше обчислене), документ C — через ДРУГУ зв'язку (січень).
    /// </summary>
    [Fact]
    public async Task Кілька_споживачів_і_звязок_кожен_за_своїм_правилом()
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, code) = await world.RegistryAsync();
        var (secondRegistryId, secondCode) = await world.RegistryAsync();
        var source = await YearlySourceAsync();
        var a = await world.DocumentAsync([1, 2, 3], code);
        var b = await world.DocumentAsync([1, 2, 3], code);
        var c = await world.DocumentAsync([1, 2, 3], secondCode);
        await world.BindingAsync(source.TemplateId, registryId);
        var second = await world.BindingAsync(source.TemplateId, secondRegistryId);
        await using (var db = Sql.CreateContext())
        {
            await CrossMonthAsync(db, a);
            await StreamAsync(db, source);
        }

        var impact = await AssessAsync(source, source.SheetDefId, [1]);

        Assert.Equal(FirstQuarter.Select(SourceDependencyWorld.Period), Recalculated(impact, a));
        Assert.Equal([SourceDependencyWorld.Period(1)], Recalculated(impact, b));
        Assert.Equal([SourceDependencyWorld.Period(1)], Recalculated(impact, c));
        await using var check = Sql.CreateContext();
        var secondCode2 = await check.DocumentSourceBindings.Where(x => x.Id == second).Select(x => x.Code).SingleAsync();
        Assert.Equal(secondCode2, Assert.Single(impact.Consumers, x => x.DocumentId == c.DocumentId).BindingCode);
    }

    /// <summary>
    /// Огляд H1: «перерахувати й затверджені» ставить увесь рік (місяців немає) — і затверджений лютий, що читає січень, і
    /// березень, який людина могла правити після першої правки, отримують перерахунок з перекриттям.
    /// </summary>
    [Fact]
    public async Task Перекриття_затверджених_рахує_увесь_рік_споживача_і_затверджені_місяці()
    {
        var (source, consumer, sheetDefId, bindingId) = await ArrangeAsync(spreadSource: false, crossMonth: true, approveFebruary: true);
        await using (var host = NewHost())
        {
            await new SourceApprovalChangeTrigger(host.Db, Scheduler(host))
                .RequestApprovedDependentsRecalculationAsync(
                    source.DocumentId, sheetDefId, YearKey, cascadeMonths: null, new ApprovedRecalculationOverride(9, "акт"), CancellationToken.None);
        }

        var parent = Assert.Single(await RowsAsync($"{nameof(IRegistryImpactRecalculationJob)}~source-change-b{bindingId}-"));
        Assert.DoesNotContain("-m", parent.TargetKey!.Split("-y")[1], StringComparison.Ordinal);
        await using (var host = NewHost())
        {
            await new RegistryImpactRecalculationJob(
                    new RegistryImpactStore(host.Db), Trigger(host.Db, Scheduler(host)), graph: SourceDependencyWorld.Graph(host.Db))
                .ExecuteAsync(parent.Payload!, Substitute.For<IJobProgress>(), CancellationToken.None);
        }

        var periods = (await RowsAsync($"{nameof(IRecalculationJob)}~doc{consumer.DocumentId}-"))
            .Select(r => JsonDocument.Parse(r.Payload!).RootElement.GetProperty("periodKey").GetInt32())
            .Order()
            .ToList();
        Assert.Equal(FirstQuarter.Select(SourceDependencyWorld.Period), periods);
    }

    /// <summary>Прогноз, що падає (огляд M2); доказ місяців — «так».</summary>
    private sealed class FailingPlanner : ISourceCascadePlanner
    {
        public Task<IReadOnlyList<CascadePlannedPeriod>> PlanSourceCascadeAsync(
            int projectId, long documentId, int year, IReadOnlyCollection<int> cascade, CancellationToken ct)
            => throw new InvalidOperationException("planner down");

        public Task<bool> SourceKeepsMonthsAsync(long documentId, CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyDictionary<(long DocumentId, int PeriodKey), int>> StaleReadersAsync(
            IReadOnlyCollection<(long DocumentId, int Year, IReadOnlyCollection<int> Seeds)> groups, CancellationToken ct)
            => throw new InvalidOperationException("planner down");
    }
}
