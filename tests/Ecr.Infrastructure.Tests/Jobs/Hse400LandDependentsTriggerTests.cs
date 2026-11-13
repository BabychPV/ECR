// tests/Ecr.Infrastructure.Tests/Jobs/Hse400LandDependentsTriggerTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
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
/// HSE400 3b: після перерахунку документа HSE400 — одна злита задача перерахунку документів, методології яких читають
/// склад газу (<see cref="Hse400LandDependentsTrigger"/> + <see cref="RegistryImpactRecalculationJob"/>), на справжній
/// черзі в базі й справжніх сховищах.
/// </summary>
/// <remarks>
/// Детерміновано: жодного таймінгу, «у черзі» — рядок <c>Queued</c>, а не гонитва потоків.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Directive", "HSE400")]
public sealed class Hse400LandDependentsTriggerTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private const int Year = 2026;
    private static readonly DateTime Now = new(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("true", null, false)]
    [InlineData(null, "true", false)]
    [InlineData("false", "true", false)]
    [InlineData("true", "false", false)]
    [InlineData("true", "true", true)]
    public void Увімкнено_лише_разом_із_читанням_складу_з_документа(string? fromDocument, string? dependents, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [Hse400CompositionSnapshotLoader.EnabledKey] = fromDocument,
                [Hse400LandDependentsTrigger.EnabledKey] = dependents,
            })
            .Build();

        Assert.Equal(expected, Hse400LandDependentsTrigger.IsEnabled(configuration));
    }

    [Fact]
    public async Task Вимкнено_нічого_не_ставиться()
    {
        var (registryId, registryCode) = await NewRegistryAsync();
        var (hse400, template) = await NewHse400DocumentAsync();
        var jobs = Substitute.For<IBackgroundJobScheduler>();

        await using var db = Sql.CreateContext();
        await new Hse400LandDependentsTrigger(db, jobs, enabled: false, registryCode: registryCode, templateCode: template)
            .OnSucceededAsync(Scope(hse400), CancellationToken.None);

        await jobs.DidNotReceiveWithAnyArgs()
            .EnqueueCoalescedAsync<IRegistryImpactRecalculationJob>(default!, default, default, default);
        Assert.Equal(0, await QueuedAsync(registryId));
    }

    [Fact]
    public async Task Документ_HSE400_ставить_одну_злиту_задачу_року_і_повтори_її_не_множать()
    {
        var (registryId, registryCode) = await NewRegistryAsync();
        var (hse400, template) = await NewHse400DocumentAsync();

        await using var host = NewHost();
        var trigger = new Hse400LandDependentsTrigger(
            host.Db, Scheduler(host), enabled: true, registryCode: registryCode, templateCode: template);

        await trigger.OnSucceededAsync(Scope(hse400), CancellationToken.None);

        var row = Assert.Single(await RowsAsync(registryId));
        Assert.Equal("Queued", row.State);
        Assert.Contains("\"year\":2026", row.Payload, StringComparison.Ordinal);
        Assert.Contains($"\"registryDefId\":{registryId}", row.Payload, StringComparison.Ordinal);
        Assert.Contains("\"documentIds\":[]", row.Payload, StringComparison.Ordinal);

        // Ще два перерахунки HSE400, поки задача чекає, — та сама задача (злиття, не витіснення).
        await trigger.OnSucceededAsync(Scope(hse400), CancellationToken.None);
        await trigger.OnSucceededAsync(Scope(hse400), CancellationToken.None);

        var again = Assert.Single(await RowsAsync(registryId));
        Assert.Equal(row.JobId, again.JobId);
        Assert.Equal("Queued", again.State);

        // Задача пішла (воркер узяв її); новий перерахунок HSE400 — одна задача ПОЗАДУ, виконувана не переривається.
        var claimed = await host.ClaimLaneOfAsync<IRegistryImpactRecalculationJob>();
        Assert.Equal(row.JobId, claimed?.Claim.JobId);

        await trigger.OnSucceededAsync(Scope(hse400), CancellationToken.None);
        await trigger.OnSucceededAsync(Scope(hse400), CancellationToken.None);

        // ⛔ Мутація `EnqueueCoalescedAsync` → `EnqueueExclusiveAsync` у тригері: виконуваній ставиться запит скасування.
        var rows = await RowsAsync(registryId);
        Assert.Equal(2, rows.Count);
        var running = Assert.Single(rows, r => r.State == "Running");
        Assert.Equal(row.JobId, running.JobId);
        Assert.Null(running.CancelRequestedAt);
        Assert.Single(rows, r => r.State == "Queued");
    }

    [Fact]
    public async Task Інший_документ_не_HSE400_нічого_не_ставить()
    {
        var (registryId, registryCode) = await NewRegistryAsync();
        var (_, template) = await NewHse400DocumentAsync();
        var land = await new TestDocumentBuilder(Sql.ConnectionString).BuildAsync(periodKey: 202601);

        await using var host = NewHost();
        await new Hse400LandDependentsTrigger(
                host.Db, Scheduler(host), enabled: true, registryCode: registryCode, templateCode: template)
            .OnSucceededAsync(new Dictionary<long, int[]> { [land.DocumentId] = [202601] }, CancellationToken.None);

        Assert.Equal(0, await QueuedAsync(registryId));
    }

    /// <summary>
    /// ⛔ B63 (слово людини 07.11: «тільки погоджені не мають перераховуватися»): зміна джерела ставить перерахунок
    /// чернетки І ПОДАНОГО залежного (з позначкою поданих, <c>RecalculateSubmitted</c>), а затверджений, закритий і
    /// інший рік — ні.
    /// </summary>
    /// <remarks>
    /// ⛔ ДОКАЗ (локально): <c>RegistryImpactRecalculationJob</c> → <c>trigger.RequestAsync</c> (як доти) — поданий
    /// залежний не ставиться, червоний.
    /// </remarks>
    [Fact]
    public async Task Задача_року_ставить_чернетку_й_поданий_залежний_а_затверджений_ні()
    {
        // ⚠ DataChangedAt довідника порожній навмисно: рядки dic.* не змінюються, зміну дає документ HSE400.
        var (registryId, _) = await NewRegistryAsync();
        var open = await ArrangeDependentAsync(registryId, PeriodState.Open);
        var closed = await ArrangeDependentAsync(registryId, PeriodState.Closed);
        var submitted = await ArrangeDependentAsync(registryId, PeriodState.Open, DocumentStatus.Submitted);
        var approved = await ArrangeDependentAsync(registryId, PeriodState.Open, DocumentStatus.Approved);
        var otherYear = await ArrangeDependentAsync(registryId, PeriodState.Open, periodKey: 202501);

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        var payloads = new Dictionary<string, string>(StringComparer.Ordinal);
        jobs.EnqueueCoalescedAsync<IRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(call =>
            {
                payloads[call.ArgAt<string>(0)] = System.Text.Json.JsonSerializer.Serialize(
                    call.ArgAt<object?>(1), System.Text.Json.JsonSerializerOptions.Web);
                return "recalc-1";
            });

        await using (var db = Sql.CreateContext())
        {
            var job = new RegistryImpactRecalculationJob(
                new RegistryImpactStore(db), new CalculationTrigger(jobs, new PeriodStore(db), new WorkflowStore(db)));

            await job.ExecuteAsync(
                new RegistryImpactRecalculationRequest(registryId, [], "HSE400 2026 recalculated", Year),
                Substitute.For<IJobProgress>(),
                CancellationToken.None);
        }

        // Рівно два: чернетка й поданий; обидва — з позначкою поданих (задача пише журнал і позначку погоджувачу).
        Assert.Equal(
            new[]
            {
                CalculationTrigger.DependentsTargetOf(open.DocumentId, open.PeriodKey),
                CalculationTrigger.DependentsTargetOf(submitted.DocumentId, submitted.PeriodKey),
            }.Order(StringComparer.Ordinal),
            payloads.Keys.Order(StringComparer.Ordinal));
        Assert.All(payloads.Values, json => Assert.Contains("\"recalculateSubmitted\":true", json, StringComparison.Ordinal));
        foreach (var skipped in new[] { closed, approved, otherYear })
        {
            Assert.DoesNotContain(CalculationTrigger.DependentsTargetOf(skipped.DocumentId, skipped.PeriodKey), payloads.Keys);
        }

        await jobs.DidNotReceiveWithAnyArgs().EnqueueExclusiveAsync<IRecalculationJob>(default!, default, default, default);
    }

    [Fact]
    public async Task Перерахунок_кличе_гачок_лише_після_успіху_з_документом_і_періодом()
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
        hook.OnSucceededAsync(Arg.Do<IReadOnlyDictionary<long, int[]>>(s => seen = s), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await RecalculateAsync(builder, document, hook, fail: false);

        await hook.Received(1).OnSucceededAsync(Arg.Any<IReadOnlyDictionary<long, int[]>>(), Arg.Any<CancellationToken>());
        Assert.NotNull(seen);
        Assert.Equal([document.PeriodKey.Value], seen[document.DocumentId]);

        // Провалений прогін результатів не міняв — залежних не ставимо.
        var failed = Substitute.For<IRecalculationCompletionHook>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => RecalculateAsync(builder, document, failed, fail: true));
        await failed.DidNotReceiveWithAnyArgs().OnSucceededAsync(default!, default);
    }

    private static Dictionary<long, int[]> Scope(long documentId)
        => new Dictionary<long, int[]> { [documentId] = [(Year * 100) + 1] };

    private static DbBackgroundJobScheduler Scheduler(Host host)
        => new(
            host.Queue,
            new QuartzJobScheduler(null, new JobProgressStore(host.Db), new SystemClock()),
            new JobQueueSignal());

    private static string TargetKey(int registryId)
        => $"{nameof(IRegistryImpactRecalculationJob)}~{RegistryImpactRecalculationTarget.ForYear(registryId, Year)}";

    private async Task<List<Ecr.Domain.Entities.Integration.JobProgress>> RowsAsync(int registryId)
    {
        var target = TargetKey(registryId);
        await using var db = Sql.CreateContext();
        return await db.JobProgresses.AsNoTracking().Where(p => p.TargetKey == target).ToListAsync();
    }

    private async Task<int> QueuedAsync(int registryId) => (await RowsAsync(registryId)).Count;

    private async Task<(int Id, string Code)> NewRegistryAsync()
    {
        await using var db = Sql.CreateContext();
        var registry = new RegistryDef(
            EcrCode.Create($"HC{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Composition" }),
            isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();
        return (registry.Id, registry.Code);
    }

    /// <summary>Документ, чий шаблон грає роль HSE400 (код шаблону передається тригеру).</summary>
    private async Task<(long DocumentId, string TemplateCode)> NewHse400DocumentAsync()
    {
        var document = await new TestDocumentBuilder(Sql.ConnectionString).BuildAsync(periodKey: (Year * 100) + 1);
        await using var db = Sql.CreateContext();
        var code = await db.Templates.Where(t => t.Id == document.TemplateId).Select(t => t.Code).SingleAsync();
        return (document.DocumentId, code);
    }

    /// <summary>Документ із прогоном <c>Current</c>, чия методологія читає довідник.</summary>
    private async Task<(long DocumentId, PeriodKey PeriodKey)> ArrangeDependentAsync(
        int registryId, PeriodState state, DocumentStatus? sheet = null, int periodKey = (Year * 100) + 1)
    {
        var builder = new TestDocumentBuilder(Sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey: periodKey);

        await using var db = builder.CreateContext();

        var period = await db.Periods.SingleAsync(
            p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value);
        period.TransitionTo(PeriodState.Open, Now);
        if (state == PeriodState.Closed)
        {
            period.TransitionTo(PeriodState.Closed, Now);
        }

        if (sheet is { } status)
        {
            var approval = new ApprovalState(document.DocumentId, document.SheetDefId, document.PeriodKey.Value);
            approval.Submit(userId: 5, Now);
            if (status == DocumentStatus.Approved)
            {
                approval.Approve(userId: 6, Now);
            }

            db.ApprovalStates.Add(approval);
        }

        var methodology = new Methodology(
            EcrCode.Create($"HL{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        db.RegistryUses.Add(RegistryUse.ForMethodologyFormula(version.Id, "F1", registryId, "X"));

        var run = new CalculationRun(document.ProjectId, document.PeriodKey.Value, null, Now, document.DocumentId);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();

        run.Complete("Succeeded", Now, null, null);
        run.MakeCurrent();

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        await new CalculationResultStore(db, new TestClock(Now)).WriteResultsAsync(
            run.Id,
            [new CalculationOutput(
                document.DocumentId, "R-1",
                [new CalculationOutputValue(version.Id, null, "tons", 1m, unitId)],
                [])],
            CancellationToken.None);
        await db.SaveChangesAsync();

        return (document.DocumentId, document.PeriodKey);
    }

    private static async Task RecalculateAsync(
        TestDocumentBuilder builder, TestDocument document, IRecalculationCompletionHook hook, bool fail)
    {
        var clock = new TestClock(Now);
        await using var db = builder.CreateContext();
        var job = new RecalculationJob(
            db, new Runner(fail), RunHandler(), FormulaService(), clock, completionHook: hook);

        await job.ExecuteAsync(
            new RecalculationRequest(document.ProjectId, document.DocumentId, document.PeriodKey.Value, TriggeredByUserId: null),
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
