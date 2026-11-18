// tests/Ecr.Infrastructure.Tests/Jobs/Hse400SourceApprovalTriggerTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// B80 (N2a, RW.2): за зв'язкою <see cref="DocumentSourceState.ApprovedOnly"/> злиту задачу року ставлять остаточне
/// затвердження, повернення із затвердженого і видалення документа HSE400 (<see cref="Hse400SourceApprovalTrigger"/>),
/// а хук завершення перерахунку (<see cref="Hse400LandDependentsTrigger"/>) мовчить; за <c>Any</c> — навпаки, як доти.
/// </summary>
/// <remarks>Справжня черга в базі; періоди 2099xx.</remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Directive", "B80")]
public sealed class Hse400SourceApprovalTriggerTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private const int Year = 2099;
    private const int PeriodKeyValue = (Year * 100) + 3;
    private const int ActorId = 42;

    /// <summary>
    /// Пара в ОДНОМУ світі (слово координатора 18.11): за <c>Any</c> перерахунок HSE400 ставить, а затвердження — ні;
    /// за <c>ApprovedOnly</c> — навпаки.
    /// </summary>
    /// <remarks>
    /// Мутації: у хуку завершення прибрати перевірку <c>SourceState == ApprovedOnly</c> — рядок ApprovedOnly
    /// червоний; у тригері прибрати <c>binding.SourceState != ApprovedOnly</c> — рядок Any червоний.
    /// </remarks>
    [Theory]
    [InlineData(DocumentSourceState.Any, 1, 0)]
    [InlineData(DocumentSourceState.ApprovedOnly, 0, 1)]
    public async Task Перерахунок_і_затвердження_ставлять_залежно_від_стану_зв_язки(
        DocumentSourceState state, int byRecalculation, int byApproval)
    {
        var world = await ArrangeAsync(state);

        var recalculationJobs = Substitute.For<IBackgroundJobScheduler>();
        var approvalJobs = Substitute.For<IBackgroundJobScheduler>();
        await using (var db = Sql.CreateContext())
        {
            await new Hse400LandDependentsTrigger(
                    db, recalculationJobs, enabled: null, registryCode: world.RegistryCode, templateCode: world.TemplateCode,
                    readOverride: null)
                .OnSucceededAsync(new Dictionary<long, int[]> { [world.DocumentId] = [PeriodKeyValue] }, CancellationToken.None);
            await Trigger(db, approvalJobs, world).OnSourceSheetApprovalChangedAsync(
                world.DocumentId, world.SheetDefId, PeriodKeyValue, SourceApprovalChange.Approved, ActorId, CancellationToken.None);
        }

        Assert.Equal(byRecalculation, Enqueued(recalculationJobs));
        Assert.Equal(byApproval, Enqueued(approvalJobs));
    }

    [Fact]
    public async Task ApprovedOnly_затвердження_і_повернення_ставлять_одну_злиту_задачу_року_з_автором()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);

        await using var host = NewHost();
        var trigger = Trigger(host.Db, Scheduler(host), world);
        await trigger.OnSourceSheetApprovalChangedAsync(
            world.DocumentId, world.SheetDefId, PeriodKeyValue, SourceApprovalChange.Approved, ActorId, CancellationToken.None);

        var row = Assert.Single(await RowsAsync(world.RegistryId));
        Assert.Equal("Queued", row.State);
        Assert.Equal(ActorId, row.CreatedByUserId);
        Assert.Contains($"\"year\":{Year}", row.Payload, StringComparison.Ordinal);
        Assert.Contains($"HSE400 {PeriodKeyValue} approved", row.Payload, StringComparison.Ordinal);

        // Повернення із затвердженого, поки задача чекає, — та сама задача (злиття).
        await trigger.OnSourceSheetApprovalChangedAsync(
            world.DocumentId, world.SheetDefId, PeriodKeyValue, SourceApprovalChange.Reopened, ActorId, CancellationToken.None);
        Assert.Equal(row.JobId, Assert.Single(await RowsAsync(world.RegistryId)).JobId);
    }

    [Fact]
    public async Task ApprovedOnly_повернення_ставить_задачу_року()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);

        await using var host = NewHost();
        await Trigger(host.Db, Scheduler(host), world).OnSourceSheetApprovalChangedAsync(
            world.DocumentId, world.SheetDefId, PeriodKeyValue, SourceApprovalChange.Reopened, ActorId, CancellationToken.None);

        var row = Assert.Single(await RowsAsync(world.RegistryId));
        Assert.Contains($"HSE400 {PeriodKeyValue} reopened", row.Payload, StringComparison.Ordinal);
    }

    /// <summary>Мутація: <c>PrepareDocumentDeletionAsync</c> повертає <c>null</c> завжди — червоний.</summary>
    [Fact]
    public async Task ApprovedOnly_видалення_документа_ставить_задачу_його_років_а_за_Any_нічого()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);

        await using (var host = NewHost())
        {
            var trigger = Trigger(host.Db, Scheduler(host), world);
            var deletion = await trigger.PrepareDocumentDeletionAsync(world.DocumentId, CancellationToken.None);
            Assert.NotNull(deletion);
            Assert.Equal([Year], deletion.Years);

            await trigger.OnSourceDocumentDeletedAsync(deletion, ActorId, CancellationToken.None);
        }

        var row = Assert.Single(await RowsAsync(world.RegistryId));
        Assert.Contains("HSE400 document deleted", row.Payload, StringComparison.Ordinal);

        await SetStateAsync(world, DocumentSourceState.Any);
        await using var db = Sql.CreateContext();
        Assert.Null(await Trigger(db, Substitute.For<IBackgroundJobScheduler>(), world)
            .PrepareDocumentDeletionAsync(world.DocumentId, CancellationToken.None));
    }

    /// <summary>Біт <c>RecalculateDependents</c> 0 — нічого з трьох; явний ключ <c>true</c> перекриває біт (як у хуку).</summary>
    /// <remarks>Мутація: <c>enabled ?? binding.RecalculateDependents</c> → <c>enabled ?? true</c> — червоний.</remarks>
    [Fact]
    public async Task Біт_перераховувати_вимкнено_нічого_не_ставиться()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);
        await using (var db = Sql.CreateContext())
        {
            await db.DocumentSourceBindings.Where(b => b.RegistryDefId == world.RegistryId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.RecalculateDependents, false));
        }

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        await using (var db = Sql.CreateContext())
        {
            var trigger = Trigger(db, jobs, world);
            await trigger.OnSourceSheetApprovalChangedAsync(
                world.DocumentId, world.SheetDefId, PeriodKeyValue, SourceApprovalChange.Approved, ActorId, CancellationToken.None);
            await trigger.OnSourceSheetApprovalChangedAsync(
                world.DocumentId, world.SheetDefId, PeriodKeyValue, SourceApprovalChange.Reopened, ActorId, CancellationToken.None);
            Assert.Null(await trigger.PrepareDocumentDeletionAsync(world.DocumentId, CancellationToken.None));
        }

        Assert.Equal(0, Enqueued(jobs));

        await using (var db = Sql.CreateContext())
        {
            await new Hse400SourceApprovalTrigger(
                    db, jobs, enabled: true, readOverride: null, registryCode: world.RegistryCode, templateCode: world.TemplateCode)
                .OnSourceSheetApprovalChangedAsync(
                    world.DocumentId, world.SheetDefId, PeriodKeyValue, SourceApprovalChange.Approved, ActorId, CancellationToken.None);
        }

        Assert.Equal(1, Enqueued(jobs));
    }

    [Fact]
    public async Task Аркуш_іншого_шаблону_нічого_не_ставить()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);
        var other = await new TestDocumentBuilder(Sql.ConnectionString).BuildAsync(periodKey: PeriodKeyValue);

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        await using var db = Sql.CreateContext();
        await Trigger(db, jobs, world).OnSourceSheetApprovalChangedAsync(
            other.DocumentId, other.SheetDefId, PeriodKeyValue, SourceApprovalChange.Approved, ActorId, CancellationToken.None);

        Assert.Equal(0, Enqueued(jobs));
    }

    private static Hse400SourceApprovalTrigger Trigger(EcrDbContext db, IBackgroundJobScheduler jobs, World world)
        => new(db, jobs, enabled: null, readOverride: null, registryCode: world.RegistryCode, templateCode: world.TemplateCode);

    private static int Enqueued(IBackgroundJobScheduler jobs)
        => jobs.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IBackgroundJobScheduler.EnqueueCoalescedAsync));

    private static DbBackgroundJobScheduler Scheduler(Host host)
        => new(
            host.Queue,
            new QuartzJobScheduler(null, new JobProgressStore(host.Db), new SystemClock()),
            new JobQueueSignal());

    private async Task<List<Ecr.Domain.Entities.Integration.JobProgress>> RowsAsync(int registryId)
    {
        var target = $"{nameof(IRegistryImpactRecalculationJob)}~{RegistryImpactRecalculationTarget.ForYear(registryId, Year)}";
        await using var db = Sql.CreateContext();
        return await db.JobProgresses.AsNoTracking().Where(p => p.TargetKey == target).ToListAsync();
    }

    /// <summary>Документ, чий шаблон грає роль HSE400, власний довідник і зв'язка в заданому стані.</summary>
    private async Task<World> ArrangeAsync(DocumentSourceState state)
    {
        var document = await new TestDocumentBuilder(Sql.ConnectionString).BuildAsync(periodKey: PeriodKeyValue);
        await using var db = Sql.CreateContext();
        var registry = new RegistryDef(
            EcrCode.Create($"HS{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Composition" }),
            isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var templateCode = await db.Templates.Where(t => t.Id == document.TemplateId).Select(t => t.Code).SingleAsync();
        db.DocumentSourceBindings.Add(new DocumentSourceBinding($"B80N2_{registry.Code}", document.TemplateId, registry.Id));
        await db.SaveChangesAsync();

        var world = new World(document.DocumentId, document.SheetDefId, registry.Id, registry.Code, templateCode);
        await SetStateAsync(world, state);
        return world;
    }

    private async Task SetStateAsync(World world, DocumentSourceState state)
    {
        await using var db = Sql.CreateContext();
        await db.DocumentSourceBindings.Where(b => b.RegistryDefId == world.RegistryId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.SourceState, state));
    }

    private sealed record World(long DocumentId, int SheetDefId, int RegistryId, string RegistryCode, string TemplateCode);
}
