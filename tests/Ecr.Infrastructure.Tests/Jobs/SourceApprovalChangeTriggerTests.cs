// tests/Ecr.Infrastructure.Tests/Jobs/SourceApprovalChangeTriggerTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// B80 (N2a) → ✎ B179 L2: за зв'язкою <see cref="DocumentSourceState.ApprovedOnly"/> злиту задачу (зв'язка × рік × місяць)
/// ставлять остаточне затвердження, повернення із затвердженого і видалення документа-джерела будь-якої зв'язки
/// (<see cref="SourceApprovalChangeTrigger"/>), а хук завершення перерахунку (<see cref="SourceDocumentChangeHook"/>)
/// мовчить; за <c>Any</c> — навпаки. Замінює <c>Hse400SourceApprovalTriggerTests</c> (коди HSE400, ціль «довідник × рік»).
/// </summary>
/// <remarks>Справжня черга в базі; періоди 2099xx.</remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Directive", "B179")]
public sealed class SourceApprovalChangeTriggerTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private const int Month = 3;
    private const int ActorId = 42;

    /// <summary>
    /// Пара в ОДНОМУ світі: за <c>Any</c> перерахунок джерела ставить, а затвердження — ні; за <c>ApprovedOnly</c> — навпаки.
    /// </summary>
    /// <remarks>
    /// Мутації: хук активний і за ApprovedOnly (прибрати фільтр стану <c>Any</c>) — рядок ApprovedOnly червоний; у тригері
    /// прибрати фільтр <c>ApprovedOnly</c> — рядок Any червоний.
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
            await new SourceDocumentChangeHook(db, recalculationJobs)
                .OnSucceededAsync(
                    new Dictionary<long, int[]> { [world.Source.DocumentId] = [SourceDependencyWorld.Period(Month)] }, 0,
                    CancellationToken.None);
            await new SourceApprovalChangeTrigger(db, approvalJobs).OnSourceSheetApprovalChangedAsync(
                world.Source.DocumentId, world.Source.SheetDefId, SourceDependencyWorld.Period(Month),
                SourceApprovalChange.Approved, ActorId, CancellationToken.None);
        }

        Assert.Equal(byRecalculation, Enqueued(recalculationJobs));
        Assert.Equal(byApproval, Enqueued(approvalJobs));
    }

    [Fact]
    public async Task ApprovedOnly_затвердження_і_повернення_ставлять_одну_злиту_задачу_місяця_з_автором()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);

        await using var host = NewHost();
        var trigger = new SourceApprovalChangeTrigger(host.Db, Scheduler(host));
        await trigger.OnSourceSheetApprovalChangedAsync(
            world.Source.DocumentId, world.Source.SheetDefId, SourceDependencyWorld.Period(Month),
            SourceApprovalChange.Approved, ActorId, CancellationToken.None);

        var row = Assert.Single(await RowsAsync(world.BindingId));
        Assert.Equal(TargetKey(world.BindingId, Month), row.TargetKey);
        Assert.Equal("Queued", row.State);
        Assert.Equal(ActorId, row.CreatedByUserId);
        Assert.Contains($"\"bindingId\":{world.BindingId}", row.Payload, StringComparison.Ordinal);
        Assert.Contains($"\"month\":{Month}", row.Payload, StringComparison.Ordinal);
        Assert.Contains("approved", row.Payload, StringComparison.Ordinal);

        // Повернення із затвердженого, поки задача чекає, — та сама задача (злиття).
        await trigger.OnSourceSheetApprovalChangedAsync(
            world.Source.DocumentId, world.Source.SheetDefId, SourceDependencyWorld.Period(Month),
            SourceApprovalChange.Reopened, ActorId, CancellationToken.None);
        Assert.Equal(row.JobId, Assert.Single(await RowsAsync(world.BindingId)).JobId);
    }

    /// <summary>Мутація: <c>PrepareDocumentDeletionAsync</c> повертає <c>null</c> завжди — червоний.</summary>
    [Fact]
    public async Task ApprovedOnly_видалення_документа_ставить_задачу_його_років_а_за_Any_нічого()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);

        await using (var host = NewHost())
        {
            var trigger = new SourceApprovalChangeTrigger(host.Db, Scheduler(host));
            var deletion = await trigger.PrepareDocumentDeletionAsync(world.Source.DocumentId, CancellationToken.None);
            Assert.NotNull(deletion);
            Assert.Equal([SourceDependencyWorld.Year], deletion.Years);
            Assert.Equal([world.BindingId], deletion.BindingIds);

            await trigger.OnSourceDocumentDeletedAsync(deletion, ActorId, CancellationToken.None);
        }

        var row = Assert.Single(await RowsAsync(world.BindingId));
        Assert.Equal(TargetKey(world.BindingId, null), row.TargetKey);

        await SetStateAsync(world.BindingId, DocumentSourceState.Any);
        await using var db = Sql.CreateContext();
        Assert.Null(await new SourceApprovalChangeTrigger(db, Substitute.For<IBackgroundJobScheduler>())
            .PrepareDocumentDeletionAsync(world.Source.DocumentId, CancellationToken.None));
    }

    /// <summary>
    /// B181(2) H3: екземпляри таблиць КОПІЇ аркуша джерелом для залежних не є (завантажувач складу читає
    /// <c>SheetCopyId = null</c>), тож видалення документа, що має лише копію аркуша шаблону-джерела, перерахунок не ставить.
    /// </summary>
    /// <remarks>Мутація: прибрати <c>&amp;&amp; i.SheetCopyId == null</c> у <c>PrepareDocumentDeletionAsync</c> — червоний.</remarks>
    [Fact]
    public async Task ApprovedOnly_видалення_документа_лише_з_копією_аркуша_джерела_нічого_не_готує()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);

        await using (var db = Sql.CreateContext())
        {
            var original = new Ecr.Domain.Entities.Documents.DocumentSheet(world.Source.DocumentId, world.Source.SheetDefId);
            db.DocumentSheets.Add(original);
            await db.SaveChangesAsync();
            var copy = original.CreateCopy(2);
            db.DocumentSheets.Add(copy);
            await db.SaveChangesAsync();

            // Усі екземпляри документа — екземпляри копії: оригінального аркуша з даними немає.
            await db.TableInstances.Where(i => i.DocumentId == world.Source.DocumentId)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.SheetCopyId, (long?)copy.Id));
        }

        await using var check = Sql.CreateContext();
        Assert.Null(await new SourceApprovalChangeTrigger(check, Substitute.For<IBackgroundJobScheduler>())
            .PrepareDocumentDeletionAsync(world.Source.DocumentId, CancellationToken.None));
    }

    /// <summary>Аварійний вимикач — біт <c>RecalculateDependents</c>: 0 — нічого з трьох дій.</summary>
    /// <remarks>Мутація: ігнорувати біт (прибрати <c>&amp;&amp; b.RecalculateDependents</c>) — червоний.</remarks>
    [Fact]
    public async Task Біт_перераховувати_вимкнено_нічого_не_ставиться()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);
        await new SourceDependencyWorld(Sql).SetRecalculateDependentsAsync(world.BindingId, false);

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        await using var db = Sql.CreateContext();
        var trigger = new SourceApprovalChangeTrigger(db, jobs);
        await trigger.OnSourceSheetApprovalChangedAsync(
            world.Source.DocumentId, world.Source.SheetDefId, SourceDependencyWorld.Period(Month),
            SourceApprovalChange.Approved, ActorId, CancellationToken.None);
        await trigger.OnSourceSheetApprovalChangedAsync(
            world.Source.DocumentId, world.Source.SheetDefId, SourceDependencyWorld.Period(Month),
            SourceApprovalChange.Reopened, ActorId, CancellationToken.None);
        Assert.Null(await trigger.PrepareDocumentDeletionAsync(world.Source.DocumentId, CancellationToken.None));

        Assert.Equal(0, Enqueued(jobs));
    }

    [Fact]
    public async Task Аркуш_іншого_шаблону_нічого_не_ставить()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);
        var other = await new SourceDependencyWorld(Sql).DocumentAsync([Month]);

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        await using var db = Sql.CreateContext();
        await new SourceApprovalChangeTrigger(db, jobs).OnSourceSheetApprovalChangedAsync(
            other.DocumentId, other.SheetDefId, SourceDependencyWorld.Period(Month),
            SourceApprovalChange.Approved, ActorId, CancellationToken.None);

        Assert.Equal(0, Enqueued(jobs));
    }

    private static int Enqueued(IBackgroundJobScheduler jobs)
        => jobs.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IBackgroundJobScheduler.EnqueueCoalescedAsync));

    private static DbBackgroundJobScheduler Scheduler(Host host)
        => new(
            host.Queue,
            new QuartzJobScheduler(null, new JobProgressStore(host.Db), new SystemClock()),
            new JobQueueSignal());

    private static string TargetKey(int bindingId, int? month)
        => $"{nameof(IRegistryImpactRecalculationJob)}~{RegistryImpactRecalculationTarget.ForSourceChange(bindingId, SourceDependencyWorld.Year, month)}";

    private async Task<List<JobProgress>> RowsAsync(int bindingId)
    {
        var prefix = $"{nameof(IRegistryImpactRecalculationJob)}~source-change-b{bindingId}-";
        await using var db = Sql.CreateContext();
        return await db.JobProgresses.AsNoTracking().Where(p => p.TargetKey!.StartsWith(prefix)).ToListAsync();
    }

    private async Task<(TestDocument Source, int BindingId)> ArrangeAsync(DocumentSourceState state)
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, _) = await world.RegistryAsync();
        var source = await world.DocumentAsync([Month]);
        return (source, await world.BindingAsync(source.TemplateId, registryId, state));
    }

    private async Task SetStateAsync(int bindingId, DocumentSourceState state)
    {
        await using var db = Sql.CreateContext();
        await db.DocumentSourceBindings.Where(b => b.Id == bindingId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.SourceState, state));
    }
}
