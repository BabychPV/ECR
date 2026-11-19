// tests/Ecr.Infrastructure.Tests/Jobs/SourceBindingGateCacheTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// B179 L2 perf («perf max», FormulaJobFixedCostTests 21 &gt; 20): ЗОНД звернень хука джерела з теплим кешем воріт —
/// задача формул НЕ-джерела не звертається до бази (0), документа-джерела — одне звернення (екземпляри оригіналу).
/// </summary>
/// <remarks>Детерміновано: лічильник команд контексту хука, без таймінгу; періоди 2099xx.</remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Directive", "B179")]
public sealed class SourceBindingGateCacheTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private const int Month = 10;

    /// <summary>Не-джерело — 0 звернень і нічого в черзі; джерело — рівно 1 звернення і одна батьківська задача.</summary>
    /// <remarks>
    /// Мутація: у хуку читати ворота з бази щоразу (<c>SourceBindingGate.LoadAsync</c> замість <c>gateCache.GetAsync</c>)
    /// — не-джерело дає ≥ 1 звернення, червоний.
    /// </remarks>
    [Fact]
    public async Task Теплі_ворота_не_джерело_0_звернень_джерело_1()
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, _) = await world.RegistryAsync();
        var source = await world.DocumentAsync([Month]);
        var stranger = await world.DocumentAsync([Month]);
        var bindingId = await world.BindingAsync(source.TemplateId, registryId);
        var cache = new SourceBindingGateCache();

        // Прогрів (у задачі — перший прогін процесу).
        await RunAsync(cache, stranger, new CommandRecorder());

        var strangerCommands = new CommandRecorder();
        await RunAsync(cache, stranger, strangerCommands);
        Assert.True(strangerCommands.Matching(string.Empty).Count == 0, strangerCommands.Format());
        Assert.Empty(await ParentRowsAsync(bindingId));

        var sourceCommands = new CommandRecorder();
        await RunAsync(cache, source, sourceCommands);
        Assert.True(sourceCommands.Matching(string.Empty).Count == 1, sourceCommands.Format());
        Assert.Single(await ParentRowsAsync(bindingId));
    }

    /// <summary>
    /// Версія новіша за знімок воріт (нова версія шаблону після прогріву) — знімок перечитується, а не мовчить.
    /// </summary>
    /// <remarks>Мутація: прибрати перечитування за <c>MaxVersionId</c> — джерело з новою версією мовчить, червоний.</remarks>
    [Fact]
    public async Task Нова_версія_шаблону_після_знімка_перечитує_ворота()
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, _) = await world.RegistryAsync();
        var stranger = await world.DocumentAsync([Month]);
        var cache = new SourceBindingGateCache();

        // Знімок до появи документа-джерела (і його версії), але вже з активною зв'язкою — на шаблон чужого документа.
        await world.BindingAsync(stranger.TemplateId, registryId);
        await RunAsync(cache, stranger, new CommandRecorder());
        var loads = cache.Loads;

        var source = await world.DocumentAsync([Month]);
        var bindingId = await world.BindingAsync(source.TemplateId, registryId);

        await RunAsync(cache, source, new CommandRecorder());

        Assert.Equal(loads + 1, cache.Loads);
        Assert.Single(await ParentRowsAsync(bindingId));
    }

    /// <summary>Без версій (шлях <see cref="RecalculationJob"/>) — як доти: шаблони документа читає сам хук.</summary>
    [Fact]
    public async Task Без_версій_хук_ставить_задачу_джерела_як_доти()
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, _) = await world.RegistryAsync();
        var source = await world.DocumentAsync([Month]);
        var bindingId = await world.BindingAsync(source.TemplateId, registryId);

        await using var host = NewHost();
        await new SourceDocumentChangeHook(host.Db, Scheduler(host), gateCache: new SourceBindingGateCache())
            .OnSucceededAsync(
                new Dictionary<long, int[]> { [source.DocumentId] = [SourceDependencyWorld.Period(Month)] },
                0,
                CancellationToken.None);

        Assert.Single(await ParentRowsAsync(bindingId));
    }

    private async Task RunAsync(SourceBindingGateCache cache, TestDocument document, CommandRecorder recorder)
    {
        await using var host = NewHost();
        await using var db = Sql.CreateContext(recorder);
        await new SourceDocumentChangeHook(db, Scheduler(host), gateCache: cache)
            .OnSucceededAsync(
                new ChangedPeriodsDictionary
                {
                    [document.DocumentId] = [SourceDependencyWorld.Period(Month)],
                    TemplateVersionIds = new HashSet<int> { document.TemplateVersionId },
                },
                0,
                CancellationToken.None);
    }

    private static DbBackgroundJobScheduler Scheduler(Host host)
        => new(
            host.Queue,
            new QuartzJobScheduler(null, new JobProgressStore(host.Db), new SystemClock()),
            new JobQueueSignal());

    private async Task<List<string>> ParentRowsAsync(int bindingId)
    {
        var prefix = $"{nameof(IRegistryImpactRecalculationJob)}~source-change-b{bindingId}-";
        await using var db = Sql.CreateContext();
        return await db.JobProgresses.AsNoTracking()
            .Where(p => p.TargetKey!.StartsWith(prefix))
            .Select(p => p.TargetKey!)
            .ToListAsync();
    }
}
