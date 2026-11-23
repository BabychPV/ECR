// tests/Ecr.Infrastructure.Tests/Jobs/SourceYearlyMonthsTests.cs
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
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
/// ✎ PERF-06b (<c>D-572</c>, закриває «не зроблено» D-566): правка РІЧНОГО документа-джерела будить у споживачів лише місяці
/// правки, а не весь рік — місяці змінених місячних колонок (з проходу формул) їдуть крізь власний перерахунок джерела
/// (<c>-auto</c>, злиття — об'єднанням) до каскаду, якщо його методології й формули не переносять зміну в інші місяці.
/// </summary>
/// <remarks>
/// <para>Справжні SQL, черга в базі, граф і тригер; періоди 2099xx; джерело — проєкт <c>Yearly</c> з колонками M1..M12.</para>
/// <para>
/// МУТАЦІЇ (локально, не комітяться): (1) хук правки кличе <c>RequestAsync</c> без місяців — червоний
/// <c>Правка_місячних_колонок…</c>; (2) у <c>MergePressFieldsSql</c> прибрати гілку <c>sourceMonths</c> — червоні
/// <c>Правка_місячних_колонок…</c> і <c>Злиття_в_черзі…</c>; (3) <c>AbsorbChainSql</c> ELSE без умови <c>sourceMonths</c> —
/// червоний <c>Поглинання…</c>; (4) <c>SourceKeepsMonthsAsync</c> → завжди <c>true</c> — червоні рядки «не доведено»;
/// (5) хук ігнорує <c>SourceMonths</c> (завжди увесь рік) — червоний <c>Доведені_місяці…</c>.
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "PERF-06")]
public sealed class SourceYearlyMonthsTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private const int YearKey = (SourceDependencyWorld.Year * 100) + 1;

    private static readonly DateTime Now = new(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Правка місячних колонок річного джерела (березень, травень) ставить власний перерахунок із <c>sourceMonths</c> [3,5];
    /// ще одна правка (липень) — об'єднання [3,5,7]; правка НЕ-місячної колонки — місяці знято (увесь рік).
    /// </summary>
    [Fact]
    public async Task Правка_місячних_колонок_річного_джерела_несе_місяці_крізь_власний_перерахунок()
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, _) = await world.RegistryAsync();
        var source = await YearlySourceAsync();
        await world.BindingAsync(source.TemplateId, registryId);

        await EditAsync(source, source.MonthColumnDefIds[2], source.MonthColumnDefIds[4]);
        Assert.Equal("3,5", SourceMonthsOf(Assert.Single(await RecalculationRowsAsync(source)).Payload));

        await EditAsync(source, source.MonthColumnDefIds[6]);
        Assert.Equal("3,5,7", SourceMonthsOf(Assert.Single(await RecalculationRowsAsync(source)).Payload));

        await EditAsync(source, source.ColumnDefIds[1]);
        Assert.Null(SourceMonthsOf(Assert.Single(await RecalculationRowsAsync(source)).Payload));
    }

    /// <summary>Черга без об'єднання (Quartz лишає payload першої постановки) — місяців у payload немає: увесь рік, як доти.</summary>
    /// <remarks>Мутація: у <c>CalculationTrigger.RequestAsync(…, sourceMonths, …)</c> не перевіряти <c>MergesCascadePeriods</c> — червоний.</remarks>
    [Fact]
    public async Task Черга_без_обєднання_не_несе_місяців()
    {
        var source = await YearlySourceAsync();
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.MergesCascadePeriods.Returns(false);
        object? payload = null;
        jobs.EnqueueCoalescedAsync<IRecalculationJob>(
                Arg.Any<string>(), Arg.Do<object?>(p => payload = p), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("auto");

        await using var db = Sql.CreateContext();
        Assert.Equal("auto", await Trigger(db, jobs).RequestAsync(source.DocumentId, new PeriodKey(YearKey), [3], CancellationToken.None));
        Assert.Null(SourceMonthsOf(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web)));
    }

    /// <summary>Злиття в черзі: обидві з місяцями — об'єднання; хоч одна без — місяці знято.</summary>
    [Fact]
    public async Task Злиття_в_черзі_обєднує_місяці_а_постановка_без_місяців_їх_знімає()
    {
        var target = Target();
        await using var host = NewHost();
        var id = (await host.Queue.EnqueueAsync(Request(target, payload: """{"sourceMonths":[3]}"""), CancellationToken.None)).JobId;
        await host.Queue.EnqueueAsync(Request(target, payload: """{"sourceMonths":[5,3]}"""), CancellationToken.None);
        Assert.Equal("3,5", SourceMonthsOf((await RowAsync(id))!.Payload));

        await host.Queue.EnqueueAsync(Request(target, payload: "{}"), CancellationToken.None);
        Assert.Null(SourceMonthsOf((await RowAsync(id))!.Payload));

        await host.Queue.EnqueueAsync(Request(target, payload: """{"sourceMonths":[7]}"""), CancellationToken.None);
        Assert.Null(SourceMonthsOf((await RowAsync(id))!.Payload));
    }

    /// <summary>
    /// Пакетний пошук задач, що чекають (постановка каскаду за 12 періодів — одне звернення), дає те саме, що пошук по одній
    /// цілі: лише <c>Queued</c>; виконувана й відсутня — ні.
    /// </summary>
    /// <remarks>Мутація: у <c>FindQueuedByTargetsAsync</c> прибрати <c>[State] = 'Queued'</c> — червоний.</remarks>
    [Fact]
    public async Task Пакетний_пошук_задач_що_чекають_дорівнює_поодинокому()
    {
        var (queued, running, absent) = (Target(), Target(), Target());
        await using var host = NewHost();
        await host.Queue.EnqueueAsync(Request(running), CancellationToken.None);
        Assert.NotNull(await host.ClaimAsync());
        await host.Queue.EnqueueAsync(Request(queued, TimeSpan.FromMinutes(5)), CancellationToken.None);

        var batch = await host.Queue.FindQueuedByTargetsAsync([queued, running, absent], CancellationToken.None);

        Assert.Equal([queued], batch.Keys);
        foreach (var target in new[] { queued, running, absent })
        {
            Assert.Equal(await host.Queue.FindQueuedByTargetAsync(target, CancellationToken.None), batch.GetValueOrDefault(target));
        }
    }

    /// <summary>
    /// Поглинання при поверненні в чергу (відкладення/ретрай): задача позаду з місяцями поглинає виконувану БЕЗ місяців — місяці
    /// знято (виконувана несла «увесь рік»); обидві з місяцями — об'єднання.
    /// </summary>
    [Theory]
    [InlineData("{}", """{"sourceMonths":[3]}""", null)]
    [InlineData("""{"sourceMonths":[5]}""", """{"sourceMonths":[3]}""", new[] { 3, 5 })]
    public async Task Поглинання_задачею_позаду_знімає_чи_обєднує_місяці(string running, string behind, int[]? expected)
    {
        var target = Target();
        await using var host = NewHost();
        await host.Queue.EnqueueAsync(Request(target, payload: running), CancellationToken.None);
        var claimed = await host.ClaimAsync();
        Assert.NotNull(claimed);
        var behindId = (await host.Queue.EnqueueAsync(Request(target, payload: behind), CancellationToken.None)).JobId;

        Assert.True(await host.Queue.RequeueAsync(claimed.Claim, TimeSpan.Zero, CancellationToken.None));

        Assert.Equal(expected is null ? null : string.Join(",", expected), SourceMonthsOf((await RowAsync(behindId))!.Payload));
    }

    /// <summary>
    /// Успіх власного перерахунку річного джерела з місяцями [3,5]: хук отримує доведені місяці лише тоді, коли методології
    /// (рівень Module, скрипт свого місяця) і формули джерела не переносять місяць; інакше — без них (увесь рік).
    /// </summary>
    [Theory]
    [InlineData("none", true)]
    [InlineData("module-own-month-script", true)]
    [InlineData("module-other-month-script", false)]
    [InlineData("configuration-level", false)]
    [InlineData("formula-other-month", false)]
    [InlineData("formula-same-month", true)]
    public async Task Доказ_методологій_і_формул_джерела_вирішує_місяці(string arrangement, bool proven)
    {
        var builder = new TestDocumentBuilder(Sql.ConnectionString);
        var source = await YearlySourceAsync(builder);
        await ArrangeSourceAsync(source, arrangement);

        var hook = Substitute.For<IRecalculationCompletionHook>();
        IReadOnlyDictionary<long, int[]>? seen = null;
        hook.OnSucceededAsync(Arg.Do<IReadOnlyDictionary<long, int[]>>(s => seen = s), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await using (var db = builder.CreateContext())
        {
            await new RecalculationJob(db, new NoopRunner(), RunHandler(), FormulaService(), new TestClock(Now), completionHook: hook)
                .ExecuteAsync(
                    new RecalculationRequest(source.ProjectId, source.DocumentId, YearKey, TriggeredByUserId: null)
                    {
                        RecalculateSubmitted = true,
                        SourceMonths = [3, 5],
                    },
                    Substitute.For<IJobProgress>(),
                    CancellationToken.None);
        }

        Assert.NotNull(seen);
        Assert.Equal([YearKey], seen[source.DocumentId]);
        var months = (seen as ChangedPeriodsDictionary)?.SourceMonths?.GetValueOrDefault(YearKey);
        if (proven)
        {
            Assert.Equal([3, 5], months!.Order());
        }
        else
        {
            Assert.Null(months);
        }
    }

    /// <summary>
    /// Доведені місяці [3,5] → дві задачі зв'язки (<c>-m3</c>, <c>-m5</c>), не задача року → каскад місячного споживача лише за
    /// березень і травень (одна злита задача). Без доведених — одна задача року → увесь рік споживача.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Доведені_місяці_річного_джерела_будять_лише_ці_місяці_споживача(bool proven)
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, code) = await world.RegistryAsync();
        var source = await YearlySourceAsync();
        int[] months = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        var consumer = await world.DocumentAsync(months, code);
        var bindingId = await world.BindingAsync(source.TemplateId, registryId);

        await using (var host = NewHost())
        {
            var completed = new ChangedPeriodsDictionary
            {
                [source.DocumentId] = [YearKey],
                SourceMonths = proven ? new Dictionary<int, IReadOnlySet<int>> { [YearKey] = new HashSet<int> { 3, 5 } } : null,
            };
            await new SourceDocumentChangeHook(host.Db, Scheduler(host)).OnSucceededAsync(completed, 0, CancellationToken.None);
        }

        var parents = await ParentRowsAsync(bindingId);
        Assert.Equal(
            proven
                ? [$"{nameof(IRegistryImpactRecalculationJob)}~source-change-b{bindingId}-y{SourceDependencyWorld.Year}-m3",
                   $"{nameof(IRegistryImpactRecalculationJob)}~source-change-b{bindingId}-y{SourceDependencyWorld.Year}-m5"]
                : new[] { $"{nameof(IRegistryImpactRecalculationJob)}~source-change-b{bindingId}-y{SourceDependencyWorld.Year}" },
            parents.Select(p => p.TargetKey!).Order(StringComparer.Ordinal));

        foreach (var parent in parents)
        {
            await RunParentAsync(parent.Payload!);
        }

        var cascade = Assert.Single(await RecalculationRowsAsync(consumer));
        var periods = JsonDocument.Parse(cascade.Payload!).RootElement.GetProperty("cascadePeriods").EnumerateArray()
            .Select(e => e.GetInt32()).ToArray();
        Assert.Equal(
            proven ? [SourceDependencyWorld.Period(3), SourceDependencyWorld.Period(5)] : months.Select(SourceDependencyWorld.Period),
            periods);
    }

    /// <summary>Місяці <c>sourceMonths</c> payload через кому; <c>null</c> — поля немає (увесь рік).</summary>
    private static string? SourceMonthsOf(string? payload)
        => payload is not null && JsonDocument.Parse(payload).RootElement.TryGetProperty("sourceMonths", out var months)
           && months.ValueKind == JsonValueKind.Array
            ? string.Join(",", months.EnumerateArray().Select(e => e.GetInt32()))
            : null;

    /// <summary>Документ річного проєкту 2099 з колонками M1..M12; період 209901 відкритий.</summary>
    private async Task<TestDocument> YearlySourceAsync(TestDocumentBuilder? builder = null)
    {
        builder ??= new TestDocumentBuilder(Sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey: YearKey, monthColumns: 12);
        await using var db = Sql.CreateContext();
        await db.Projects.Where(p => p.Id == document.ProjectId).ExecuteUpdateAsync(s => s.SetProperty(p => p.PeriodKind, PeriodKind.Yearly));
        foreach (var period in await db.Periods.Where(p => p.ProjectId == document.ProjectId).ToListAsync())
        {
            period.AdvanceTo(PeriodState.Open, Now);
        }

        await db.SaveChangesAsync();
        return document;
    }

    private async Task ArrangeSourceAsync(TestDocument source, string arrangement)
    {
        await using var db = Sql.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..8];
        if (arrangement is "module-own-month-script" or "module-other-month-script" or "configuration-level")
        {
            var methodology = new Methodology(
                EcrCode.Create($"P6B_{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();
            db.CalculationBindings.Add(new CalculationBinding(source.TableDefId, source.ColumnDefIds[2], methodology.Id, "tons", "{}"));
            var version = arrangement == "configuration-level"
                ? new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now)
                : new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Module, 1, Now, MethodologyEngine.Script);
            if (arrangement != "configuration-level")
            {
                version.SetScript(
                    arrangement == "module-own-month-script"
                        ? "rows from T1; var a = row.Hours[period.Month]; output O1 : t = a;"
                        : "rows from T1; var a = row.Hours[3]; output O1 : t = a;",
                    [1]);
            }

            db.MethodologyVersions.Add(version);
            await db.SaveChangesAsync();
        }
        else if (arrangement is "formula-other-month" or "formula-same-month")
        {
            var formula = new FormulaDef(source.TableDefId, FormulaScope.Column, "1", ExpressionDialect.Template);
            formula.AssignColumn(source.MonthColumnDefIds[4]);
            db.FormulaDefs.Add(formula);
            await db.SaveChangesAsync();
            var read = arrangement == "formula-other-month" ? source.MonthColumnDefIds[3] : source.MonthColumnDefIds[4];
            db.FormulaDependencies.Add(FormulaDependency.ForFormula(formula.Id, 0, source.TableDefId, "R1", read, null, null, 0));
            await db.SaveChangesAsync();
        }
    }

    private async Task EditAsync(TestDocument source, params int[] columns)
    {
        await using var host = NewHost();
        var jobs = Scheduler(host);
        await new SourceDocumentChangeHook(host.Db, jobs, Trigger(host.Db, jobs))
            .OnCellsEditedAsync(
                new ChangedPeriodsDictionary
                {
                    [source.DocumentId] = [YearKey],
                    ChangedColumns = new Dictionary<int, IReadOnlySet<int>> { [YearKey] = columns.ToHashSet() },
                },
                CancellationToken.None);
    }

    private async Task RunParentAsync(string payload)
    {
        await using var host = NewHost();
        var jobs = Scheduler(host);
        await new RegistryImpactRecalculationJob(new RegistryImpactStore(host.Db), Trigger(host.Db, jobs), graph: SourceDependencyWorld.Graph(host.Db))
            .ExecuteAsync(payload, Substitute.For<IJobProgress>(), CancellationToken.None);
    }

    private static CalculationTrigger Trigger(EcrDbContext db, IBackgroundJobScheduler jobs)
        => new(jobs, new PeriodStore(db), new WorkflowStore(db), new DocumentStore(db), new DocumentSheetCopyStore(db));

    private static DbBackgroundJobScheduler Scheduler(Host host)
        => new(host.Queue, new QuartzJobScheduler(null, new JobProgressStore(host.Db), new SystemClock()), new JobQueueSignal());

    private async Task<List<JobProgress>> ParentRowsAsync(int bindingId)
    {
        var prefix = $"{nameof(IRegistryImpactRecalculationJob)}~source-change-b{bindingId}-";
        await using var db = Sql.CreateContext();
        return await db.JobProgresses.AsNoTracking().Where(p => p.TargetKey!.StartsWith(prefix)).ToListAsync();
    }

    private async Task<List<JobProgress>> RecalculationRowsAsync(TestDocument document)
    {
        var prefix = $"{nameof(IRecalculationJob)}~doc{document.DocumentId}-";
        await using var db = Sql.CreateContext();
        return await db.JobProgresses.AsNoTracking().Where(p => p.TargetKey!.StartsWith(prefix)).ToListAsync();
    }

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
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(), Substitute.For<ICurrentUser>(), new TestClock(Now),
            Substitute.For<IAuditWriter>());

    private sealed class NoopRunner : ICalculationRunner
    {
        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
            => Task.FromResult(new ModuleProfile());
    }
}
