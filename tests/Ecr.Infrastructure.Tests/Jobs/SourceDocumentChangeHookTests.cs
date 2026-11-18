// tests/Ecr.Infrastructure.Tests/Jobs/SourceDocumentChangeHookTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Integration;
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
/// B179 L2 (P-0118-33/34): хук зміни документа-джерела (<see cref="SourceDocumentChangeHook"/>) на справжній черзі в базі —
/// одна злита батьківська задача на (зв'язку, рік, місяць); за <c>ApprovedOnly</c> і з вимкненим бітом — нічого; ланка ≥ 8
/// — нічого. Замінює <c>Hse400LandDependentsTriggerTests</c> (хук лише для HSE400 і за ключем конфігурації).
/// </summary>
/// <remarks>Детерміновано: «у черзі» — рядок <c>Queued</c>, без таймінгу; періоди 2099xx.</remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Directive", "B179")]
public sealed class SourceDocumentChangeHookTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private const int Month = 10;
    private static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Пара (слово координатора): за <c>Any</c> правка ставить, за <c>ApprovedOnly</c> хук мовчить.</summary>
    /// <remarks>Мутація: у хуку прибрати фільтр <c>SourceState == Any</c> — рядок ApprovedOnly червоний.</remarks>
    [Theory]
    [InlineData(DocumentSourceState.Any, 1)]
    [InlineData(DocumentSourceState.ApprovedOnly, 0)]
    public async Task Правка_джерела_ставить_задачу_місяця_лише_за_Any(DocumentSourceState state, int expected)
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, _) = await world.RegistryAsync();
        var source = await world.DocumentAsync([Month]);
        var bindingId = await world.BindingAsync(source.TemplateId, registryId, state);

        await RunHookAsync(source, [Month], hop: 0);

        var rows = await ParentRowsAsync(bindingId);
        Assert.Equal(expected, rows.Count);
        if (expected == 1)
        {
            var row = rows[0];
            Assert.Equal(TargetKey(bindingId, Month), row.TargetKey);
            Assert.Equal("Queued", row.State);
            Assert.Contains($"\"bindingId\":{bindingId}", row.Payload, StringComparison.Ordinal);
            Assert.Contains($"\"month\":{Month}", row.Payload, StringComparison.Ordinal);
            Assert.Contains("\"hop\":0", row.Payload, StringComparison.Ordinal);
            Assert.Contains($"\"registryDefId\":{registryId}", row.Payload, StringComparison.Ordinal);
        }
    }

    /// <summary>Аварійний вимикач (Q2) — біт зв'язки <c>RecalculateDependents</c>; ключа конфігурації більше немає.</summary>
    /// <remarks>Мутація: у хуку прибрати <c>&amp;&amp; b.RecalculateDependents</c> — червоний.</remarks>
    [Fact]
    public async Task Біт_перераховувати_вимкнено_нічого_не_ставиться()
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, _) = await world.RegistryAsync();
        var source = await world.DocumentAsync([Month]);
        var bindingId = await world.BindingAsync(source.TemplateId, registryId);
        await world.SetRecalculateDependentsAsync(bindingId, false);

        await RunHookAsync(source, [Month], hop: 0);

        Assert.Empty(await ParentRowsAsync(bindingId));
    }

    /// <summary>Документ не шаблону-джерела — нічого.</summary>
    [Fact]
    public async Task Документ_іншого_шаблону_нічого_не_ставить()
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, _) = await world.RegistryAsync();
        var source = await world.DocumentAsync([Month]);
        var stranger = await world.DocumentAsync([Month]);
        var bindingId = await world.BindingAsync(source.TemplateId, registryId);

        await RunHookAsync(stranger, [Month], hop: 0);

        Assert.Empty(await ParentRowsAsync(bindingId));
    }

    /// <summary>
    /// ЗОНД ЗЛИТТЯ (координатор (a)): 50 правок джерела в одному місяці → ОДНА батьківська задача → рівно ОДИН
    /// перерахунок документа-споживача (за місяць зміни); інших місяців — нічого.
    /// </summary>
    /// <remarks>
    /// Мутація: у ключ цілі (<c>ForSourceChange</c>) додати унікальний Id — 50 задач, червоний.
    /// </remarks>
    [Fact]
    public async Task Пʼятдесят_правок_місяця_дають_одну_задачу_і_один_перерахунок_споживача()
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, code) = await world.RegistryAsync();
        var source = await world.DocumentAsync([Month]);
        var consumer = await world.DocumentAsync([9, Month, 11], code);
        var bindingId = await world.BindingAsync(source.TemplateId, registryId);

        for (var i = 0; i < 50; i++)
        {
            await RunHookAsync(source, [Month], hop: 0);
        }

        var parent = Assert.Single(await ParentRowsAsync(bindingId));
        Assert.Equal(TargetKey(bindingId, Month), parent.TargetKey);

        await RunParentAsync(parent.Payload!);
        await RunParentAsync(parent.Payload!); // повтор (наприклад, ретрай) — той самий перерахунок, злиття

        var recalculations = await RecalculationRowsAsync(consumer);
        var only = Assert.Single(recalculations);
        Assert.Equal(
            $"{nameof(IRecalculationJob)}~{SourceDependencyWorld.DependentsTarget(consumer, Month)}", only.TargetKey);
        Assert.Contains("\"sourceHop\":1", only.Payload, StringComparison.Ordinal);
    }

    /// <summary>Стеля ланцюжка: хук із ланкою 8 нічого не ставить (журнал помилки), 7 — ставить з ланкою 7.</summary>
    /// <remarks>Мутація: прибрати перевірку <c>sourceHop &gt;= MaxHops</c> у хуку — червоний.</remarks>
    [Fact]
    public async Task Ланка_на_стелі_нічого_не_ставить()
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, _) = await world.RegistryAsync();
        var source = await world.DocumentAsync([Month]);
        var bindingId = await world.BindingAsync(source.TemplateId, registryId);

        await RunHookAsync(source, [Month], hop: RegistryImpactRecalculationRequest.MaxHops);
        Assert.Empty(await ParentRowsAsync(bindingId));

        await RunHookAsync(source, [Month], hop: RegistryImpactRecalculationRequest.MaxHops - 1);
        var row = Assert.Single(await ParentRowsAsync(bindingId));
        Assert.Contains($"\"hop\":{RegistryImpactRecalculationRequest.MaxHops - 1}", row.Payload, StringComparison.Ordinal);
    }

    /// <summary>
    /// P-0118-34: постачена конфігурація не містить ключа, що вимикав біт зв'язки (корінь дефекту live).
    /// </summary>
    [Fact]
    public void Постачений_appsettings_не_має_ключа_Hse400RecalculateLandDependents()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Ecr.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        foreach (var file in Directory.GetFiles(Path.Combine(root.FullName, "src"), "appsettings*.json", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("Hse400RecalculateLandDependents", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// <see cref="RecalculationJob"/> кличе хук лише після УСПІХУ — з документом, періодом і ланкою з постановки залежного.
    /// </summary>
    /// <remarks>Мутація: передавати хуку 0 замість <c>request.SourceHop</c> — червоний.</remarks>
    [Fact]
    public async Task Перерахунок_кличе_хук_лише_після_успіху_з_періодом_і_ланкою()
    {
        var builder = new TestDocumentBuilder(Sql.ConnectionString);
        var document = await builder.BuildAsync();
        await using (var arrange = builder.CreateContext())
        {
            arrange.CalculationBindings.Add(new CalculationBinding(
                document.TableDefId, document.ColumnDefIds[1], 812, "tons", "{}"));
            await arrange.SaveChangesAsync();
        }

        var hook = Substitute.For<IRecalculationCompletionHook>();
        IReadOnlyDictionary<long, int[]>? seen = null;
        var hop = -1;
        hook.OnSucceededAsync(
                Arg.Do<IReadOnlyDictionary<long, int[]>>(s => seen = s), Arg.Do<int>(h => hop = h), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await RecalculateAsync(builder, document, hook, fail: false, sourceHop: 3);

        await hook.ReceivedWithAnyArgs(1).OnSucceededAsync(default!, default, default);
        Assert.NotNull(seen);
        Assert.Equal([document.PeriodKey.Value], seen[document.DocumentId]);
        Assert.Equal(3, hop);

        // Провалений прогін результатів не міняв — залежних не ставимо.
        var failed = Substitute.For<IRecalculationCompletionHook>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => RecalculateAsync(builder, document, failed, fail: true, sourceHop: 0));
        await failed.DidNotReceiveWithAnyArgs().OnSucceededAsync(default!, default, default);
    }

    private async Task RunHookAsync(TestDocument document, int[] months, int hop)
    {
        await using var host = NewHost();
        await new SourceDocumentChangeHook(host.Db, Scheduler(host))
            .OnSucceededAsync(
                new Dictionary<long, int[]> { [document.DocumentId] = [.. months.Select(SourceDependencyWorld.Period)] },
                hop,
                CancellationToken.None);
    }

    private async Task RunParentAsync(string payload)
    {
        await using var host = NewHost();
        var jobs = Scheduler(host);
        await new RegistryImpactRecalculationJob(
                new RegistryImpactStore(host.Db),
                new CalculationTrigger(
                    jobs, new PeriodStore(host.Db), new WorkflowStore(host.Db), new DocumentStore(host.Db),
                    new DocumentSheetCopyStore(host.Db)),
                graph: SourceDependencyWorld.Graph(host.Db))
            .ExecuteAsync(payload, Substitute.For<IJobProgress>(), CancellationToken.None);
    }

    private static DbBackgroundJobScheduler Scheduler(Host host)
        => new(
            host.Queue,
            new QuartzJobScheduler(null, new JobProgressStore(host.Db), new SystemClock()),
            new JobQueueSignal());

    private static string TargetKey(int bindingId, int? month)
        => $"{nameof(IRegistryImpactRecalculationJob)}~{RegistryImpactRecalculationTarget.ForSourceChange(bindingId, SourceDependencyWorld.Year, month)}";

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

    private static async Task RecalculateAsync(
        TestDocumentBuilder builder, TestDocument document, IRecalculationCompletionHook hook, bool fail, int sourceHop)
    {
        await using var db = builder.CreateContext();
        var job = new RecalculationJob(
            db, new Runner(fail), RunHandler(), FormulaService(), new TestClock(Now), completionHook: hook);

        await job.ExecuteAsync(
            new RecalculationRequest(document.ProjectId, document.DocumentId, document.PeriodKey.Value, TriggeredByUserId: null)
            {
                SourceHop = sourceHop,
            },
            Substitute.For<IJobProgress>(),
            CancellationToken.None);
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

    private static RunCalculationHandler RunHandler()
        => new(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            Substitute.For<ICalculationResultStore>(),
            Substitute.For<IBackgroundJobScheduler>(),
            Substitute.For<IUnitOfWork>(),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(),
            new TestClock(Now),
            Substitute.For<IAuditWriter>());

    /// <summary>Оркестратор-заглушка: успіх або збій методологій.</summary>
    private sealed class Runner(bool fail) : ICalculationRunner
    {
        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
            => fail
                ? throw new InvalidOperationException("оркестратор зламався")
                : Task.FromResult(new ModuleProfile());
    }
}
