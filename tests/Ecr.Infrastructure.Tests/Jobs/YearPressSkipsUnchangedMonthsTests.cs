// tests/Ecr.Infrastructure.Tests/Jobs/YearPressSkipsUnchangedMonthsTests.cs
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
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
/// ✎ PERF-01/02 (аудит PERF1000 21.11, рішення людини §6 п. 1; <c>D-564a/b</c>): кнопка «Перерахувати» документа річної
/// розкладки рахує лише місяці зі зміненими входами й місяць на екрані; злите натискання не повертає ланцюжок на перший
/// місяць, а дописує лише змінені зроблені місяці.
/// </summary>
/// <remarks>
/// <para>
/// Справжні черга (ланцюжок кроків), сховище результатів, перемикання актуальності й детектор входів; оркестратор — фейк, що
/// пише результати (як у <see cref="RecalculationYearPerPeriodCommitTests"/>). Зміни входів — рядки <c>aud.CellChange</c>
/// (журнал — єдине, що читає детектор). Годинник — «слот» далеко в майбутньому (2071): глобальні входи D-562 (довідники,
/// публікації), які в спільній тестовій базі пишуть інші тести справжнім часом, лишаються старшими за межу.
/// </para>
/// <para>
/// МУТАЦІЇ (локально, не комітяться): (1) у <c>MonthCellChangesAsync</c> прибрати гілку «похідні після прогону» — червоний
/// <c>Каскад_формул_у_пізніший_місяць…</c>; (2) прибрати гілку не-місячної колонки якоря — червоний
/// <c>Правка_загальних_даних_якоря…</c>; (3) <c>PlanChainAsync</c> без місяця на екрані (forced) — червоний
/// <c>Нічого_не_змінилося…</c>; (4) без «немає актуального / новіший Failed» — червоний <c>Місяць_без_актуального…</c>;
/// (5) <c>CoalescePayloadTemplate</c> знову скидає поля ланцюжка — червоні обидва тести злиття; (6) <c>AbsorbChainSql</c> → старий
/// <c>CoalescePayloadSql</c> — червоний <c>Натискання_під_час_кроку…</c>; (7) у <c>PlanChainAsync</c> дописувати зроблені
/// місяці без перевірки — червоний <c>Злите_натискання_не_повертає…</c>.
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "PERF-01")]
public sealed partial class YearPressSkipsUnchangedMonthsTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private const int Year = 2026;

    private const int Presser = 7;

    private static readonly DateTime Baseline = new(2071, 3, 10, 8, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Press = Baseline.AddHours(4);

    private static readonly int[] Months = [202601, 202602, 202603, 202604, 202605, 202606];

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private int _methodologyVersionId;

    /// <summary>Гілка 5: читачі результатів власника (замість роутера місяців); <c>null</c> — без гілки.</summary>
    private Func<int, Task<IReadOnlyList<int>>>? _ownerReaders;

    /// <summary>Стеля версій для переліку залежностей (повторний огляд M-b); <c>null</c> — типова (64).</summary>
    private int? _versionLimit;

    private int _unitId;

    /// <summary>✎ PERF-06b: справжній граф залежностей (обсяг ребра зв'язки для помісячної гілки джерел).</summary>
    private bool _withGraph;

    [Fact]
    public async Task Правка_місяця_3_перераховує_лише_місяць_3_і_місяць_на_екрані()
    {
        var (builder, document) = await ArrangeAsync();
        await CellChangeAsync(document, 202603, MonthColumn(document, 3), CellChangeOrigins.UserEdit, Baseline.AddMinutes(1));

        var run = await PressAsync(builder, document, screen: 202605, Press);

        Assert.Equal([202603, 202605], run.Steps);
        Assert.Equal((RecalculationJob.SkippedUnchangedKey, "4", "2026-01, 2026-02, 2026-04, 2026-06"), run.Outcome);
        Assert.Equal("Succeeded", (await RowAsync(run.JobId))!.State);

        // Незмінні місяці лишили свій актуальний прогін (базовий).
        Assert.Equal(Months, await CurrentPeriodsAsync(document.DocumentId));
        Assert.Equal([202601, 202602, 202604, 202606], await CurrentPeriodsStartedAtAsync(document.DocumentId, Baseline));
    }

    /// <summary>Нічого не змінилося — рахується лише місяць на екрані (натискання не виглядає як «нічого не сталося»).</summary>
    [Fact]
    public async Task Нічого_не_змінилося_рахується_лише_місяць_на_екрані()
    {
        var (builder, document) = await ArrangeAsync();

        // Правка задовго до базового прогону — не зміна.
        await CellChangeAsync(document, 202603, MonthColumn(document, 3), CellChangeOrigins.UserEdit, Baseline.AddHours(-2));

        var run = await PressAsync(builder, document, screen: 202602, Press);

        Assert.Equal([202602], run.Steps);
        Assert.Equal((RecalculationJob.SkippedUnchangedKey, "5", "2026-01, 2026-03, 2026-04, 2026-05, 2026-06"), run.Outcome);
    }

    /// <summary>
    /// D-562: запас 60 хв на межі входів — правка за 30 хв ДО старту прогону місяця (довгий імпорт ставить <c>ChangedAt</c> на
    /// початку транзакції; прогін міг її не бачити) — зміна.
    /// </summary>
    [Fact]
    public async Task Правка_в_межах_запасу_до_прогону_рахується_зміною()
    {
        var (builder, document) = await ArrangeAsync();
        await CellChangeAsync(document, 202604, MonthColumn(document, 4), CellChangeOrigins.Import, Baseline.AddMinutes(-30));

        var run = await PressAsync(builder, document, screen: 202601, Press);

        Assert.Equal([202601, 202604], run.Steps);
    }

    [Fact]
    public async Task Повний_перерахунок_рахує_всі_відкриті_місяці()
    {
        var (builder, document) = await ArrangeAsync();

        var run = await PressAsync(builder, document, screen: 202602, Press, full: true);

        Assert.Equal(Months, run.Steps);
        Assert.Null(run.Outcome);
    }

    /// <summary>
    /// Міжперіодна залежність (<c>[Period:-1]</c>, читання введеної місячної колонки з усіх періодів, Q-17): правка місяця 3
    /// уже дала каскад формул у місяць 4 (задача формул переписала його похідну комірку ПІСЛЯ його прогону) — місяць 4 теж
    /// рахується: його методології бачили старі числа. Каскад, що нічого не змінив, рядків не пише — місяць 6 не будиться.
    /// </summary>
    [Fact]
    public async Task Каскад_формул_у_пізніший_місяць_будить_і_його()
    {
        var (builder, document) = await ArrangeAsync();
        await CellChangeAsync(document, 202603, MonthColumn(document, 3), CellChangeOrigins.UserEdit, Baseline.AddMinutes(1));
        await CellChangeAsync(document, 202604, document.ColumnDefIds[2], CellChangeOrigins.Recalculation, Baseline.AddMinutes(1));

        var run = await PressAsync(builder, document, screen: 202602, Press);

        Assert.Equal([202602, 202603, 202604], run.Steps);
    }

    /// <summary>
    /// Річний якір: не-місячна колонка якоря — загальні дані року, їх читає кожен місяць (D-540): правка будить УСІ місяці;
    /// і так само річний підсумок якоря, переписаний каскадом. Місячна колонка січня — лише січень.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Правка_загальних_даних_якоря_будить_усі_місяці(bool monthColumn, bool derived)
    {
        var (builder, document) = await ArrangeAsync();
        var column = monthColumn ? MonthColumn(document, 1) : document.ColumnDefIds[1];
        await CellChangeAsync(
            document, 202601, column, derived ? CellChangeOrigins.Recalculation : CellChangeOrigins.UserEdit, Baseline.AddMinutes(1));

        var run = await PressAsync(builder, document, screen: 202603, Press);

        Assert.Equal(monthColumn ? [202601, 202603] : Months, run.Steps);
    }

    /// <summary>
    /// Скрипт методології документа читає місячну колонку ІНШОГО місяця (<c>row.X[3]</c>, <c>row.X[period.Month - 1]</c>) —
    /// каскаду формул такого читання не видно, тож вхід будь-якого місяця будить усі місяці (як D-562 на рівні документа).
    /// Скрипт, що читає лише свій місяць (<c>row.X[period.Month]</c>), — ні.
    /// </summary>
    /// <remarks>МУТАЦІЯ: <c>ReadsOtherMonths</c> → <c>false</c> чи без гілки <c>{5} = 1</c> у запиті — червоний.</remarks>
    [Theory]
    [InlineData("rows from T1; var a = row.Hours[3]; output O1 : t = a;", true)]
    [InlineData("rows from T1; var a = row.Hours[period.Month - 1]; output O1 : t = a;", true)]
    [InlineData("rows from T1; var a = row.Hours[period.Month]; output O1 : t = a;", false)]
    public async Task Скрипт_що_читає_інший_місяць_будить_усі_місяці(string script, bool yearWide)
    {
        var (builder, document) = await ArrangeAsync();
        await using (var db = builder.CreateContext())
        {
            var methodology = new Methodology(
                EcrCode.Create($"P01S_{Guid.NewGuid().ToString("N")[..8]}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "s" }));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();
            db.CalculationBindings.Add(new CalculationBinding(
                document.TableDefId, document.ColumnDefIds[2], methodology.Id, "tons", "{}"));
            var version = new MethodologyVersion(
                methodology.Id, "1.0", CalculationLevel.Configuration, 1, Baseline, MethodologyEngine.Script);
            version.SetScript(script, [1]);
            db.MethodologyVersions.Add(version);
            await db.SaveChangesAsync();
        }

        Assert.Equal(yearWide, RecalculationJob.ReadsOtherMonths(
            Ecr.Scripting.ScriptCompiler.Compile(script, new Ecr.Scripting.ScriptCompileOptions(NumericMode.Strict, false)).Script!));

        await CellChangeAsync(document, 202602, MonthColumn(document, 2), CellChangeOrigins.UserEdit, Baseline.AddMinutes(1));
        var run = await PressAsync(builder, document, screen: 202605, Press);

        Assert.Equal(yearWide ? Months : [202602, 202605], run.Steps);
    }

    /// <summary>Місяць без актуального прогону чи з новішим проваленим прогоном документа — рахується завжди.</summary>
    [Fact]
    public async Task Місяць_без_актуального_чи_з_проваленим_прогоном_рахується()
    {
        var (builder, document) = await ArrangeAsync();
        await using (var db = Sql.CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"""
                DELETE FROM calc.CalculationResult WHERE CalculationRunId IN
                    (SELECT Id FROM calc.CalculationRun WHERE DocumentId = {document.DocumentId} AND PeriodKey = 202606);
                DELETE FROM calc.CalculationRun WHERE DocumentId = {document.DocumentId} AND PeriodKey = 202606;
                INSERT INTO calc.CalculationRun (ProjectId, PeriodKey, DocumentId, Status, StartedAt, FinishedAt)
                VALUES ({document.ProjectId}, 202602, {document.DocumentId}, N'Failed', {Baseline.AddMinutes(5)}, {Baseline.AddMinutes(6)});
                """);
        }

        var run = await PressAsync(builder, document, screen: 202604, Press);

        Assert.Equal([202602, 202604, 202606], run.Steps);
    }

    /// <summary>
    /// Режим Quartz (одна задача без ланцюжка) — увесь рік, як доти: там claim не поступається задачі формул документа, і
    /// рішення «що змінилось» могло б не побачити каскаду правки, що прийшла поруч.
    /// </summary>
    [Fact]
    public async Task Без_ланцюжка_Quartz_рахує_весь_рік_як_доти()
    {
        var (builder, document) = await ArrangeAsync();
        await using var db = builder.CreateContext();
        var before = await MaxRunIdAsync();

        var job = NewJob(builder, db, Press, scheduler: null, lease: null, queue: null);
        await job.ExecuteAsync(JsonSerializer.Serialize(PressPayload(document, 202602, full: false), Web), new Progress(null), CancellationToken.None);

        Assert.Equal(Months, await NewRunPeriodsAsync(document.DocumentId, before));
    }

    /// <summary>
    /// PERF-02 (рішення людини, читання H1 «лише змінені зроблені місяці»): нове натискання, злите в ланцюжок між кроками,
    /// НЕ починає його з першого місяця. Решта плану йде далі; зроблений місяць, чиї входи змінились ПІСЛЯ старту його кроку,
    /// дописується (один раз); зроблений місяць зі зміною ДО старту кроку — ні; шкала «період k з N»: N росте, k — ні назад.
    /// </summary>
    [Fact]
    public async Task Злите_натискання_не_повертає_ланцюжок_на_січень_і_дописує_лише_змінені_зроблені()
    {
        var (builder, document) = await ArrangeAsync();
        await using var host = NewHost();
        var scheduler = Scheduler(host);
        var target = RecalculateDocumentHandler.YearScopedTargetOf(document.DocumentId, Year, null);

        // Повний ланцюжок року (як «Повний перерахунок»), два кроки: січень, лютий.
        var jobId = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            target, PressPayload(document, 202601, full: true), CancellationToken.None, Presser);
        var steps = new List<int>();
        var totals = new List<(int Done, int? Total)>();
        for (var i = 0; i < 2; i++)
        {
            await StepAsync(builder, host, Press, steps, totals);
        }

        Assert.Equal([202601, 202602], steps);

        // Лютий змінено ДО старту його кроку (поза запасом) — не повтор; березень (не зроблений) і лютий ПІСЛЯ кроку — так.
        await CellChangeAsync(document, 202602, MonthColumn(document, 2), CellChangeOrigins.UserEdit, Press.AddHours(-2));
        await CellChangeAsync(document, 202605, MonthColumn(document, 5), CellChangeOrigins.UserEdit, Press.AddMinutes(1));

        // Натискання іншої людини — злиття в ланцюжок, що чекає.
        Assert.Equal(jobId, await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            target, PressPayload(document, 202605, full: false), CancellationToken.None, 8));
        var merged = JsonSerializer.Deserialize<RecalculationRequest>((await RowAsync(jobId))!.Payload!, Web)!;
        Assert.Equal((202602, true), (merged.ChainAfter, merged.ChainRecheck));

        await StepAsync(builder, host, Press, steps, totals);
        Assert.Equal([202601, 202602, 202603], steps);

        // Тепер лютий змінено після його кроку — наступне натискання дописує ЛИШЕ його, один раз.
        await CellChangeAsync(document, 202602, MonthColumn(document, 2), CellChangeOrigins.UserEdit, Press.AddMinutes(2));
        await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            target, PressPayload(document, 202604, full: false), CancellationToken.None, 8);
        while (await StepAsync(builder, host, Press, steps, totals))
        {
        }

        Assert.Equal([202601, 202602, 202603, 202602, 202604, 202605, 202606], steps);
        Assert.Equal("Succeeded", (await RowAsync(jobId))!.State);

        // Шкала: k (зроблено кроків) лише росте, N росте на дописаний місяць і не падає.
        Assert.Equal(totals.Select(t => t.Done).Order(), totals.Select(t => t.Done));
        Assert.Equal(totals.Select(t => t.Total ?? 0).Order(), totals.Select(t => t.Total ?? 0));
        Assert.Equal(7, totals[^1].Total);
    }

    /// <summary>
    /// PERF-02: натискання, що прийшло, поки крок ланцюжка ВИКОНУЄТЬСЯ (окремий рядок у черзі на ту саму ціль), поглинає
    /// ланцюжок з його прогресом: продовжує з наступного місяця, а не з першого; автор — той, хто натиснув.
    /// </summary>
    [Fact]
    public async Task Натискання_під_час_кроку_продовжує_ланцюжок_а_не_починає_спочатку()
    {
        var (builder, document) = await ArrangeAsync();
        await using var host = NewHost();
        var scheduler = Scheduler(host);
        var target = RecalculateDocumentHandler.YearScopedTargetOf(document.DocumentId, Year, null);
        var first = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            target, PressPayload(document, 202601, full: true), CancellationToken.None, Presser);

        var steps = new List<int>();
        await StepAsync(builder, host, Press, steps, null);
        var step = (await ClaimAsync(host))!;
        Assert.Equal(first, step.Claim.JobId);

        var behind = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            target, PressPayload(document, 202604, full: false, user: 8), CancellationToken.None, 8);
        Assert.NotEqual(first, behind);

        await RunClaimedAsync(builder, host, step, Press, steps, null);
        Assert.Equal("Cancelled", (await RowAsync(first))!.State);

        var revived = JsonSerializer.Deserialize<RecalculationRequest>((await RowAsync(behind))!.Payload!, Web)!;
        Assert.Equal((202602, true, 8), (revived.ChainAfter, revived.ChainRecheck, revived.TriggeredByUserId));
        Assert.Equal([202601, 202604], revived.ScreenPeriods!);

        while (await StepAsync(builder, host, Press, steps, null))
        {
        }

        Assert.Equal(Months, steps);
        Assert.Equal("Succeeded", (await RowAsync(behind))!.State);
    }

    /// <summary>«Повний перерахунок», злитий у ланцюжок між кроками, — явний перезапуск: з першого місяця, як доти.</summary>
    [Fact]
    public async Task Повний_злитий_у_ланцюжок_починає_з_першого_місяця()
    {
        var (builder, document) = await ArrangeAsync();
        await using var host = NewHost();
        var scheduler = Scheduler(host);
        var target = RecalculateDocumentHandler.YearScopedTargetOf(document.DocumentId, Year, null);
        await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            target, PressPayload(document, 202601, full: true), CancellationToken.None, Presser);
        var steps = new List<int>();
        await StepAsync(builder, host, Press, steps, null);
        await StepAsync(builder, host, Press, steps, null);

        await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            target, PressPayload(document, 202604, full: true), CancellationToken.None, Presser);
        await StepAsync(builder, host, Press, steps, null);

        Assert.Equal([202601, 202602, 202601], steps);
    }

    /// <summary>
    /// ✎ Огляд H2: «записано після прогону» — за <c>aud.CellChange.Id</c> проти межі прогону (<c>CellChangeWatermark</c>), а не
    /// за годинниками (Api пише <c>ChangedAt</c>, воркер — <c>FinishedAt</c>). Каскад, записаний ПІСЛЯ прогону годинником, що
    /// відстає на секунду, будить місяць; запис ДО прогону годинником, що поспішає на хвилину, — ні.
    /// </summary>
    /// <remarks>МУТАЦІЯ: повернути <c>ChangedAt &gt; Finished</c> замість <c>Id &gt; Watermark</c> — червоні обидва рядки.</remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Похідні_після_прогону_за_межею_журналу_а_не_за_годинником(bool afterRun)
    {
        var (builder, document) = await ArrangeAsync(beforeBaseline: afterRun
            ? null
            : d => CellChangeAsync(d, 202604, d.ColumnDefIds[2], CellChangeOrigins.Recalculation, Baseline.AddMinutes(1)));
        if (afterRun)
        {
            await CellChangeAsync(document, 202604, document.ColumnDefIds[2], CellChangeOrigins.Recalculation, Baseline.AddSeconds(-1));
        }

        var run = await PressAsync(builder, document, screen: 202602, Press);

        Assert.Equal(afterRun ? [202602, 202604] : [202602], run.Steps);
    }

    /// <summary>
    /// ✎ Огляд H2: актуальний прогін усього ПРОЄКТУ (без документа) чи прогін без межі журналу (до D-564) — межі «після прогону»
    /// немає, місяць рахується.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Прогін_проєкту_чи_без_межі_журналу_рахується(bool projectRun)
    {
        var (builder, document) = await ArrangeAsync();
        await using (var db = Sql.CreateContext())
        {
            if (projectRun)
            {
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO calc.CalculationRun (ProjectId, PeriodKey, DocumentId, Status, StartedAt, FinishedAt)
                    VALUES ({document.ProjectId}, 202606, NULL, N'Current', {Baseline.AddMinutes(5)}, {Baseline.AddMinutes(6)});
                    """);
            }
            else
            {
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE calc.CalculationRun SET CellChangeWatermark = NULL
                    WHERE DocumentId = {document.DocumentId} AND PeriodKey = 202606 AND Status = N'Current';
                    """);
            }
        }

        var run = await PressAsync(builder, document, screen: 202602, Press);

        Assert.Equal([202602, 202606], run.Steps);
    }

    /// <summary>
    /// ✎ Огляд H1: пізніший місяць, що читає обчислені числа ранішого (<c>[Period:#3]</c> на колонку <c>Calculated</c>), іде в
    /// план разом із ним — і прокидається СТАНОМ, коли ланцюжок, що мав його порахувати, скасували після кроку березня, чи
    /// березень порахував системний прогін одного періоду (авто, залежні B179): прогін березня новіший (Id) за прогін читача.
    /// </summary>
    /// <remarks>
    /// МУТАЦІЯ: прибрати правило «власник новіший за читача» в <c>ChangedMonthsCoreAsync</c> — червоні рядки «скасовано» й
    /// «прогін періоду»; прибрати замикання в <c>ChangedMonthsAsync</c> — червоний рядок «правка» (план першого кроку — лише
    /// {3}: читачів дописав би лише крок березня).
    /// </remarks>
    [Theory]
    [InlineData("edit")]
    [InlineData("cancelled")]
    [InlineData("periodRun")]
    public async Task Читач_обчислених_чисел_ранішого_місяця_не_губиться(string scenario)
    {
        var (builder, document) = await ArrangeAsync();
        await CrossReadAsync(document, kind: 5, offset: 3);

        if (scenario == "edit")
        {
            await CellChangeAsync(document, 202603, MonthColumn(document, 3), CellChangeOrigins.UserEdit, Baseline.AddMinutes(1));
            await using var press = NewHost();
            var jobId = await Scheduler(press).EnqueueCoalescedAsync<IRecalculationJob>(
                RecalculateDocumentHandler.YearScopedTargetOf(document.DocumentId, Year, null),
                PressPayload(document, 202601, full: false), CancellationToken.None, Presser);
            var done = new List<int>();
            await StepAsync(builder, press, Press, done, null);

            // Читачі березня — у плані вже з першого кроку (шкала «з 5» чесна з початку), а не лише коли ланцюжок дійде до березня.
            var planned = JsonSerializer.Deserialize<RecalculationRequest>((await RowAsync(jobId))!.Payload!, Web)!;
            Assert.Equal([202603, 202604, 202605, 202606], planned.ChainPlan!);
            Assert.Equal(5, planned.ChainTotal);
            while (await StepAsync(builder, press, Press, done, null))
            {
            }

            Assert.Equal([202601, 202603, 202604, 202605, 202606], done);
            return;
        }

        await using (var host = NewHost())
        {
            var scheduler = Scheduler(host);
            var steps = new List<int>();
            if (scenario == "cancelled")
            {
                // Натискання на березні: план {3,4,5,6}; крок березня — і ланцюжок скасовано (людина, межа, збій).
                var jobId = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
                    RecalculateDocumentHandler.YearScopedTargetOf(document.DocumentId, Year, null),
                    PressPayload(document, 202603, full: false), CancellationToken.None, Presser);
                await StepAsync(builder, host, Press, steps, null);
                Assert.Equal([202603], steps);
                await ExecAsync("UPDATE itg.JobProgress SET [State] = 'Cancelled' WHERE JobId = @id;", jobId);
            }
            else
            {
                // Автоперерахунок березня (один період, не ланцюжок).
                await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
                    CalculationTrigger.AutoTargetOf(document.DocumentId, new PeriodKey(202603)),
                    new { document.DocumentId, PeriodKey = 202603, RecalculateSubmitted = true }, CancellationToken.None);
                await StepAsync(builder, host, Press, steps, null);
                Assert.Equal([202603], steps);
            }
        }

        var next = await PressAsync(builder, document, screen: 202601, Press.AddHours(1));

        Assert.Equal([202601, 202604, 202605, 202606], next.Steps);
    }

    /// <summary>
    /// ✎ Огляд M4: документні входи D-562 будять місяці, чия межа входів не пізніша за зміну, — двійковий пошук за межами
    /// (базові прогони місяців — о різних годинах). Журнал структури версії документа і дані довідника (глобальний рядок —
    /// прибирається).
    /// </summary>
    /// <remarks>МУТАЦІЯ: <c>lo = mid + 1</c> → <c>hi = mid</c> в обох гілках (пошук «ніхто») — червоний.</remarks>
    [Theory]
    [InlineData("structure")]
    [InlineData("registry")]
    public async Task Документний_вхід_будить_лише_місяці_з_ранішою_межею(string kind)
    {
        var (builder, document) = await ArrangeAsync(staggered: true);

        // Межі: місяць k — Baseline + (k − 1) год − 60 хв. Зміна о Baseline + 1,5 год будить січень–березень.
        var at = Baseline.AddHours(1.5);
        int? registryId = null;
        try
        {
            await using (var db = builder.CreateContext())
            {
                if (kind == "structure")
                {
                    await db.Database.ExecuteSqlAsync($"""
                        INSERT INTO aud.StructureChange (ChangedAt, TemplateVersionId, EntityType, EntityId, ChangeClass, Operation,
                                                         ChangedByUserId)
                        VALUES ({at}, {document.TemplateVersionId}, N'cfg.FormulaDef', 1, 0, N'Update', 1)
                        """);
                }
                else
                {
                    var registry = new RegistryDef(
                        EcrCode.Create($"P01R{Guid.NewGuid().ToString("N")[..10]}"),
                        new LocalizedText(new Dictionary<string, string> { ["en"] = "perf01" }), isTemporal: false);
                    registry.MarkDataChanged(at);
                    db.RegistryDefs.Add(registry);
                    await db.SaveChangesAsync();
                    registryId = registry.Id;
                }
            }

            var run = await PressAsync(builder, document, screen: 202606, Baseline.AddHours(8));

            Assert.Equal([202601, 202602, 202603, 202606], run.Steps);
        }
        finally
        {
            if (registryId is { } id)
            {
                await using var cleanup = builder.CreateContext();
                await cleanup.Database.ExecuteSqlAsync($"DELETE FROM cfg.RegistryDef WHERE Id = {id}");
            }
        }
    }

    /// <summary>
    /// ✎ Огляд M4, гілка (5): читач РЕЗУЛЬТАТІВ місяця-власника (скрипт, колонка <c>Calculated</c> з чужим власником), чий
    /// власник має новіший прогін (прохід читача загубився), — рахується. Порядок — за Id прогонів.
    /// </summary>
    /// <remarks>МУТАЦІЯ: прибрати гілку (5) — червоний.</remarks>
    [Fact]
    public async Task Читач_результатів_з_новішим_власником_рахується()
    {
        var (builder, document) = await ArrangeAsync();
        _ownerReaders = owner => Task.FromResult<IReadOnlyList<int>>(owner == 202603 ? [202605] : []);

        await using (var host = NewHost())
        {
            await Scheduler(host).EnqueueCoalescedAsync<IRecalculationJob>(
                CalculationTrigger.AutoTargetOf(document.DocumentId, new PeriodKey(202603)),
                new { document.DocumentId, PeriodKey = 202603, RecalculateSubmitted = true }, CancellationToken.None);
            var steps = new List<int>();
            await StepAsync(builder, host, Press, steps, null);
        }

        var run = await PressAsync(builder, document, screen: 202601, Press.AddHours(1));

        Assert.Equal([202601, 202605], run.Steps);
    }

    /// <summary>
    /// ✎ Повторний огляд M-a: формула читає НЕ-місячну розрахункову колонку маршрутизованої таблиці (річне значення, власник —
    /// якір) — зміна лише січня (якоря) будить і всі пізніші місяці; зміна березня — лише березень.
    /// </summary>
    /// <remarks>МУТАЦІЯ: прибрати правило «річне значення — власник якір» у <c>CrossReadsAsync</c> — червоний рядок «січень».</remarks>
    [Theory]
    [InlineData(1, new[] { 202601, 202602, 202603, 202604, 202605, 202606 })]
    [InlineData(3, new[] { 202603 })]
    public async Task Річне_значення_якоря_будить_пізніші_місяці(int editedMonth, int[] expected)
    {
        var (builder, document) = await ArrangeAsync();
        await CrossReadAsync(document, kind: 0, offset: 0);
        await CellChangeAsync(
            document, 202600 + editedMonth, MonthColumn(document, editedMonth), CellChangeOrigins.UserEdit, Baseline.AddMinutes(1));

        var run = await PressAsync(builder, document, screen: 202600 + editedMonth, Press);

        Assert.Equal(expected, run.Steps);
    }

    /// <summary>
    /// ✎ Повторний огляд M-b: версій шаблону документа понад стелю — перелік залежностей неповний: кожен пізніший місяць
    /// читає кожен раніший (рахувати).
    /// </summary>
    /// <remarks>МУТАЦІЯ: прибрати перевірку <c>versionIds.Count &gt; CrossReadVersionLimit</c> — червоний.</remarks>
    [Fact]
    public async Task Версій_понад_стелю_кожен_пізніший_місяць_читач()
    {
        var (builder, document) = await ArrangeAsync();
        _versionLimit = 0;

        var run = await PressAsync(builder, document, screen: 202603, Press);

        Assert.Equal([202603, 202604, 202605, 202606], run.Steps);
    }

    /// <summary>
    /// ✎ Повторний огляд L-a: перерахунок ПОДАННЯ тримає лок аркуша, а не документа — його незакомічений запис може мати Id не
    /// більший за межу журналу прогону, що завершився паралельно. Такі записи (позначка <c>submit-recalc</c>) будять місяць і за
    /// часом (останні 60 хв до кінця прогону); той самий запис без позначки — ні (Id менший за межу).
    /// </summary>
    /// <remarks>МУТАЦІЯ: прибрати гілку позначки подання в <c>MonthCellChangesAsync</c> — червоний рядок «з позначкою».</remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Запис_перерахунку_подання_будить_місяць_за_часом(bool submitMarker)
    {
        var (builder, document) = await ArrangeAsync(beforeBaseline: async d =>
        {
            await using var db = Sql.CreateContext();
            var correlation = submitMarker ? CellChangeOrigins.SubmitRecalculationCorrelation : null;
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO aud.CellChange (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue,
                                            ChangedByUserId, Origin, CorrelationId)
                VALUES ({Baseline.AddMinutes(-5)}, 202604, {d.DocumentId}, {d.RowIds[0]}, N'R1', {d.ColumnDefIds[2]}, N'1', N'2', 1,
                        {CellChangeOrigins.Recalculation}, {correlation})
                """);
        });

        var run = await PressAsync(builder, document, screen: 202602, Press);

        Assert.Equal(submitMarker ? [202602, 202604] : [202602], run.Steps);
    }

    /// <summary>Залежність формули таблиці документа на колонку <c>Calculated</c> іншого місяця.</summary>
    private async Task CrossReadAsync(TestDocument document, byte kind, short offset)
    {
        await using var db = Sql.CreateContext();
        var column = new ColumnDef(
            document.TableDefId, EcrCode.Create($"CALC_{Guid.NewGuid().ToString("N")[..8]}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "calc" }), 90, CellDataType.Calculated);
        var target = new ColumnDef(
            document.TableDefId, EcrCode.Create($"FRM_{Guid.NewGuid().ToString("N")[..8]}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "formula" }), 91, CellDataType.Formula);
        db.ColumnDefs.AddRange(column, target);
        await db.SaveChangesAsync();
        var formula = new FormulaDef(document.TableDefId, FormulaScope.Column, "1", ExpressionDialect.Template);
        formula.AssignColumn(target.Id);
        db.FormulaDefs.Add(formula);
        await db.SaveChangesAsync();
        db.FormulaDependencies.Add(FormulaDependency.ForFormula(
            formula.Id, kind, document.TableDefId, null, column.Id, null, offset, 0));
        await db.SaveChangesAsync();
    }

    private static object PressPayload(TestDocument document, int screen, bool full, int user = Presser)
        => new
        {
            document.DocumentId,
            PeriodKey = (int?)null,
            Year,
            TriggeredByUserId = user,
            SkipUnchanged = !full,
            ForceFull = full,
            ScreenPeriods = new[] { screen },
        };

    private async Task<PressRun> PressAsync(
        TestDocumentBuilder builder, TestDocument document, int screen, DateTime at, bool full = false)
    {
        await using var host = NewHost();
        var scheduler = Scheduler(host);
        var jobId = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            RecalculateDocumentHandler.YearScopedTargetOf(document.DocumentId, Year, null),
            PressPayload(document, screen, full), CancellationToken.None, Presser);

        var steps = new List<int>();
        var messages = new List<string?>();
        while (await StepAsync(builder, host, at, steps, null, messages))
        {
        }

        // Крок ланцюжка обгортає повідомлення шкалою «період k з N» (jobs.chainStep) — підсумок усередині.
        (string, string?, string?)? outcome = null;
        foreach (var message in messages)
        {
            if (!JobProgressMessageCodec.TryDecode(message, out var envelope))
            {
                continue;
            }

            for (var e = envelope; e is not null; e = e.Inner)
            {
                if (e.Key is RecalculationJob.SkippedUnchangedKey or RecalculationJob.SkippedUnchangedWithLogKey)
                {
                    outcome = (e.Key, e.Params?["count"], e.Params?["months"]);
                }
            }
        }

        return new PressRun(jobId, steps, outcome);
    }

    /// <summary>Один крок, як виконавець: claim → задача → продовження чи завершення. <c>false</c> — брати нічого.</summary>
    private async Task<bool> StepAsync(
        TestDocumentBuilder builder, Host host, DateTime at, List<int> steps, List<(int Done, int? Total)>? totals,
        List<string?>? messages = null)
    {
        var step = await ClaimAsync(host);
        if (step is null)
        {
            return false;
        }

        await RunClaimedAsync(builder, host, step, at, steps, totals, messages);
        return true;
    }

    private async Task RunClaimedAsync(
        TestDocumentBuilder builder, Host host, ClaimedJob step, DateTime at, List<int> steps,
        List<(int Done, int? Total)>? totals, List<string?>? messages = null)
    {
        var request = JsonSerializer.Deserialize<RecalculationRequest>(step.PayloadJson!, Web)!;
        totals?.Add((request.ChainDone?.Length ?? 0, request.ChainTotal));
        var before = await MaxRunIdAsync();

        await using var db = builder.CreateContext();
        var job = NewJob(builder, db, at, Scheduler(host), new FixedLease(step.Claim), host.Queue);
        try
        {
            await job.ExecuteAsync(step.PayloadJson, new Progress(messages), CancellationToken.None);
            steps.AddRange(await NewRunPeriodsAsync(DocumentOf(request), before));
            Assert.True(await host.Queue.CompleteAsync(step.Claim, CancellationToken.None));
        }
        catch (JobContinuedException continued)
        {
            steps.AddRange(await NewRunPeriodsAsync(DocumentOf(request), before));
            Assert.True(await host.Queue.ContinueAsync(
                step.Claim, continued.PayloadJson, continued.Delay, continued.SettleThrough, CancellationToken.None));
        }
    }

    private static long DocumentOf(RecalculationRequest request) => request.DocumentId;

    private RecalculationJob NewJob(
        TestDocumentBuilder builder, EcrDbContext db, DateTime at, IBackgroundJobScheduler? scheduler, IJobLeaseContext? lease,
        IJobQueue? queue)
    {
        var clock = new TestClock(at);
        return new RecalculationJob(
            db,
            new WritingRunner(builder, _methodologyVersionId, _unitId, at, _failPeriod),
            RunHandler(db, clock),
            FormulaService(),
            clock,
            jobs: scheduler,
            lease: lease,
            queue: queue,
            dependencyGraph: _withGraph ? SourceDependencyWorld.Graph(db) : null)
        {
            OwnerReadersProbe = _ownerReaders,
            CrossReadVersionLimit = _versionLimit ?? 64,
        };
    }

    private static Task<ClaimedJob?> ClaimAsync(Host host)
        => host.Queue.ClaimAsync([JobLanes.Recalc], "test/perf01", JobQueueLimits.DefaultLease, CancellationToken.None);

    private static DbBackgroundJobScheduler Scheduler(Host host)
        => new(host.Queue, new QuartzJobScheduler(null, new JobProgressStore(host.Db), new SystemClock()), new JobQueueSignal());

    private static int MonthColumn(TestDocument document, int month) => document.ColumnDefIds[2 + month];

    private async Task CellChangeAsync(TestDocument document, int period, int columnDefId, string origin, DateTime at)
    {
        await using var db = Sql.CreateContext();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO aud.CellChange (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue,
                                        ChangedByUserId, Origin)
            VALUES ({at}, {period}, {document.DocumentId}, {document.RowIds[0]}, N'R1', {columnDefId}, N'1', N'2', 1, {origin})
            """);
    }

    private async Task<long> MaxRunIdAsync()
    {
        await using var db = Sql.CreateContext();
        return await db.CalculationRuns.AsNoTracking().MaxAsync(r => (long?)r.Id) ?? 0;
    }

    private async Task<List<int>> NewRunPeriodsAsync(long documentId, long afterId)
    {
        await using var db = Sql.CreateContext();
        return await db.CalculationRuns.AsNoTracking()
            .Where(r => r.DocumentId == documentId && r.Id > afterId && r.PeriodKey != null)
            .OrderBy(r => r.Id)
            .Select(r => r.PeriodKey!.Value)
            .ToListAsync();
    }

    private async Task<List<int>> CurrentPeriodsAsync(long documentId)
    {
        await using var db = Sql.CreateContext();
        return await db.CalculationRuns.AsNoTracking()
            .Where(r => r.DocumentId == documentId && r.Status == CalculationRun.CurrentStatus && r.PeriodKey != null)
            .OrderBy(r => r.PeriodKey)
            .Select(r => r.PeriodKey!.Value)
            .ToListAsync();
    }

    private async Task<List<int>> CurrentPeriodsStartedAtAsync(long documentId, DateTime startedAt)
    {
        await using var db = Sql.CreateContext();
        return await db.CalculationRuns.AsNoTracking()
            .Where(r => r.DocumentId == documentId && r.Status == CalculationRun.CurrentStatus && r.PeriodKey != null
                        && r.StartedAt == startedAt)
            .OrderBy(r => r.PeriodKey)
            .Select(r => r.PeriodKey!.Value)
            .ToListAsync();
    }

    /// <summary>
    /// Документ із шістьма місяцями (січень — будівник, з місячними колонками M1..M6), прив'язка методології і БАЗОВИЙ
    /// повний ланцюжок року о <see cref="Baseline"/>: кожен місяць має актуальний прогін.
    /// </summary>
    /// <param name="beforeBaseline">Що зробити ДО базового прогону (записи журналу з меншим Id за межу прогону).</param>
    /// <param name="staggered">Базовий прогін місяця k — о <see cref="Baseline"/> + (k − 1) год (різні межі входів).</param>
    private async Task<(TestDocumentBuilder Builder, TestDocument Document)> ArrangeAsync(
        Func<TestDocument, Task>? beforeBaseline = null, bool staggered = false)
    {
        var builder = new TestDocumentBuilder(Sql.ConnectionString);
        var document = await builder.BuildAsync(monthColumns: 6);
        foreach (var month in Months.Skip(1))
        {
            await builder.AddPeriodInstanceAsync(document, new PeriodKey(month));
        }

        if (beforeBaseline is not null)
        {
            await beforeBaseline(document);
        }

        await using (var db = builder.CreateContext())
        {
            var tag = Guid.NewGuid().ToString("N")[..8];
            var methodology = new Methodology(
                EcrCode.Create($"P01_{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();

            db.CalculationBindings.Add(new CalculationBinding(
                document.TableDefId, document.ColumnDefIds[1], methodology.Id, "tons", "{}"));
            var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Baseline);
            db.MethodologyVersions.Add(version);
            await db.SaveChangesAsync();

            _methodologyVersionId = version.Id;
            _unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        }

        await using (var host = NewHost())
        {
            await Scheduler(host).EnqueueCoalescedAsync<IRecalculationJob>(
                RecalculateDocumentHandler.YearScopedTargetOf(document.DocumentId, Year, null),
                PressPayload(document, 202601, full: true), CancellationToken.None, Presser);
            var steps = new List<int>();
            while (await StepAsync(builder, host, staggered ? Baseline.AddHours(steps.Count) : Baseline, steps, null))
            {
            }

            Assert.Equal(Months, steps);
        }

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
            clock);

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

    private sealed record PressRun(string JobId, List<int> Steps, (string Key, string? Count, string? Months)? Outcome);

    /// <summary>Фейк оркестратора: пише вихід через справжнє сховище.</summary>
    private sealed class WritingRunner(
        TestDocumentBuilder builder, int methodologyVersionId, int unitId, DateTime at, int? failPeriod = null)
        : ICalculationRunner
    {
        public async Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
        {
            if (bindings.Count == 0)
            {
                return new ModuleProfile();
            }

            // F1 (PERF-06): детермінований збій методології місяця — без ретраю (JobRetryPolicy.IsWorthRetrying).
            if (periodKey.Value == failPeriod)
            {
                throw new InsufficientExecutionStackException("test: methodology of the failing month");
            }

            await using var db = builder.CreateContext();
            var store = new CalculationResultStore(db, new TestClock(at));
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

    private sealed class Progress(List<string?>? messages) : IJobProgress
    {
        public Task ReportAsync(int percent, string? message, CancellationToken ct)
        {
            messages?.Add(message);
            return Task.CompletedTask;
        }
    }
}
