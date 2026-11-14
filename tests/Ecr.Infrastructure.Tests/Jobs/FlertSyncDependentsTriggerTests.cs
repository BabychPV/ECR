// tests/Ecr.Infrastructure.Tests/Jobs/FlertSyncDependentsTriggerTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// FLERT F6 (Q6): синк FLERT змінив рядки дзеркала — одна злита задача перерахунку залежних на змінений довідник × рік
/// (<see cref="FlertSyncDependentsTrigger"/>), на справжній черзі в базі.
/// </summary>
/// <remarks>
/// ⛔ Доказ: прибрати з <c>AffectedYearsAsync</c> умову <c>Open</c>/<c>Grace</c> — червоний «закритий рік»; повернути
/// <c>enabled</c>-перевірку — червоний «вимкнено»; поставити задачу на кожен період, а не на рік, — червоний «одна на рік».
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Directive", "D-476")]
public sealed class FlertSyncDependentsTriggerTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private static readonly DateTime Now = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("yes", false)]
    [InlineData("true", true)]
    public void Увімкнено_лише_явним_true(string? value, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [FlertSyncDependentsTrigger.EnabledKey] = value })
            .Build();

        Assert.Equal(expected, FlertSyncDependentsTrigger.IsEnabled(configuration));
    }

    [Fact]
    public async Task Ключ_вимкнено_нічого_не_ставиться()
    {
        var (registryId, codes) = await NewMirrorAsync();
        await ArrangeDependentAsync(registryId, PeriodState.Open, 202601);
        var jobs = Substitute.For<IBackgroundJobScheduler>();

        await using var db = Sql.CreateContext();
        var queued = await new FlertSyncDependentsTrigger(db, jobs, enabled: false)
            .OnSyncedAsync(Changed("compositions"), codes, CancellationToken.None);

        Assert.Equal(0, queued);
        await jobs.DidNotReceiveWithAnyArgs()
            .EnqueueCoalescedAsync<IRegistryImpactRecalculationJob>(default!, default, default, default);
    }

    [Fact]
    public async Task Синк_без_змін_нічого_не_ставить()
    {
        var (registryId, codes) = await NewMirrorAsync();
        await ArrangeDependentAsync(registryId, PeriodState.Open, 202601);
        var jobs = Substitute.For<IBackgroundJobScheduler>();

        var unchanged = new FlertSyncCounts();
        unchanged.Unchanged["compositions"] = 40;
        var outcome = new FlertSyncOutcome(FlertCompositionSyncJob.Succeeded, null, 40, unchanged, [], [], []);

        await using var db = Sql.CreateContext();
        Assert.Equal(0, await new FlertSyncDependentsTrigger(db, jobs, enabled: true).OnSyncedAsync(outcome, codes, CancellationToken.None));
        Assert.Equal(0, await new FlertSyncDependentsTrigger(db, jobs, enabled: true)
            .OnSyncedAsync(FlertSyncOutcome.Disabled(), codes, CancellationToken.None));
        await jobs.DidNotReceiveWithAnyArgs()
            .EnqueueCoalescedAsync<IRegistryImpactRecalculationJob>(default!, default, default, default);
    }

    [Fact]
    public async Task Одна_злита_задача_на_рік_закритий_рік_не_чіпається()
    {
        var (registryId, codes) = await NewMirrorAsync();
        await ArrangeDependentAsync(registryId, PeriodState.Open, 202601);
        await ArrangeDependentAsync(registryId, PeriodState.Open, 202603);
        await ArrangeDependentAsync(registryId, PeriodState.Open, 202602);
        await ArrangeDependentAsync(registryId, PeriodState.Closed, 202501);

        await using var host = NewHost();
        var trigger = new FlertSyncDependentsTrigger(host.Db, Scheduler(host), enabled: true);

        Assert.Equal(1, await trigger.OnSyncedAsync(Changed("compositions"), codes, CancellationToken.None));

        var row = Assert.Single(await RowsAsync(registryId, 2026));
        Assert.Equal("Queued", row.State);
        Assert.Contains("\"year\":2026", row.Payload, StringComparison.Ordinal);
        Assert.Contains($"\"registryDefId\":{registryId}", row.Payload, StringComparison.Ordinal);
        Assert.Empty(await RowsAsync(registryId, 2025));

        // Другий синк, поки задача чекає, — та сама задача (злиття, не витіснення).
        await trigger.OnSyncedAsync(Changed("compositions"), codes, CancellationToken.None);
        var again = Assert.Single(await RowsAsync(registryId, 2026));
        Assert.Equal(row.JobId, again.JobId);
    }

    [Fact]
    public async Task Змінено_лише_інший_довідник_дзеркала_цей_не_ставиться()
    {
        var (registryId, codes) = await NewMirrorAsync();
        await ArrangeDependentAsync(registryId, PeriodState.Open, 202601);
        var jobs = Substitute.For<IBackgroundJobScheduler>();

        await using var db = Sql.CreateContext();
        await new FlertSyncDependentsTrigger(db, jobs, enabled: true)
            .OnSyncedAsync(Changed("streams"), codes, CancellationToken.None);

        await jobs.DidNotReceiveWithAnyArgs()
            .EnqueueCoalescedAsync<IRegistryImpactRecalculationJob>(default!, default, default, default);
    }

    /// <summary>
    /// ⛔ B63 / B97 (слово людини 07.11: «тільки погоджені не мають перераховуватися»): увесь шлях FLERT F6 — синк змінив
    /// склад → <see cref="FlertSyncDependentsTrigger"/> ставить задачу року → <see cref="RegistryImpactRecalculationJob"/>
    /// → <c>CalculationTrigger.RequestDependentsAsync</c> — ставить перерахунок чернетки й ПОДАНОГО залежного (з позначкою
    /// поданих), а затвердженого — ні.
    /// </summary>
    /// <remarks>
    /// ⛔ ДОКАЗ (локально): у <c>RegistryImpactRecalculationJob</c> <c>RequestDependentsAsync</c> → <c>RequestAsync</c> без
    /// позначки (як до B63) — поданий без <c>recalculateSubmitted</c>/не поставлений, червоний. Періоди — 2099xx (retro 375).
    /// </remarks>
    [Fact]
    [Trait("Finding", "B97")]
    public async Task Синк_ставить_перерахунок_чернетки_й_поданого_залежного_з_позначкою_а_затвердженого_ні()
    {
        const int periodKey = 209901;
        var (registryId, codes) = await NewMirrorAsync();
        var draft = await ArrangeDependentAsync(registryId, PeriodState.Open, periodKey);
        var submitted = await ArrangeDependentAsync(registryId, PeriodState.Open, periodKey, DocumentStatus.Submitted);
        var approved = await ArrangeDependentAsync(registryId, PeriodState.Open, periodKey, DocumentStatus.Approved);

        // Крок 1: синк FLERT — одна задача року на змінений довідник.
        var impactJobs = Substitute.For<IBackgroundJobScheduler>();
        object? impactPayload = null;
        impactJobs.EnqueueCoalescedAsync<IRegistryImpactRecalculationJob>(
                Arg.Any<string>(), Arg.Do<object?>(p => impactPayload = p), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("impact-1");

        await using (var db = Sql.CreateContext())
        {
            Assert.Equal(1, await new FlertSyncDependentsTrigger(db, impactJobs, enabled: true)
                .OnSyncedAsync(Changed("compositions"), codes, CancellationToken.None));
        }

        Assert.NotNull(impactPayload);

        // Крок 2: задача року розкладає на залежні через справжній тригер і справжні сховища.
        var recalcJobs = Substitute.For<IBackgroundJobScheduler>();
        var payloads = new Dictionary<string, string>(StringComparer.Ordinal);
        recalcJobs.EnqueueCoalescedAsync<IRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(call =>
            {
                payloads[call.ArgAt<string>(0)] = System.Text.Json.JsonSerializer.Serialize(
                    call.ArgAt<object?>(1), System.Text.Json.JsonSerializerOptions.Web);
                return "recalc-1";
            });

        await using (var db = Sql.CreateContext())
        {
            var trigger = new CalculationTrigger(
                recalcJobs, new PeriodStore(db), new WorkflowStore(db), new DocumentStore(db), new DocumentSheetCopyStore(db));
            await new RegistryImpactRecalculationJob(new RegistryImpactStore(db), trigger)
                .ExecuteAsync(impactPayload, Substitute.For<IJobProgress>(), CancellationToken.None);
        }

        Assert.Equal(
            new[]
            {
                CalculationTrigger.DependentsTargetOf(draft.DocumentId, draft.PeriodKey),
                CalculationTrigger.DependentsTargetOf(submitted.DocumentId, submitted.PeriodKey),
            }.Order(StringComparer.Ordinal),
            payloads.Keys.Order(StringComparer.Ordinal));
        Assert.All(payloads.Values, json => Assert.Contains("\"recalculateSubmitted\":true", json, StringComparison.Ordinal));
        Assert.DoesNotContain(CalculationTrigger.DependentsTargetOf(approved.DocumentId, approved.PeriodKey), payloads.Keys);
        await recalcJobs.DidNotReceiveWithAnyArgs().EnqueueExclusiveAsync<IRecalculationJob>(default!, default, default, default);
    }

    private static FlertSyncOutcome Changed(string step)
    {
        var counts = new FlertSyncCounts();
        counts.Updated[step] = 3;
        return new FlertSyncOutcome(FlertCompositionSyncJob.Succeeded, null, 3, counts, [], [], []);
    }

    private static DbBackgroundJobScheduler Scheduler(Host host)
        => new(
            host.Queue,
            new QuartzJobScheduler(null, new JobProgressStore(host.Db), new SystemClock()),
            new JobQueueSignal());

    private async Task<List<Ecr.Domain.Entities.Integration.JobProgress>> RowsAsync(int registryId, int year)
    {
        var target = $"{nameof(IRegistryImpactRecalculationJob)}~{RegistryImpactRecalculationTarget.ForYear(registryId, year)}";
        await using var db = Sql.CreateContext();
        return await db.JobProgresses.AsNoTracking().Where(p => p.TargetKey == target).ToListAsync();
    }

    /// <summary>Довідник у ролі <c>GAS_COMPOSITION</c> з унікальним кодом (спільна тестова база).</summary>
    private async Task<(int Id, FlertRegistryCodes Codes)> NewMirrorAsync()
    {
        await using var db = Sql.CreateContext();
        var registry = new RegistryDef(
            EcrCode.Create($"FG{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Composition" }),
            isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        return (registry.Id, new FlertRegistryCodes($"FS{suffix}", $"FC{suffix}", registry.Code, $"FK{suffix}"));
    }

    /// <summary>Документ із прогоном <c>Current</c>, чия методологія читає довідник, у періоді заданого стану.</summary>
    private async Task<(long DocumentId, PeriodKey PeriodKey)> ArrangeDependentAsync(
        int registryId, PeriodState state, int periodKey, DocumentStatus? sheet = null)
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
            EcrCode.Create($"FM{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"),
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
}
