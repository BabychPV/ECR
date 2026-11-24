// tests/Ecr.Infrastructure.Tests/Jobs/SourceMonthsQuartzTests.cs
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.DocumentDependencies;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using Quartz.Spi;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ PERF-06b (<c>D-572</c>, AC «річне/квартальне джерело будить лише змінені місяці») у режимі live — Quartz/InProcess
/// (W15z M1): правка місяця 10 річного джерела (HSE400) несе місяці крізь власний перерахунок джерела (злиття — об'єднанням)
/// і будить у місячного споживача з річною розкладкою (Land, <c>REGFIND(…, [Period].Year, m, …)</c> у колонці <c>M{m}</c>)
/// лише період 10. Контролі: місяці невідомі — увесь рік; читання місяця 10 в колонці іншого місяця — увесь рік; F5 тримає
/// лише період правки і пізніші (поки йде ланка) та лише поставлений період (потім).
/// </summary>
/// <remarks>
/// МУТАЦІЇ (локально): (1) <c>QuartzJobScheduler.MergesSourceMonths</c> → <c>false</c> — червоні «злиття», «несе місяці»
/// й «лише період власника»; (2) у <c>RegistryImpactRecalculationJob</c> <c>routedMonth</c> → завжди <c>null</c> — червоний
/// «лише період власника» і F5; (3) ворота F5 звужують рік (огляд b11) — червоний F5;
/// (4) <c>QuartzSourceMonths.AccumulateDirty</c> повертає лише нове тіло — червоний «злиття з виконуваною».
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "PERF-06b")]
public sealed class SourceMonthsQuartzTests(SqlServerFixture sql) : IAsyncLifetime
{
    private const int YearKey = (SourceDependencyWorld.Year * 100) + 1;

    private static readonly DateTime Now = new(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(30);

    private IScheduler? quartz;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (quartz is not null)
        {
            await quartz.Shutdown(waitForJobsToComplete: false);
        }
    }

    /// <summary>Злиття в задачу, що чекає: обидві з місяцями — об'єднання; хоч одна без — увесь рік (як черга в базі).</summary>
    [Fact]
    public async Task Злиття_в_Quartz_обєднує_місяці_а_постановка_без_місяців_їх_знімає()
    {
        var (jobs, _) = await StandbyAsync();
        var document = Random.Shared.NextInt64(1_000_000_000, 9_000_000_000);
        var target = CalculationTrigger.AutoTargetOf(document, new PeriodKey(YearKey));

        var id = await jobs.EnqueueCoalescedAsync<IRecalculationJob>(target, Auto(document, [3]), CancellationToken.None);
        Assert.True(jobs.MergesSourceMonths);
        Assert.Equal("3", MonthsOf(await PayloadAsync(id)));

        Assert.Equal(id, await jobs.EnqueueCoalescedAsync<IRecalculationJob>(target, Auto(document, [5, 3]), CancellationToken.None));
        Assert.Equal("3,5", MonthsOf(await PayloadAsync(id)));

        Assert.Equal(id, await jobs.EnqueueCoalescedAsync<IRecalculationJob>(target, Auto(document, null), CancellationToken.None));
        Assert.Null(MonthsOf(await PayloadAsync(id)));

        Assert.Equal(id, await jobs.EnqueueCoalescedAsync<IRecalculationJob>(target, Auto(document, [7]), CancellationToken.None));
        Assert.Null(MonthsOf(await PayloadAsync(id)));
    }

    /// <summary>
    /// Злиття з ВИКОНУВАНОЮ задачею: дві правки за її прогін — перепостановка несе місяці ОБОХ ([5,7]), а не лише останньої.
    /// </summary>
    [Fact]
    public async Task Злиття_з_виконуваною_накопичує_місяці_для_перепостановки()
    {
        var job = new RecordingRecalculationJob();
        var factory = Factory();
        quartz = await factory.GetScheduler();
        var services = new ServiceCollection();
        services.AddSingleton<Ecr.Domain.Abstractions.IClock, SystemClock>();
        services.AddSingleton<IRecalculationJob>(job);
        await using var provider = services.BuildServiceProvider();
        quartz.JobFactory = new AdapterFactory(provider);
        await quartz.Start();

        var jobs = new QuartzJobScheduler(factory, null, new SystemClock());
        var document = Random.Shared.NextInt64(1_000_000_000, 9_000_000_000);
        var target = CalculationTrigger.AutoTargetOf(document, new PeriodKey(YearKey));

        var running = await jobs.EnqueueCoalescedAsync<IRecalculationJob>(
            target, Auto(document, [3]), CancellationToken.None);
        await job.FirstStarted.Task.WaitAsync(Safety);

        Assert.Equal(running, await jobs.EnqueueCoalescedAsync<IRecalculationJob>(target, Auto(document, [5]), CancellationToken.None));
        Assert.Equal(running, await jobs.EnqueueCoalescedAsync<IRecalculationJob>(target, Auto(document, [7]), CancellationToken.None));

        job.Release.TrySetResult();
        var second = await job.SecondPayload.Task.WaitAsync(Safety);

        Assert.Equal("5,7", MonthsOf(second));
    }

    /// <summary>
    /// ✎ Огляд b11-2 (H): злита під час прогону робота стає в чергу (рядок прогресу <c>Queued</c>) ДО того, як задача, що
    /// виконувалась, отримає <c>Succeeded</c> — без шпарини, у якій документ не має жодної живої задачі (F5 відпустив би рік).
    /// </summary>
    /// <remarks>Мутація: адаптер не кличе <c>RequeueDirtyBeforeFinishAsync</c> — червоний (Succeeded раніше за постановку).</remarks>
    [Fact]
    public async Task Перепостановка_злитої_роботи_видима_до_завершення_задачі()
    {
        var job = new RecordingRecalculationJob();
        var events = new ConcurrentQueue<string>();
        var progress = Substitute.For<IJobProgressStore>();
        progress.When(p => p.QueueAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>(), Arg.Any<int?>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string?>()))
            .Do(c => events.Enqueue("queue:" + c.ArgAt<string>(0)));
        progress.When(p => p.FinishAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>(), Arg.Any<string?>()))
            .Do(c => events.Enqueue("finish:" + c.ArgAt<string>(0) + ":" + c.ArgAt<string>(1)));

        var factory = Factory();
        quartz = await factory.GetScheduler();
        var services = new ServiceCollection();
        services.AddSingleton<Ecr.Domain.Abstractions.IClock, SystemClock>();
        services.AddSingleton<IRecalculationJob>(job);
        services.AddSingleton(progress);
        await using var provider = services.BuildServiceProvider();
        quartz.JobFactory = new AdapterFactory(provider);
        await quartz.Start();

        var jobs = new QuartzJobScheduler(factory, progress, new SystemClock());
        var document = Random.Shared.NextInt64(1_000_000_000, 9_000_000_000);
        var target = CalculationTrigger.AutoTargetOf(document, new PeriodKey(YearKey));

        var running = await jobs.EnqueueCoalescedAsync<IRecalculationJob>(target, Auto(document, [3]), CancellationToken.None);
        await job.FirstStarted.Task.WaitAsync(Safety);
        Assert.Equal(running, await jobs.EnqueueCoalescedAsync<IRecalculationJob>(target, Auto(document, [5]), CancellationToken.None));

        job.Release.TrySetResult();
        await job.SecondPayload.Task.WaitAsync(Safety);

        var order = events.ToList();
        var finished = order.IndexOf($"finish:{running}:Succeeded");
        var requeued = order.FindIndex(e => e.StartsWith("queue:", StringComparison.Ordinal) && e != $"queue:{running}");
        Assert.True(finished >= 0 && requeued >= 0, string.Join(" | ", order));
        Assert.True(requeued < finished, string.Join(" | ", order));
    }
    /// <summary>
    /// ✎ Огляд b11 №3 (LOW): задача, що вичерпала стелю відкладення, ставить злиту під час прогону роботу в чергу ДО запису
    /// <c>Failed</c> — так само без шпарини для воріт F5, як і успіх.
    /// </summary>
    /// <remarks>Мутація: перепостановку на шляху вичерпання — після <c>FinishAsync(Failed)</c> (як доти) — червоний.</remarks>
    [Fact]
    public async Task Вичерпане_відкладення_ставить_злиту_роботу_до_Failed()
    {
        var job = new DeferringRecalculationJob();
        var events = new ConcurrentQueue<string>();
        var progress = Substitute.For<IJobProgressStore>();
        progress.When(p => p.QueueAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>(), Arg.Any<int?>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string?>()))
            .Do(c => events.Enqueue("queue:" + c.ArgAt<string>(0)));
        progress.When(p => p.FinishAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>(), Arg.Any<string?>()))
            .Do(c => events.Enqueue("finish:" + c.ArgAt<string>(0) + ":" + c.ArgAt<string>(1)));

        var factory = Factory();
        quartz = await factory.GetScheduler();
        var services = new ServiceCollection();
        services.AddSingleton<Ecr.Domain.Abstractions.IClock, SystemClock>();
        services.AddSingleton<IRecalculationJob>(job);
        services.AddSingleton(progress);
        services.AddSingleton(new JobWorkerOptions { Lanes = [JobLanes.Default], MaxDeferral = TimeSpan.Zero });
        await using var provider = services.BuildServiceProvider();
        quartz.JobFactory = new AdapterFactory(provider);
        await quartz.Start();

        var jobs = new QuartzJobScheduler(factory, progress, new SystemClock());
        var document = Random.Shared.NextInt64(1_000_000_000, 9_000_000_000);
        var target = CalculationTrigger.AutoTargetOf(document, new PeriodKey(YearKey));

        var running = await jobs.EnqueueCoalescedAsync<IRecalculationJob>(target, Auto(document, [3]), CancellationToken.None);
        await job.FirstStarted.Task.WaitAsync(Safety);
        Assert.Equal(running, await jobs.EnqueueCoalescedAsync<IRecalculationJob>(target, Auto(document, [5]), CancellationToken.None));
        job.Release.TrySetResult();

        var deadline = DateTime.UtcNow + Safety;
        while (!events.Contains($"finish:{running}:Failed") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        var order = events.ToList();
        var failed = order.IndexOf($"finish:{running}:Failed");
        var requeued = order.FindIndex(e => e.StartsWith("queue:", StringComparison.Ordinal) && e != $"queue:{running}");
        Assert.True(failed >= 0 && requeued >= 0, string.Join(" | ", order));
        Assert.True(requeued < failed, string.Join(" | ", order));
    }

    /// <summary>
    /// Повний шлях Quartz: правка місяця 10 річного джерела → <c>-auto</c> з місяцями [10] → доведені місяці → задача зв'язки
    /// <c>-m10</c> → залежні споживача лише за період 10. Контролі: місяці невідомі (правка не-місячної колонки) і читання
    /// місяця 10 у колонці M11 — увесь рік. ✎ W15z M1 (репетиція на справжніх даних): ще одна ОПУБЛІКОВАНА стара версія
    /// шаблону-споживача (PerMonthCopies, як Land 1.0.8–1.0.18) читає довідник поза власником — періоди документа на новій
    /// версії однаково звужуються (рішення по версії, не по шаблону).
    /// </summary>
    /// <remarks>Мутація: звуження по шаблону (AND по всіх версіях, <c>OwnedVersions</c> ігнорується) — червоний «old-version».</remarks>
    [Theory]
    [InlineData("owned", new[] { 10 })]
    [InlineData("old-version", new[] { 10 })]
    [InlineData("unknown-months", new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 })]
    [InlineData("misplaced", new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 })]
    public async Task Правка_місяця_джерела_будить_лише_період_власника(string arrangement, int[] expected)
    {
        var w = await ArrangeAsync(misplaced: arrangement == "misplaced", oldVersion: arrangement == "old-version");
        var (jobs, _) = await StandbyAsync();

        await RunSourceEditAsync(w, jobs, monthEdit: arrangement != "unknown-months");

        Assert.Equal(expected.Select(SourceDependencyWorld.Period), await DependentsPeriodsAsync(w.Consumer));
    }

    /// <summary>
    /// Немає рядка календаря власника місяця 10 (маршрутизація віддає комірку M10 кожному періоду): постановка будить увесь
    /// рік — і F5 тримає ввесь рік, зокрема РАНІШИЙ місяць (те саме рішення, <c>MonthOwnerRouting</c>), поки жива задача зв'язки
    /// і поки чекають залежні.
    /// </summary>
    /// <remarks>Мутація: ворота звужують без перевірки рядка календаря (<c>Narrows(…, true)</c>) — червоний.</remarks>
    [Fact]
    public async Task Без_рядка_календаря_власника_постановка_й_F5_беруть_увесь_рік()
    {
        var w = await ArrangeAsync(misplaced: false, skipMonth: 10);
        var (jobs, _) = await StandbyAsync();

        await RunSourceEditAsync(w, jobs, monthEdit: true, runParents: false);
        var parents = await KeysAsync($"{nameof(IRegistryImpactRecalculationJob)}~source-change-b{w.BindingId}-");
        Assert.Single(parents);

        Assert.True(await PendingAsync(w, 9));
        Assert.True(await PendingAsync(w, 11));

        await RunParentsAsync(w, jobs);
        Assert.Equal(
            Enumerable.Range(1, 12).Where(m => m != 10).Select(SourceDependencyWorld.Period),
            await DependentsPeriodsAsync(w.Consumer));

        foreach (var parent in parents)
        {
            await jobs.CancelAsync(parent, CancellationToken.None);
        }

        Assert.True(await PendingAsync(w, 9));
    }

    /// <summary>
    /// ✎ Огляд b11 (HIGH-1/2), Quartz: будиться лише період 10, а тримається ВЕСЬ рік документа — поки жива задача зв'язки
    /// <c>-m10</c> (зокрема січень-якір і раніший вересень), поки чекають залежні періоду 10 і поки чекає задача формул
    /// документа (читачі <c>CrossPeriodFanOut</c>: пізніші місяці й якір). Нічого живого — рік вільний.
    /// </summary>
    /// <remarks>
    /// Мутації: (1) <c>Affects</c> — «власник і пізніші» — червоні січень і вересень; (2) ворота не дивляться на задачу формул
    /// документа — червоний листопад/січень після залежних.
    /// </remarks>
    [Fact]
    public async Task F5_тримає_весь_рік_поки_живий_будь_який_крок_каскаду()
    {
        var w = await ArrangeAsync(misplaced: false);
        var (jobs, _) = await StandbyAsync();

        await RunSourceEditAsync(w, jobs, monthEdit: true, runParents: false);
        var parents = await KeysAsync($"{nameof(IRegistryImpactRecalculationJob)}~source-change-b{w.BindingId}-");
        Assert.Equal($"{nameof(IRegistryImpactRecalculationJob)}~source-change-b{w.BindingId}-y{SourceDependencyWorld.Year}-m10", Assert.Single(parents)[..^33]);

        foreach (var month in new[] { 1, 9, 10, 11 })
        {
            Assert.True(await PendingAsync(w, month), $"місяць {month} під час задачі зв'язки");
        }

        await RunParentsAsync(w, jobs);
        foreach (var parent in parents)
        {
            await jobs.CancelAsync(parent, CancellationToken.None);
        }

        var dependents = Assert.Single(await KeysAsync(
            $"{nameof(IRecalculationJob)}~{CalculationTrigger.DependentsTargetOf(w.Consumer.DocumentId, new PeriodKey(SourceDependencyWorld.Period(10)))}~"));
        foreach (var month in new[] { 1, 9, 11 })
        {
            Assert.True(await PendingAsync(w, month), $"місяць {month} поки чекають залежні періоду 10");
        }

        // Залежні періоду 10 завершились і поставили читачів (fan-out) — задача формул документа: листопад і якір чекають.
        await jobs.CancelAsync(dependents, CancellationToken.None);
        var readers = await jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(
            FormulaRecalculationTarget.Of(w.Consumer.DocumentId, SourceDependencyWorld.Period(11), createdByUserId: null),
            new
            {
                DocumentId = w.Consumer.DocumentId,
                TableInstanceId = 0L,
                PeriodKey = SourceDependencyWorld.Period(11),
                Cells = new[] { new { RowId = 0L, ColumnDefId = w.Consumer.MonthColumnDefIds[9], TableDefId = w.Consumer.TableDefId, PeriodKey = SourceDependencyWorld.Period(11) } },
            },
            CancellationToken.None);
        Assert.True(await PendingAsync(w, 11));
        Assert.True(await PendingAsync(w, 1));

        await jobs.CancelAsync(readers, CancellationToken.None);
        foreach (var month in new[] { 1, 9, 10, 11 })
        {
            Assert.False(await PendingAsync(w, month), $"місяць {month} без живих задач");
        }
    }
    /// <summary>
    /// Прогін ОДНОГО періоду поза ланцюжком (залежні періоду 10 після звуження) будить і пізніших читачів своїх змінених чисел
    /// (листопад, що читає жовтень); рік, каскад і крок ланцюжка — як доти, лише раніших.
    /// </summary>
    /// <remarks>Мутація: <c>WakesLaterReaders</c> → <c>false</c> — червоний перший рядок.</remarks>
    [Theory]
    [InlineData(209910, false, false, true)]
    [InlineData(209910, true, false, false)]
    [InlineData(null, false, false, false)]
    [InlineData(null, false, true, false)]
    public void Прогін_одного_періоду_будить_пізніших_читачів(int? period, bool chain, bool cascade, bool expected)
        => Assert.Equal(
            expected,
            RecalculationJob.WakesLaterReaders(
                new RecalculationRequest(0, 1, period, TriggeredByUserId: null) { CascadePeriods = cascade ? [209910] : null },
                chain));

    /// <summary>
    /// ✎ W15z M1: звуження до власника місяця — лише для періодів на версії з <c>OwnedVersions</c>; період на іншій версії
    /// шаблону — увесь рік (консервативно).
    /// </summary>
    /// <remarks>Мутація: сховище не перевіряє версію періоду — червоний (звужено й на чужій версії).</remarks>
    [Fact]
    public async Task Звуження_лише_для_періодів_на_версії_власника()
    {
        var w = await ArrangeAsync(misplaced: false);
        await using var db = Context();
        var store = new RegistryImpactStore(db);
        var first = SourceDependencyWorld.Period(1) / 100 * 100;

        var owned = await store.ListTemplateConsumersRoutedAsync(
            [w.Consumer.TemplateId], first, first + 99, null, 10, new HashSet<int> { w.Consumer.TemplateVersionId }, CancellationToken.None);
        Assert.Equal([SourceDependencyWorld.Period(10)], owned.Select(r => r.PeriodKey));

        var other = await store.ListTemplateConsumersRoutedAsync(
            [w.Consumer.TemplateId], first, first + 99, null, 10, new HashSet<int> { -1 }, CancellationToken.None);
        Assert.Equal(12, other.Count);
    }
    /// <summary>
    /// ✎ Огляд b11-2 (A), Quartz: будь-яка жива задача документа тримає рік — постановка БЕЗ цілі (дзеркало без цілі й payload),
    /// власний перерахунок <c>-auto</c>, задача формул людини; знята — рік вільний.
    /// </summary>
    /// <remarks>Мутація: ворота беруть лише задачі з ціллю каскаду — червоні «без цілі» й «-auto».</remarks>
    [Theory]
    [InlineData("plain")]
    [InlineData("auto")]
    [InlineData("user-formula")]
    public async Task F5_Quartz_будь_яка_задача_документа_тримає_рік(string kind)
    {
        var w = await ArrangeAsync(misplaced: false);
        var (jobs, _) = await StandbyAsync();
        var doc = w.Consumer.DocumentId;
        var april = SourceDependencyWorld.Period(4);

        var id = kind switch
        {
            "plain" => await jobs.EnqueueAsync<IRecalculationJob>(new { DocumentId = doc, PeriodKey = april }, CancellationToken.None),
            "auto" => await jobs.EnqueueCoalescedAsync<IRecalculationJob>(
                CalculationTrigger.AutoTargetOf(doc, new PeriodKey(april)), new { DocumentId = doc, PeriodKey = april }, CancellationToken.None),
            _ => await jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(
                FormulaRecalculationTarget.Of(doc, april, createdByUserId: 5),
                new { DocumentId = doc, TableInstanceId = 0L, PeriodKey = april, Cells = Array.Empty<object>() },
                CancellationToken.None),
        };

        Assert.True(await PendingAsync(w, 1));
        Assert.True(await PendingAsync(w, 12));

        await jobs.CancelAsync(id, CancellationToken.None);
        Assert.False(await PendingAsync(w, 1));
    }
    /// <summary>
    /// ✎ Огляд b11-2: звуження до власника місяця не діє — увесь рік — коли (C) версія має аркуш, що копіюється, чи документ має
    /// копії аркушів у році (маршрутизація копій не віддає власнику), (D) період-власник місяця на іншій версії (змішані версії року).
    /// </summary>
    /// <remarks>Мутації: не перевіряти аркуші, що копіюються / копії / версію власника — червоні відповідні рядки.</remarks>
    [Theory]
    [InlineData("copyable")]
    [InlineData("copy-instance")]
    [InlineData("owner-other-version")]
    public async Task Копії_аркушів_і_власник_на_іншій_версії_будять_увесь_рік(string arrangement)
    {
        var w = await ArrangeAsync(misplaced: false);
        await using (var db = Context())
        {
            switch (arrangement)
            {
                case "copyable":
                    await db.SheetDefs.Where(s => s.Id == w.Consumer.SheetDefId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsCopyable, true));
                    break;
                case "copy-instance":
                {
                    var original = new DocumentSheet(w.Consumer.DocumentId, w.Consumer.SheetDefId);
                    db.DocumentSheets.Add(original);
                    await db.SaveChangesAsync();
                    db.DocumentSheets.Add(original.CreateCopy(2));
                    await db.SaveChangesAsync();
                    await new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now))
                        .EnsureTableInstancesAsync(w.Consumer.DocumentId, new PeriodKey(SourceDependencyWorld.Period(3)), CancellationToken.None);
                    Assert.True(await db.TableInstances.AnyAsync(i => i.DocumentId == w.Consumer.DocumentId && i.SheetCopyId != null));
                    break;
                }

                default:
                {
                    var other = new TemplateVersion(w.Consumer.TemplateId, $"0.8.0.{Guid.NewGuid():N}"[..20], 1, Now);
                    db.TemplateVersions.Add(other);
                    await db.SaveChangesAsync();
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO doc.ProjectTemplateVersion (ProjectId, FromPeriodKey, TemplateVersionId, TemplateId)
                        VALUES ({w.Consumer.ProjectId}, {SourceDependencyWorld.Period(1)}, {w.Consumer.TemplateVersionId}, {w.Consumer.TemplateId}),
                               ({w.Consumer.ProjectId}, {SourceDependencyWorld.Period(10)}, {other.Id}, {w.Consumer.TemplateId});
                        """);
                    break;
                }
            }
        }

        await using var check = Context();
        var first = SourceDependencyWorld.Period(1) / 100 * 100;
        var rows = await new RegistryImpactStore(check).ListTemplateConsumersRoutedAsync(
            [w.Consumer.TemplateId], first, first + 99, null, 10, new HashSet<int> { w.Consumer.TemplateVersionId }, CancellationToken.None);
        Assert.True(rows.Count > 1, $"{arrangement}: звужено до {string.Join(",", rows.Select(r => r.PeriodKey))}");
        Assert.Contains(rows, r => r.PeriodKey == SourceDependencyWorld.Period(1));
    }
    /// <summary>✎ Огляд b11 (HIGH-1): ланка джерела з місяцем у масці тримає ВЕСЬ рік споживача, зокрема раніші місяці й якір.</summary>
    [Theory]
    [InlineData(1, PeriodKind.Monthly)]
    [InlineData(9, PeriodKind.Monthly)]
    [InlineData(10, PeriodKind.Monthly)]
    [InlineData(11, PeriodKind.Monthly)]
    [InlineData(1, PeriodKind.Yearly)]
    public void Ворота_тримають_увесь_рік_для_місяця_в_масці(int month, PeriodKind kind)
        => Assert.True(
            SourceCascadePendingGate.Affects(
                new PeriodScope(PeriodScopeKind.Year, 0b1_1111_1111_1110), 2099, 10, SourceDependencyWorld.Period(month), kind));
    /// <summary>
    /// Контроль черги в базі (не погіршено): той самий звужений набір — каскад споживача лише за період 10; без місяця — рік.
    /// </summary>
    [Theory]
    [InlineData(10, new[] { 10 })]
    [InlineData(null, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 })]
    public async Task Черга_в_базі_отримує_той_самий_звужений_набір(int? month, int[] expected)
    {
        var w = await ArrangeAsync(misplaced: false);
        await using var db = Context();
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.MergesCascadePeriods.Returns(true);
        var cascades = new ConcurrentBag<string>();
        jobs.EnqueueCoalescedDelayedAsync<IRecalculationJob>(
                Arg.Any<string>(), Arg.Do<object?>(p => cascades.Add(JsonSerializer.Serialize(p, System.Text.Json.JsonSerializerOptions.Web))),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("cascade");

        var request = new RegistryImpactRecalculationRequest(
            w.RegistryId, [], "test", SourceDependencyWorld.Year, w.BindingId, month);
        await new RegistryImpactRecalculationJob(new RegistryImpactStore(db), Trigger(db, jobs), graph: SourceDependencyWorld.Graph(db))
            .ExecuteAsync(JsonSerializer.Serialize(request, System.Text.Json.JsonSerializerOptions.Web), Substitute.For<IJobProgress>(), CancellationToken.None);

        var payload = JsonDocument.Parse(Assert.Single(cascades)).RootElement;
        Assert.Equal(
            expected.Select(SourceDependencyWorld.Period),
            payload.GetProperty("cascadePeriods").EnumerateArray().Select(e => e.GetInt32()));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record World(TestDocument Source, TestDocument Consumer, int RegistryId, int BindingId);

    private static object Auto(long document, int[]? months)
        => months is null
            ? new { DocumentId = document, PeriodKey = YearKey, RecalculateSubmitted = true }
            : new { DocumentId = document, PeriodKey = YearKey, RecalculateSubmitted = true, SourceMonths = months };

    private static string? MonthsOf(string? payload)
        => payload is not null && JsonDocument.Parse(payload).RootElement.TryGetProperty("sourceMonths", out var months)
           && months.ValueKind == JsonValueKind.Array
            ? string.Join(",", months.EnumerateArray().Select(e => e.GetInt32()))
            : null;

    private static StdSchedulerFactory Factory()
        => new(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-p6b-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "4",
        });

    /// <summary>Не запущений планувальник: задачі чекають; прогрес — у базі (ворота F5 бачать дзеркало).</summary>
    private async Task<(QuartzJobScheduler Jobs, IScheduler Scheduler)> StandbyAsync()
    {
        var factory = Factory();
        quartz = await factory.GetScheduler();
        return (new QuartzJobScheduler(factory, new JobProgressStore(Context()), new SystemClock()), quartz);
    }

    /// <summary>Актуальне тіло задачі: злите (<see cref="QuartzPayloadMerges"/>) чи з даних задачі.</summary>
    private async Task<string?> PayloadAsync(string jobId)
    {
        if (QuartzPayloadMerges.Take(jobId, out _) is { } merged)
        {
            QuartzPayloadMerges.Reopen(jobId);
            return merged;
        }

        return (await quartz!.GetJobDetail(new JobKey(jobId)))?.JobDataMap.GetString(QuartzJobScheduler.PayloadKey);
    }

    private async Task<List<string>> KeysAsync(string prefix)
        => [.. (await quartz!.GetJobKeys(GroupMatcher<JobKey>.AnyGroup())).Select(k => k.Name)
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    private async Task<List<int>> DependentsPeriodsAsync(TestDocument consumer)
    {
        var prefix = $"{nameof(IRecalculationJob)}~doc{consumer.DocumentId.ToString(CultureInfo.InvariantCulture)}-p";
        return [.. (await KeysAsync(prefix))
            .Where(k => k.Contains("-dependents~", StringComparison.Ordinal))
            .Select(k => int.Parse(k.AsSpan(prefix.Length, 6), CultureInfo.InvariantCulture))
            .Order()];
    }

    /// <summary>Правка джерела → <c>-auto</c> (місяці з payload Quartz) → успіх із доведеними місяцями → задачі зв'язки.</summary>
    private async Task RunSourceEditAsync(World w, QuartzJobScheduler jobs, bool monthEdit, bool runParents = true)
    {
        await using (var db = Context())
        {
            var column = monthEdit ? w.Source.MonthColumnDefIds[9] : w.Source.ColumnDefIds[1];
            await new SourceDocumentChangeHook(db, jobs, Trigger(db, jobs)).OnCellsEditedAsync(
                new ChangedPeriodsDictionary
                {
                    [w.Source.DocumentId] = [YearKey],
                    ChangedColumns = new Dictionary<int, IReadOnlySet<int>> { [YearKey] = new HashSet<int> { column } },
                },
                CancellationToken.None);
        }

        var auto = Assert.Single(await KeysAsync(
            $"{nameof(IRecalculationJob)}~{CalculationTrigger.AutoTargetOf(w.Source.DocumentId, new PeriodKey(YearKey))}~"));
        var months = MonthsOf(await PayloadAsync(auto));
        Assert.Equal(monthEdit ? "10" : null, months);

        // Успіх -auto: RecalculationJob віддає хуку доведені місяці payload (SourceKeepsMonthsAsync — джерело без методологій).
        await using (var db = Context())
        {
            await new SourceDocumentChangeHook(db, jobs).OnSucceededAsync(
                new ChangedPeriodsDictionary
                {
                    [w.Source.DocumentId] = [YearKey],
                    SourceMonths = months is null
                        ? null
                        : new Dictionary<int, IReadOnlySet<int>> { [YearKey] = months.Split(',').Select(int.Parse).ToHashSet() },
                },
                0,
                CancellationToken.None);
        }

        await jobs.CancelAsync(auto, CancellationToken.None);

        if (runParents)
        {
            await RunParentsAsync(w, jobs);
        }
    }

    private async Task RunParentsAsync(World w, QuartzJobScheduler jobs)
    {
        foreach (var parent in await KeysAsync($"{nameof(IRegistryImpactRecalculationJob)}~source-change-b{w.BindingId}-"))
        {
            var payload = (await quartz!.GetJobDetail(new JobKey(parent)))!.JobDataMap.GetString(QuartzJobScheduler.PayloadKey);
            await using var db = Context();
            await new RegistryImpactRecalculationJob(new RegistryImpactStore(db), Trigger(db, jobs), graph: SourceDependencyWorld.Graph(db))
                .ExecuteAsync(payload, Substitute.For<IJobProgress>(), CancellationToken.None);
        }
    }

    private async Task<bool> PendingAsync(World w, int month)
    {
        await using var db = Context();
        var gate = new SourceCascadePendingGate(db, gateCache: null, SourceDependencyWorld.Graph(db))
        {
            AutoBindingsProbe = _ => Task.FromResult<IReadOnlyCollection<int>>([w.BindingId]),
        };
        return await gate.IsPendingAsync(w.Consumer.DocumentId, SourceDependencyWorld.Period(month), CancellationToken.None);
    }

    /// <summary>
    /// Джерело — річний документ із M1..M12 і зв'язкою; споживач — місячний, версія <c>YearWithMonthColumns</c>, таблиця
    /// «місяці в колонках», обчислювані M1..M12 з <c>REGFIND(код, [Period].Year, m, …)</c> (misplaced — місяць 10 у M11 і навпаки).
    /// </summary>
    private async Task<World> ArrangeAsync(bool misplaced, int? skipMonth = null, bool oldVersion = false)
    {
        var world = new SourceDependencyWorld(sql);
        var (registryId, code) = await world.RegistryAsync();

        var source = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: YearKey, monthColumns: 12);
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var consumer = await builder.BuildAsync(
            periodKey: SourceDependencyWorld.Period(1), layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);
        // skipMonth — місяця немає в календарі проєкту (ні екземпляра, ні рядка періоду).
        for (var month = 2; month <= 12; month++)
        {
            if (month != skipMonth)
            {
                await builder.AddPeriodInstanceAsync(consumer, new PeriodKey(SourceDependencyWorld.Period(month)));
            }
        }

        await using (var db = Context())
        {
            await db.Projects.Where(p => p.Id == source.ProjectId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.PeriodKind, PeriodKind.Yearly));
            await db.TemplateVersions.Where(v => v.Id == consumer.TemplateVersionId)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.Layout, TemplateLayout.YearWithMonthColumns));
            var monthColumns = consumer.MonthColumnDefIds.ToList();
            await db.ColumnDefs.Where(c => monthColumns.Contains(c.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.DataType, CellDataType.Formula));

            foreach (var period in await db.Periods
                         .Where(p => p.ProjectId == source.ProjectId || p.ProjectId == consumer.ProjectId).ToListAsync())
            {
                period.AdvanceTo(PeriodState.Open, Now);
            }

            for (var month = 1; month <= 12; month++)
            {
                var column = misplaced && month is 10 or 11 ? (month == 10 ? 11 : 10) : month;
                var formula = new FormulaDef(
                    consumer.TableDefId, FormulaScope.Column,
                    $"REGFIELD(REGFIND('{code}', [Period].Year, {month.ToString(CultureInfo.InvariantCulture)}, 'S', 'I'), 'VALUE')",
                    ExpressionDialect.Template);
                formula.AssignColumn(consumer.MonthColumnDefIds[column - 1]);
                db.FormulaDefs.Add(formula);
            }

            await db.SaveChangesAsync();

            if (oldVersion)
            {
                // Опублікована стара версія того самого шаблону (не на періодах документа): читає місяць 10 у не-місячній колонці.
                var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
                var old = new TemplateVersion(consumer.TemplateId, $"0.9.0.{tag}", 1, Now);
                db.TemplateVersions.Add(old);
                await db.SaveChangesAsync();
                var name = new LocalizedText(new Dictionary<string, string> { ["en"] = "old" });
                var sheet = new SheetDef(old.Id, EcrCode.Create($"OLDS{tag}"), name, 1);
                db.SheetDefs.Add(sheet);
                await db.SaveChangesAsync();
                var table = new TableDef(sheet.Id, EcrCode.Create($"OLDT{tag}"), name, 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
                db.TableDefs.Add(table);
                await db.SaveChangesAsync();
                var column = new ColumnDef(table.Id, EcrCode.Create($"OLDC{tag}"), name, 1, CellDataType.Formula);
                db.ColumnDefs.Add(column);
                await db.SaveChangesAsync();
                var formula = new FormulaDef(
                    table.Id, FormulaScope.Column, $"REGFIELD(REGFIND('{code}', [Period].Year, 10, 'S', 'I'), 'VALUE')",
                    ExpressionDialect.Template);
                formula.AssignColumn(column.Id);
                db.FormulaDefs.Add(formula);
                await db.SaveChangesAsync();

                // Публікація — після структури (опублікована версія заморожена, ECR-TMPL-0409).
                await db.TemplateVersions.Where(v => v.Id == old.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(v => v.Status, TemplateVersionStatus.Published)
                        .SetProperty(v => v.PublishedAt, Now)
                        .SetProperty(v => v.PublishedByUserId, 1));
            }
        }

        var bindingId = await world.BindingAsync(source.TemplateId, registryId);
        return new World(source, consumer, registryId, bindingId);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static CalculationTrigger Trigger(EcrDbContext db, IBackgroundJobScheduler jobs)
        => new(jobs, new PeriodStore(db), new WorkflowStore(db), new DocumentStore(db), new DocumentSheetCopyStore(db));

    private sealed class AdapterFactory(IServiceProvider provider) : IJobFactory
    {
        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler)
            => new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance);

        public void ReturnJob(IJob job)
        {
        }
    }

    /// <summary>Перший прогін чекає бар'єра; другий віддає свій payload.</summary>
    /// <summary>Перший прогін чекає <see cref="Release"/>; кожен прогін — ресурс зайнятий (відкладення).</summary>
    private sealed class DeferringRecalculationJob : IRecalculationJob
    {
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        {
            if (FirstStarted.TrySetResult())
            {
                await Release.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            throw new JobDeferredException(TimeSpan.FromMilliseconds(50), "busy", "test-lock");
        }
    }

    private sealed class RecordingRecalculationJob : IRecalculationJob
    {
        private int runs;

        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> SecondPayload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                FirstStarted.TrySetResult();
                await Release.Task.WaitAsync(ct).ConfigureAwait(false);
                return;
            }

            SecondPayload.TrySetResult(payload?.ToString() ?? string.Empty);
        }
    }
}
