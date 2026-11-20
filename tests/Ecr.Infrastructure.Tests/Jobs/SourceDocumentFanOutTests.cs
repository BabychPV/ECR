// tests/Ecr.Infrastructure.Tests/Jobs/SourceDocumentFanOutTests.cs
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// B179 L2: батьківська задача виду <c>SourceDocumentChanged</c> (<see cref="RegistryImpactRecalculationJob"/>) — споживачі
/// з ГРАФА залежностей (формула шаблону читає довідник зв'язки), а не з актуальних результатів; документи × періоди за
/// обсягом ребра; ланцюжок джерел у порядку з ланкою; стеля 8; цикл не перетинається. Замінює
/// <c>RegistryImpactBoundConsumersTests</c> (N3: ручний перелік споживачів зв'язки + результати методологій).
/// </summary>
/// <remarks>Справжні SQL, граф, сховище й тригер; періоди 2099xx.</remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Directive", "B179")]
public sealed class SourceDocumentFanOutTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private const int Month = 10;

    /// <summary>
    /// Обсяг «місяць»: лише період-власник місяця зміни, у кожного документа-споживача — і в того, що не має жодного
    /// результату; затверджений аркуш пропущено, поданий — поставлено; закритий період і чужий шаблон — ні.
    /// </summary>
    /// <remarks>
    /// Мутації: ігнорувати обсяг (завжди рік: <c>onlyMonth</c> = <c>null</c>) — червоний (9 і 11 поставлено); брати
    /// споживачів з результатів (<c>ListImpactedForYearAsync</c>) замість графа — червоний (жодного).
    /// </remarks>
    [Fact]
    public async Task Обсяг_місяць_ставить_лише_місяць_зміни_кожного_споживача_без_результату()
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, code) = await world.RegistryAsync();
        var (_, otherCode) = await world.RegistryAsync();
        var source = await world.DocumentAsync([Month]);
        var consumer = await world.DocumentAsync([9, Month, 11], code);
        var submitted = await world.DocumentAsync([Month], code);
        var approved = await world.DocumentAsync([Month], code);
        var closed = await world.DocumentAsync([Month], code);
        var stranger = await world.DocumentAsync([Month], otherCode);
        await world.SetSheetAsync(submitted, Month, DocumentStatus.Submitted);
        await world.SetSheetAsync(approved, Month, DocumentStatus.Approved);
        await world.CloseAsync(closed, Month);
        var bindingId = await world.BindingAsync(source.TemplateId, registryId);

        var queued = await RunAsync(Request(registryId, bindingId, Month, hop: 0));

        Assert.Equal(
            new[]
            {
                SourceDependencyWorld.DependentsTarget(consumer, Month),
                SourceDependencyWorld.DependentsTarget(submitted, Month),
            }.Order(StringComparer.Ordinal),
            queued.Keys.Order(StringComparer.Ordinal));
        Assert.All(queued.Values, payload => Assert.Contains("\"sourceHop\":1", payload, StringComparison.Ordinal));
        Assert.DoesNotContain(SourceDependencyWorld.DependentsTarget(stranger, Month), queued.Keys);
    }

    /// <summary>
    /// ✎ B179 L2 (перенесено з N4-тесту <c>RegistryImpactBoundConsumersTests</c>, метод якого прибрано): споживач без
    /// результату в періоді Grace ставиться так само, як в Open.
    /// </summary>
    /// <remarks>Мутація: фільтр <c>Open || Grace</c> → лише <c>Open</c> у <c>ListTemplateConsumersAsync</c> — червоний.</remarks>
    [Fact]
    public async Task Споживач_без_результату_в_періоді_Grace_ставиться()
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, code) = await world.RegistryAsync();
        var source = await world.DocumentAsync([Month]);
        var grace = await world.DocumentAsync([Month], code);
        await world.GraceAsync(grace, Month);
        var bindingId = await world.BindingAsync(source.TemplateId, registryId);

        var queued = await RunAsync(Request(registryId, bindingId, Month, hop: 0));

        Assert.Contains(SourceDependencyWorld.DependentsTarget(grace, Month), queued.Keys);
    }

    /// <summary>
    /// ✎ F13 batch-1118-8 (c), семантика B63/B79 названим тестом: затверджений аркуш споживача пропускається (не
    /// ставиться), поданий — ставиться (позначку «перераховано після подання» ставить сам перерахунок лише за зміни
    /// чисел, B79 — тут перевіряється постановка).
    /// </summary>
    /// <remarks>Мутація: у <c>CalculationTrigger.RequestDependentsAsync</c> не пропускати затверджені — червоний.</remarks>
    [Fact]
    public async Task Затверджений_аркуш_споживача_пропускається_поданий_ставиться()
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, code) = await world.RegistryAsync();
        var source = await world.DocumentAsync([Month]);
        var approved = await world.DocumentAsync([Month], code);
        var submitted = await world.DocumentAsync([Month], code);
        await world.SetSheetAsync(approved, Month, DocumentStatus.Approved);
        await world.SetSheetAsync(submitted, Month, DocumentStatus.Submitted);
        var bindingId = await world.BindingAsync(source.TemplateId, registryId);

        var queued = await RunAsync(Request(registryId, bindingId, Month, hop: 0));

        Assert.DoesNotContain(SourceDependencyWorld.DependentsTarget(approved, Month), queued.Keys);
        Assert.Contains(SourceDependencyWorld.DependentsTarget(submitted, Month), queued.Keys);
    }

    /// <summary>
    /// ✎ F13 batch-1118-8 (c): закритий період споживача не потрапляє ані в набір сховища
    /// (<see cref="IRegistryImpactStore.ListTemplateConsumersAsync"/>), ані в постановку.
    /// </summary>
    /// <remarks>
    /// Мутація: фільтр <c>Open || Grace</c> у <c>ListTemplateConsumersAsync</c> → без фільтра — червоний (набір сховища;
    /// тригер відкидає закритий період і сам, тому перевіряється й набір).
    /// </remarks>
    [Fact]
    public async Task Закритий_період_споживача_не_ставиться()
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, code) = await world.RegistryAsync();
        var source = await world.DocumentAsync([Month]);
        var closed = await world.DocumentAsync([Month], code);
        await world.CloseAsync(closed, Month);
        var bindingId = await world.BindingAsync(source.TemplateId, registryId);

        var queued = await RunAsync(Request(registryId, bindingId, Month, hop: 0));
        Assert.DoesNotContain(SourceDependencyWorld.DependentsTarget(closed, Month), queued.Keys);

        await using var db = Sql.CreateContext();
        var period = SourceDependencyWorld.Period(Month);
        var rows = await new RegistryImpactStore(db).ListTemplateConsumersAsync(
            [closed.TemplateId], period, period, null, CancellationToken.None);
        Assert.DoesNotContain(rows, r => r.DocumentId == closed.DocumentId);
    }

    /// <summary>Зміна за весь рік (видалення, перемикання стану — без місяця): усі відкриті періоди року споживача.</summary>
    [Fact]
    public async Task Без_місяця_ставиться_увесь_рік_споживача()
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, code) = await world.RegistryAsync();
        var source = await world.DocumentAsync([Month]);
        var consumer = await world.DocumentAsync([9, Month, 11], code);
        var bindingId = await world.BindingAsync(source.TemplateId, registryId);

        var queued = await RunAsync(Request(registryId, bindingId, month: null, hop: 0));

        Assert.Equal(
            new[] { 9, Month, 11 }.Select(m => SourceDependencyWorld.DependentsTarget(consumer, m)).Order(StringComparer.Ordinal),
            queued.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>Стеля ланцюжка в задачі: ланка 8 — нічого, навіть зі споживачем.</summary>
    /// <remarks>Мутація: прибрати перевірку <c>request.Hop &gt;= MaxHops</c> у задачі — червоний.</remarks>
    [Fact]
    public async Task Ланка_на_стелі_задача_нічого_не_ставить()
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, code) = await world.RegistryAsync();
        var source = await world.DocumentAsync([Month]);
        await world.DocumentAsync([Month], code);
        var bindingId = await world.BindingAsync(source.TemplateId, registryId);

        Assert.Empty(await RunAsync(Request(registryId, bindingId, Month, RegistryImpactRecalculationRequest.MaxHops)));
        Assert.Single(await RunAsync(Request(registryId, bindingId, Month, RegistryImpactRecalculationRequest.MaxHops - 1)));
    }

    /// <summary>Цикл через зв'язку (шаблон-джерело читає власний довідник): споживач у циклі не ставиться.</summary>
    /// <remarks>Мутація: прибрати пропуск споживача з компоненти циклу — червоний.</remarks>
    [Fact]
    public async Task Споживач_у_циклі_з_джерелом_не_ставиться()
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, code) = await world.RegistryAsync();
        var source = await world.DocumentAsync([Month], code);
        var consumer = await world.DocumentAsync([Month], code);
        var bindingId = await world.BindingAsync(source.TemplateId, registryId);

        var queued = await RunAsync(Request(registryId, bindingId, Month, hop: 0));

        Assert.Equal([SourceDependencyWorld.DependentsTarget(consumer, Month)], queued.Keys);
    }

    /// <summary>
    /// Ланцюжок S → A → B у порядку: B ставиться лише після успіху перерахунку A (хук з ланкою 1), з ланкою 2.
    /// </summary>
    [Fact]
    public async Task Ланцюжок_трьох_рівнів_іде_по_порядку_з_ланками()
    {
        var world = new SourceDependencyWorld(Sql);
        var (r1, code1) = await world.RegistryAsync();
        var (r2, code2) = await world.RegistryAsync();
        var s = await world.DocumentAsync([Month]);
        var a = await world.DocumentAsync([Month], code1);
        var b = await world.DocumentAsync([Month], code2);
        var b1 = await world.BindingAsync(s.TemplateId, r1);
        var b2 = await world.BindingAsync(a.TemplateId, r2);

        // Рівень 0: правка S.
        await HookAsync(s, hop: 0);
        await RunQueuedParentAsync(b1);
        var toA = Assert.Single(await RecalculationRowsAsync(a));
        Assert.Contains("\"sourceHop\":1", toA.Payload, StringComparison.Ordinal);
        Assert.Empty(await RecalculationRowsAsync(b));

        // Рівень 1: перерахунок A завершився (хук із ланкою з payload) — і лише тепер B.
        await HookAsync(a, hop: 1);
        await RunQueuedParentAsync(b2);
        var toB = Assert.Single(await RecalculationRowsAsync(b));
        Assert.Contains("\"sourceHop\":2", toB.Payload, StringComparison.Ordinal);
    }

    private static RegistryImpactRecalculationRequest Request(int registryId, int bindingId, int? month, int hop)
        => new(registryId, [], "B179 test", SourceDependencyWorld.Year, bindingId, month, hop);

    /// <summary>Виконує задачу на справжньому сховищі, графі й тригері; повертає ціль → payload поставлених перерахунків.</summary>
    private async Task<Dictionary<string, string>> RunAsync(RegistryImpactRecalculationRequest request)
    {
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        var queued = new Dictionary<string, string>(StringComparer.Ordinal);
        jobs.EnqueueCoalescedAsync<IRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(call =>
            {
                queued[call.ArgAt<string>(0)] = JsonSerializer.Serialize(call.ArgAt<object?>(1), JsonSerializerOptions.Web);
                return "recalc";
            });

        // ✎ PSS-07 (D-561): залежні — каскад, ставляться відкладеною постановкою; той самий облік.
        jobs.EnqueueCoalescedDelayedAsync<IRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(call =>
            {
                queued[call.ArgAt<string>(0)] = JsonSerializer.Serialize(call.ArgAt<object?>(1), JsonSerializerOptions.Web);
                return "recalc";
            });

        await using var db = Sql.CreateContext();
        await Job(db, jobs).ExecuteAsync(request, Substitute.For<IJobProgress>(), CancellationToken.None);
        return queued;
    }

    private static RegistryImpactRecalculationJob Job(EcrDbContext db, IBackgroundJobScheduler jobs)
        => new(
            new RegistryImpactStore(db),
            new CalculationTrigger(jobs, new PeriodStore(db), new WorkflowStore(db), new DocumentStore(db), new DocumentSheetCopyStore(db)),
            graph: SourceDependencyWorld.Graph(db));

    private async Task HookAsync(TestDocument document, int hop)
    {
        await using var host = NewHost();
        await new SourceDocumentChangeHook(host.Db, Scheduler(host))
            .OnSucceededAsync(
                new Dictionary<long, int[]> { [document.DocumentId] = [SourceDependencyWorld.Period(Month)] }, hop, CancellationToken.None);
    }

    private async Task RunQueuedParentAsync(int bindingId)
    {
        var target = $"{nameof(IRegistryImpactRecalculationJob)}~{RegistryImpactRecalculationTarget.ForSourceChange(bindingId, SourceDependencyWorld.Year, Month)}";
        await using var host = NewHost();
        var row = await host.Db.JobProgresses.AsNoTracking().SingleAsync(p => p.TargetKey == target && p.State == "Queued");
        await Job(host.Db, Scheduler(host)).ExecuteAsync(row.Payload, Substitute.For<IJobProgress>(), CancellationToken.None);
    }

    private async Task<List<JobProgress>> RecalculationRowsAsync(TestDocument document)
    {
        var prefix = $"{nameof(IRecalculationJob)}~doc{document.DocumentId}-";
        await using var db = Sql.CreateContext();
        return await db.JobProgresses.AsNoTracking().Where(p => p.TargetKey!.StartsWith(prefix)).ToListAsync();
    }

    private static DbBackgroundJobScheduler Scheduler(Host host)
        => new(
            host.Queue,
            new QuartzJobScheduler(null, new JobProgressStore(host.Db), new SystemClock()),
            new JobQueueSignal());
}
