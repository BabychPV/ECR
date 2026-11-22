// tests/Ecr.Infrastructure.Tests/Jobs/SourceCascadeMonthsTests.cs
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ PERF-06 (аудит PERF1000, рішення людини 22.11 §6 п. 8; <c>D-566</c>): правка документа-джерела B179 ставить у
/// споживачів ЛИШЕ періоди, що читають змінений місяць (обсяг ребра графа: «місяць» — період-власник місяця; «рік» і
/// літеральний місяць зміни — увесь рік; літеральний ІНШИЙ місяць — нічого), ОДНІЄЮ задачею на документ-споживач і рік; правки
/// за вікно каскаду (<see cref="CalculationTrigger.CascadeDebounce"/>, ≤ 15 с) зливаються в неї об'єднанням місяців.
/// </summary>
/// <remarks>
/// Справжні SQL, граф залежностей, сховище, тригер і черга в базі; періоди 2099xx.
/// <para>
/// МУТАЦІЇ (локально, не комітяться): (1) у <c>RegistryImpactRecalculationJob.SourceDocumentConsumersAsync</c> ігнорувати обсяг
/// (<c>onlyMonth = null</c>) — червоний <c>Правка_місяця_3…</c>; (2) <c>DbJobQueue.MergePressFieldsSql</c> без об'єднання
/// <c>$.cascadePeriods</c> — червоний <c>Дві_правки_за_вікно…</c> (лише {3}); (3) <c>CalculationTrigger.CoalescesCascadePeriods</c>
/// → <c>false</c> (поперіодно, як доти) — червоні <c>Правка_місяця_3…</c> і <c>Дві_правки_за_вікно…</c>; (4)
/// <c>CascadeDebounce = 0</c> — червоний <c>Дві_правки_за_вікно…</c> (claim бере одразу); (5) у хуку прибрати фільтр стану
/// <c>Any</c> — червоний <c>ApprovedOnly…</c>; (6) групування в задачі за документом без року — червоний
/// <c>Споживач_за_обсягом_усе…</c>.
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "PERF-06")]
public sealed class SourceCascadeMonthsTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private static readonly int[] HalfYear = [1, 2, 3, 4, 5, 6];

    private static readonly int[] FirstFour = [1, 2, 3, 4];

    /// <summary>
    /// Правка березня джерела: споживач «місяць» — лише березень; «рік» і «літеральний березень» — усі свої періоди року;
    /// «літеральний травень» — нічого. По одній задачі на споживача, поперіодних залежних немає.
    /// </summary>
    [Fact]
    public async Task Правка_місяця_3_ставить_споживачу_одну_задачу_лише_з_місяцями_що_його_читають()
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, code) = await world.RegistryAsync();
        var source = await world.DocumentAsync([3]);
        var monthly = await world.DocumentAsync(HalfYear, code);
        var yearly = await world.DocumentAsync(HalfYear, code, "[Period].Year");
        var march = await world.DocumentAsync(FirstFour, code, "[Period].Year, 3");
        var may = await world.DocumentAsync(FirstFour, code, "[Period].Year, 5");
        var bindingId = await world.BindingAsync(source.TemplateId, registryId);

        await using var host = NewHost();
        await Job(host).ExecuteAsync(Request(registryId, bindingId, 3), Substitute.For<IJobProgress>(), CancellationToken.None);

        var rows = await CascadeRowsAsync();
        Assert.Equal([Period(3)], rows[monthly.DocumentId].Periods);
        Assert.Equal(HalfYear.Select(Period), rows[yearly.DocumentId].Periods);
        Assert.Equal(FirstFour.Select(Period), rows[march.DocumentId].Periods);
        Assert.False(rows.ContainsKey(may.DocumentId));
        Assert.All(rows.Values, r => Assert.Equal(1, r.SourceHop));
        Assert.Equal(0, await PerPeriodDependentsAsync());
    }

    /// <summary>
    /// Обсяг «усе» (рік у ключах не впізнано — <c>[Period].Year - 1</c>): по ОДНІЙ задачі на документ і КОЖЕН його рік —
    /// задача року рахує лише свій рік (ціль <c>doc{id}-y{рік}-dependents</c>).
    /// </summary>
    [Fact]
    public async Task Споживач_за_обсягом_усе_отримує_задачу_на_кожен_рік()
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, code) = await world.RegistryAsync();
        var source = await world.DocumentAsync([3]);
        var consumer = await world.DocumentAsync([1, 2], code, "[Period].Year - 1");
        await using (var db = Sql.CreateContext())
        {
            // Другий рік того самого документа (2100) — інший календарний рік, своя задача.
            var builder = new TestDocumentBuilder(Sql.ConnectionString);
            await builder.AddPeriodInstanceAsync(consumer, new PeriodKey(210001));
            foreach (var period in await db.Periods
                         .Where(p => p.ProjectId == consumer.ProjectId && p.State != PeriodState.Open).ToListAsync())
            {
                period.AdvanceTo(PeriodState.Open, DateTime.UtcNow);
            }

            await db.SaveChangesAsync();
        }

        var bindingId = await world.BindingAsync(source.TemplateId, registryId);

        await using var host = NewHost();
        await Job(host).ExecuteAsync(Request(registryId, bindingId, 3), Substitute.For<IJobProgress>(), CancellationToken.None);

        var targets = await RowsAsync(consumer.DocumentId);
        Assert.Equal(
            new[]
            {
                Target(CalculationTrigger.SourceCascadeTargetOf(consumer.DocumentId, 2099)),
                Target(CalculationTrigger.SourceCascadeTargetOf(consumer.DocumentId, 2100)),
            },
            targets.Select(r => r.TargetKey!).Order(StringComparer.Ordinal));
        Assert.Equal([Period(1), Period(2)], Parse(targets[0].Payload).CascadePeriods!);
        Assert.Equal([210001], Parse(targets[1].Payload).CascadePeriods!);
    }

    /// <summary>
    /// Дві правки джерела (березень, травень) за вікно: ОДНА задача на споживача з місяцями {3, 5}; вікно від першої правки не
    /// подовжилось і ще не минуло (claim не бере). Правка ПІСЛЯ того, як задачу взяли й завершили, — окрема задача лише зі своїм
    /// місяцем.
    /// </summary>
    [Fact]
    public async Task Дві_правки_за_вікно_дають_одну_задачу_споживача_з_обома_місяцями_а_пізніша_окрему()
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, code) = await world.RegistryAsync();
        var source = await world.DocumentAsync([3, 5, 7]);
        var first = await world.DocumentAsync(HalfYear.Append(7).ToArray(), code);
        var second = await world.DocumentAsync(HalfYear.Append(7).ToArray(), code);
        var bindingId = await world.BindingAsync(source.TemplateId, registryId);

        await HookAndRunAsync(source, bindingId, 3);
        var before = await CascadeRowsAsync();
        Assert.Equal(2, before.Count);

        await Task.Delay(200);
        await HookAndRunAsync(source, bindingId, 5);

        var merged = await CascadeRowsAsync();
        Assert.Equal(before.Keys.Order(), merged.Keys.Order());
        foreach (var consumer in new[] { first, second })
        {
            var row = merged[consumer.DocumentId];
            Assert.Equal(before[consumer.DocumentId].JobId, row.JobId);
            Assert.Equal([Period(3), Period(5)], row.Periods);
            Assert.Equal(before[consumer.DocumentId].AvailableAt, row.AvailableAt);
        }

        await using (var host = NewHost())
        {
            Assert.Null(await host.Queue.ClaimAsync([JobLanes.Recalc], "test/perf06", JobQueueLimits.DefaultLease, CancellationToken.None));

            // Вікно минуло: обидві задачі взято й завершено.
            await using (var connection = new SqlConnection(Sql.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand(
                    "UPDATE itg.JobProgress SET AvailableAt = DATEADD(second, -1, SYSUTCDATETIME()) WHERE [State] = 'Queued' AND Lane = @lane;",
                    connection);
                command.Parameters.AddWithValue("@lane", JobLanes.Recalc);
                await command.ExecuteNonQueryAsync();
            }

            for (var i = 0; i < 2; i++)
            {
                var claimed = await host.Queue.ClaimAsync([JobLanes.Recalc], "test/perf06", JobQueueLimits.DefaultLease, CancellationToken.None);
                Assert.True(await host.Queue.CompleteAsync(claimed!.Claim, CancellationToken.None));
            }
        }

        await HookAndRunAsync(source, bindingId, 7);
        var later = await CascadeRowsAsync();
        foreach (var consumer in new[] { first, second })
        {
            Assert.NotEqual(merged[consumer.DocumentId].JobId, later[consumer.DocumentId].JobId);
            Assert.Equal([Period(7)], later[consumer.DocumentId].Periods);
        }
    }

    /// <summary>
    /// <c>ApprovedOnly</c>: правка (і перерахунок) незатвердженого місяця джерела споживачів не ставить; затвердження березня
    /// ставить споживачу одну задачу лише з березнем.
    /// </summary>
    [Fact]
    public async Task ApprovedOnly_правка_не_ставить_споживачів_а_затвердження_ставить_лише_свій_місяць()
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, code) = await world.RegistryAsync();
        var source = await world.DocumentAsync([3]);
        var consumer = await world.DocumentAsync(HalfYear, code);
        var bindingId = await world.BindingAsync(source.TemplateId, registryId, DocumentSourceState.ApprovedOnly);

        await using (var host = NewHost())
        {
            var hook = new SourceDocumentChangeHook(host.Db, Scheduler(host));
            var changed = new Dictionary<long, int[]> { [source.DocumentId] = [Period(3)] };
            await hook.OnSucceededAsync(changed, 0, CancellationToken.None);
            await hook.OnCellsEditedAsync(changed, CancellationToken.None);
        }

        Assert.Equal(0, await ParentsAsync(bindingId));
        Assert.Empty(await CascadeRowsAsync());

        await using (var host = NewHost())
        {
            await new SourceApprovalChangeTrigger(host.Db, Scheduler(host)).OnSourceSheetApprovalChangedAsync(
                source.DocumentId, source.SheetDefId, Period(3), SourceApprovalChange.Approved, 42, CancellationToken.None);
        }

        await RunQueuedParentsAsync(bindingId);
        var rows = await CascadeRowsAsync();
        Assert.Equal([Period(3)], Assert.Single(rows).Value.Periods);
        Assert.Equal(consumer.DocumentId, rows.Keys.Single());
    }

    /// <summary>
    /// ~200 документів-споживачів (рішення людини: «від HSE400 залежить ~200 документів на рік») і дві правки за вікно — рівно
    /// 200 задач, по одній на споживача з обома місяцями, а не 200 × місяці.
    /// </summary>
    [Fact]
    public async Task Двісті_споживачів_і_дві_правки_дають_двісті_задач()
    {
        const long first = 9_600_001;
        await using var host = NewHost();
        var trigger = Trigger(host);
        foreach (var month in new[] { 3, 5 })
        {
            for (var i = 0; i < 200; i++)
            {
                var queued = await trigger.RequestSourceCascadeAsync(first + i, 2099, [Period(month)], 1, CancellationToken.None);
                Assert.NotNull(queued.JobId);
            }
        }

        var rows = await CascadeRowsAsync();
        Assert.Equal(200, rows.Count);
        Assert.All(rows.Values, r => Assert.Equal([Period(3), Period(5)], r.Periods));
    }

    /// <summary>
    /// F4 (огляд PERF-06): правка з меншою ланкою джерел, що злилася в задачу каскаду, бере МЕНШУ ланку — інакше задача ланки 7,
    /// що поглинула правку ланки 1, впиралась би в стелю ланцюжка (<c>MaxHops</c>) на 6 ланок раніше.
    /// </summary>
    [Theory]
    [InlineData(7, 1, 1)]
    [InlineData(1, 7, 1)]
    [InlineData(3, 3, 3)]
    public async Task Злиття_правок_каскаду_бере_меншу_ланку(int firstHop, int secondHop, int expected)
    {
        const long document = 9_600_700;
        await using var host = NewHost();
        var trigger = Trigger(host);
        var first = await trigger.RequestSourceCascadeAsync(document, 2099, [Period(3)], firstHop, CancellationToken.None);
        var second = await trigger.RequestSourceCascadeAsync(document, 2099, [Period(5)], secondHop, CancellationToken.None);

        Assert.Equal(first.JobId, second.JobId);
        var row = Assert.Single(await CascadeRowsAsync()).Value;
        Assert.Equal([Period(3), Period(5)], row.Periods);
        Assert.Equal(expected, row.SourceHop);
    }

    /// <summary>Черга без об'єднання періодів (Quartz у пам'яті) — каскад поперіодно, як до D-566 (жоден місяць не губиться).</summary>
    [Fact]
    public async Task Черга_без_обєднання_ставить_поперіодно()
    {
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        var targets = new List<string>();
        jobs.EnqueueCoalescedDelayedAsync<IRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(call =>
            {
                targets.Add(call.ArgAt<string>(0));
                return "recalc";
            });

        var trigger = Trigger(jobs);
        Assert.False(trigger.CoalescesCascadePeriods);
        var queued = await trigger.RequestSourceCascadeAsync(9_600_500, 2099, [Period(3), Period(5)], 1, CancellationToken.None);

        Assert.Equal([Period(3), Period(5)], queued.Periods);
        Assert.Equal(
            new[] { Period(3), Period(5) }.Select(p => CalculationTrigger.DependentsTargetOf(9_600_500, new PeriodKey(p))),
            targets);
    }

    private static int Period(int month) => SourceDependencyWorld.Period(month);

    private static string Target(string target) => $"{nameof(IRecalculationJob)}~{target}";

    private static RegistryImpactRecalculationRequest Request(int registryId, int bindingId, int? month)
        => new(registryId, [], "PERF-06 test", SourceDependencyWorld.Year, bindingId, month, 0);

    private static RecalculationRequest Parse(string? payload)
        => JsonSerializer.Deserialize<RecalculationRequest>(payload!, JsonSerializerOptions.Web)!;

    /// <summary>Хук завершення перерахунку джерела за місяць і виконання поставлених ним батьківських задач.</summary>
    private async Task HookAndRunAsync(TestDocument source, int bindingId, int month)
    {
        await using (var host = NewHost())
        {
            await new SourceDocumentChangeHook(host.Db, Scheduler(host)).OnSucceededAsync(
                new Dictionary<long, int[]> { [source.DocumentId] = [Period(month)] }, 0, CancellationToken.None);
        }

        await RunQueuedParentsAsync(bindingId);
    }

    private async Task RunQueuedParentsAsync(int bindingId)
    {
        var prefix = $"{nameof(IRegistryImpactRecalculationJob)}~source-change-b{bindingId}-";
        await using var host = NewHost();
        foreach (var row in await host.Db.JobProgresses.AsNoTracking()
                     .Where(p => p.TargetKey!.StartsWith(prefix) && p.State == "Queued").ToListAsync())
        {
            await Job(host).ExecuteAsync(row.Payload, Substitute.For<IJobProgress>(), CancellationToken.None);
            await host.Db.JobProgresses.Where(p => p.JobId == row.JobId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.State, "Succeeded"));
        }
    }

    private async Task<int> ParentsAsync(int bindingId)
    {
        var prefix = $"{nameof(IRegistryImpactRecalculationJob)}~source-change-b{bindingId}-";
        await using var db = Sql.CreateContext();
        return await db.JobProgresses.CountAsync(p => p.TargetKey!.StartsWith(prefix));
    }

    /// <summary>Задачі каскаду, що чекають: документ → (задача, періоди, ланка, доступність).</summary>
    private async Task<Dictionary<long, (string JobId, int[] Periods, int SourceHop, DateTime? AvailableAt)>> CascadeRowsAsync()
    {
        await using var db = Sql.CreateContext();
        var rows = await db.JobProgresses.AsNoTracking()
            .Where(p => p.State == "Queued" && p.TargetKey!.StartsWith(nameof(IRecalculationJob) + "~") && p.TargetKey.EndsWith("-dependents"))
            .ToListAsync();
        var result = new Dictionary<long, (string, int[], int, DateTime?)>();
        foreach (var row in rows)
        {
            var request = Parse(row.Payload);
            Assert.Null(request.PeriodKey);
            Assert.Contains($"~doc{request.DocumentId}-y", row.TargetKey!, StringComparison.Ordinal);
            result.Add(request.DocumentId, (row.JobId, request.CascadePeriods!, request.SourceHop, row.AvailableAt));
        }

        return result;
    }

    private async Task<List<JobProgress>> RowsAsync(long documentId)
    {
        var prefix = $"{nameof(IRecalculationJob)}~doc{documentId}-";
        await using var db = Sql.CreateContext();
        return await db.JobProgresses.AsNoTracking().Where(p => p.TargetKey!.StartsWith(prefix))
            .OrderBy(p => p.TargetKey).ToListAsync();
    }

    /// <summary>Поперіодні залежні (<c>doc{id}-p{період}-dependents</c>) — їх каскад правки джерела більше не ставить.</summary>
    private async Task<int> PerPeriodDependentsAsync()
    {
        await using var db = Sql.CreateContext();
        return await db.JobProgresses.CountAsync(p => p.TargetKey!.StartsWith(nameof(IRecalculationJob) + "~")
                                                      && p.TargetKey.Contains("-p") && p.TargetKey.EndsWith("-dependents"));
    }

    private static RegistryImpactRecalculationJob Job(Host host)
        => new(
            new RegistryImpactStore(host.Db),
            new CalculationTrigger(
                Scheduler(host), new PeriodStore(host.Db), new WorkflowStore(host.Db), new DocumentStore(host.Db),
                new DocumentSheetCopyStore(host.Db)),
            graph: SourceDependencyWorld.Graph(host.Db));

    private static DbBackgroundJobScheduler Scheduler(Host host)
        => new(host.Queue, new QuartzJobScheduler(null, new JobProgressStore(host.Db), new SystemClock()), new JobQueueSignal());

    private static CalculationTrigger Trigger(Host host) => Trigger(Scheduler(host));

    /// <summary>Тригер на вільних сховищах: період відкритий, аркушів немає (лише постановка).</summary>
    private static CalculationTrigger Trigger(IBackgroundJobScheduler jobs)
    {
        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodStateAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(PeriodState.Open);

        var workflow = Substitute.For<IWorkflowStore>();
        workflow.GetSheetsAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ApprovalState>());

        var documents = Substitute.For<IDocumentStore>();
        documents.GetIncludedSheetIdsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

        var sheetCopies = Substitute.For<IDocumentSheetCopyStore>();
        sheetCopies.ListGroupsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<SheetCopyGroupInfo>());

        return new CalculationTrigger(jobs, periods, workflow, documents, sheetCopies);
    }
}
