using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// D-424 (LP-45): stale-while-revalidate карти застарілості на TTL-складову. Протермінований (не більш ніж на
/// <see cref="StaleResultsMapCache.StaleWhileRevalidateWindow"/>) запис із ЗБІЖНИМИ епохою й відбитком віддається
/// негайно, оновлення - у фоні одним завантаженням. Тести рахують SQL-команди карти (інтерсептор), а не час.
/// </summary>
/// <remarks>
/// Мутації (detached-worktree): у <c>StaleResultsMapCache.TryStale</c> прибрати <c>hit.Epoch == epochValue</c> -
/// червоний <c>SWR_bump_епохи_...</c>; прибрати <c>hit.Fingerprint == fingerprint</c> - червоний
/// <c>SWR_зміна_відбитка_...</c>; у <c>StartBackgroundRefresh</c> прибрати перевірку <c>ReferenceEquals(...GetOrAdd)</c>
/// (single-flight) - червоний <c>SWR_N_паралельних_...</c>; прибрати верхню межу вікна - червоний
/// <c>SWR_протермінований_понад_вікно_...</c>.
/// </remarks>
public sealed partial class DocumentStaleResultsStoreTests
{
    private sealed class MovableClock(DateTime now) : Ecr.Domain.Abstractions.IClock
    {
        private long ticks = now.Ticks;

        public DateTime UtcNow => new(Interlocked.Read(ref ticks), DateTimeKind.Utc);

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    /// <summary>Рахує запити карти; може утримувати їх до <see cref="Release"/> і провалити наступний.</summary>
    private sealed class SwrProbe : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int maps;
        private int hold;
        private int failNext;

        public int Maps => Volatile.Read(ref maps);

        public void Hold()
        {
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref hold, 1);
        }

        public void Release()
        {
            Volatile.Write(ref hold, 0);
            release.TrySetResult();
        }

        public void FailNextMapQuery() => Volatile.Write(ref failNext, 1);

        public async Task WaitMapsAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (Maps < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"Запитів карти {Maps}, очікувалось {count}.");
                }

                await Task.Delay(10);
            }
        }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("MIN(t.At)", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref maps);
                if (Interlocked.Exchange(ref failNext, 0) == 1)
                {
                    throw new InvalidOperationException("SWR probe: збій фонового завантаження");
                }

                if (Volatile.Read(ref hold) == 1)
                {
                    // Не таймінг-асерт: запобіжник від зависання.
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
                }
            }

            return result;
        }
    }

    private sealed record SwrRig(MapRig R, MovableClock Clock, SwrProbe Probe, ServiceProvider Provider);

    private async Task<SwrRig> SwrRigAsync()
    {
        var r = await RigAsync();
        var probe = new SwrProbe();
        var services = new ServiceCollection();
        services.AddDbContext<EcrDbContext>(o => o.UseSqlServer(sql.ConnectionString).AddInterceptors(probe));
        return new SwrRig(r, new MovableClock(Now.AddHours(2)), probe, services.BuildServiceProvider());
    }

    private EcrDbContext SwrContext(SwrRig s) => InterceptedContext(s.Probe, s.R.Epoch);

    private static DocumentStore SwrStore(SwrRig s, EcrDbContext db)
        => new(db, s.R.Cache, s.Clock, s.R.Epoch, null, s.Provider.GetRequiredService<IServiceScopeFactory>());

    private async Task<HashSet<long>> SwrPageAsync(SwrRig s, IReadOnlyCollection<int>? visible = null)
    {
        await using var db = SwrContext(s);
        return await StaleOnPageAsync(SwrStore(s, db), s.R, visible);
    }

    private static int InFlightCount(IMemoryCache cache)
        => cache.TryGetValue("doc-stale-map:in-flight", out var found) && found is System.Collections.ICollection c ? c.Count : 0;

    private static async Task WaitIdleAsync(IMemoryCache cache)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (InFlightCount(cache) > 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Фонове завантаження не завершилось.");
            }

            await Task.Delay(10);
        }
    }

    private static DateTime EntryExpiresAt(SwrRig s)
    {
        Assert.True(s.R.Cache.TryGetValue($"doc-stale-map:{s.R.Period}:*", out var entry));
        return (DateTime)entry!.GetType().GetProperty("ExpiresAt")!.GetValue(entry)!;
    }

    private static readonly TimeSpan PastTtl = StaleResultsMapCache.Ttl + TimeSpan.FromSeconds(1);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task SWR_протермінований_віддається_без_синхронного_запиту_карти_і_запускає_один_фоновий_reload()
    {
        var s = await SwrRigAsync();
        using var _ = s.R.Cache;
        await using var __ = s.Provider;

        Assert.Equal([s.R.A, s.R.B], (await SwrPageAsync(s)).Order());
        Assert.Equal(1, s.Probe.Maps);

        s.Clock.Advance(PastTtl);
        s.Probe.Hold();

        // Фонова карта утримана: сторінка повертається, отже читач НЕ чекав запиту карти.
        var page = await SwrPageAsync(s).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal([s.R.A, s.R.B], page.Order());

        await s.Probe.WaitMapsAsync(2);
        Assert.Equal(1, InFlightCount(s.R.Cache));

        s.Probe.Release();
        await WaitIdleAsync(s.R.Cache);

        Assert.Equal(2, s.Probe.Maps);
        Assert.True(EntryExpiresAt(s) > s.Clock.UtcNow, "фонове завантаження має оновити запис");

        // Запис знову чинний: нових запитів карти немає.
        Assert.Equal([s.R.A, s.R.B], (await SwrPageAsync(s)).Order());
        Assert.Equal(2, s.Probe.Maps);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task SWR_bump_епохи_дає_синхронне_перезавантаження_власна_правка_видна_одразу()
    {
        var s = await SwrRigAsync();
        using var _ = s.R.Cache;
        await using var __ = s.Provider;

        Assert.Equal([s.R.A, s.R.B], (await SwrPageAsync(s)).Order());
        await EditAsync(s.R.Fresh, s.R.Period, s.R.Chain.ColumnDefIds[1], Now.AddHours(1));
        s.Clock.Advance(PastTtl);
        s.R.Epoch.Bump(s.R.Period);

        var page = await SwrPageAsync(s);

        Assert.Equal(new[] { s.R.A, s.R.B, s.R.Fresh }.Order(), page.Order());
        Assert.Equal(2, s.Probe.Maps);
        Assert.Equal(0, InFlightCount(s.R.Cache));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task SWR_зміна_відбитка_іншим_процесом_дає_синхронне_перезавантаження()
    {
        var s = await SwrRigAsync();
        using var _ = s.R.Cache;
        await using var __ = s.Provider;

        Assert.Equal([s.R.A, s.R.B], (await SwrPageAsync(s)).Order());
        s.Clock.Advance(PastTtl);

        // «Worker»: своя епоха; кеш читача про це знає лише з відбитка.
        var workerEpoch = new StaleCountsEpoch();
        await using (var worker = EpochContext(workerEpoch))
        {
            var run = await PendingRunAsync(worker, s.R.Chain, s.R.Version, Now.AddHours(2), [s.R.A]);
            await using var transaction = await worker.Database.BeginTransactionAsync();
            await new CalculationResultStore(worker, new TestClock(Now), workerEpoch)
                .SwitchCurrentRunAsync(run.Id, "{}", CancellationToken.None);
            await worker.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync();
        }

        var epochBefore = s.R.Epoch.ValueFor(s.R.Period);
        var page = await SwrPageAsync(s);

        Assert.Equal(epochBefore, s.R.Epoch.ValueFor(s.R.Period));
        Assert.Empty(page);
        Assert.Equal(2, s.Probe.Maps);
        Assert.Equal(0, InFlightCount(s.R.Cache));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task SWR_помилка_фону_не_кешується_і_не_отруює_наступний_читач_завантажує()
    {
        var s = await SwrRigAsync();
        using var _ = s.R.Cache;
        await using var __ = s.Provider;

        Assert.Equal([s.R.A, s.R.B], (await SwrPageAsync(s)).Order());
        var firstExpiry = EntryExpiresAt(s);
        s.Clock.Advance(PastTtl);
        s.Probe.FailNextMapQuery();

        // Читач збою не бачить: стара карта віддана.
        Assert.Equal([s.R.A, s.R.B], (await SwrPageAsync(s)).Order());
        await WaitIdleAsync(s.R.Cache);

        // Перелік робить ДВА звернення до карти (фільтр і рядки): якщо збійний фоновий запит завершився між ними,
        // друге звернення запускає ще одне оновлення. Тому лічимо відносно «після збою», а не за абсолютним числом.
        var afterFailure = s.Probe.Maps;
        Assert.InRange(afterFailure, 2, 3);
        if (afterFailure == 3)
        {
            // Друге звернення встигло запустити успішне оновлення: збій не отруїв, запис свіжий.
            Assert.True(EntryExpiresAt(s) > s.Clock.UtcNow);
            return;
        }

        Assert.Equal(firstExpiry, EntryExpiresAt(s));

        // Наступний читач запускає нове фонове завантаження, і воно вдається.
        Assert.Equal([s.R.A, s.R.B], (await SwrPageAsync(s)).Order());
        await s.Probe.WaitMapsAsync(afterFailure + 1);
        await WaitIdleAsync(s.R.Cache);
        Assert.Equal(afterFailure + 1, s.Probe.Maps);
        Assert.True(EntryExpiresAt(s) > s.Clock.UtcNow);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task SWR_N_паралельних_читачів_протермінованого_ключа_запускають_одне_фонове_завантаження()
    {
        const int readers = 6;
        var s = await SwrRigAsync();
        using var _ = s.R.Cache;
        await using var __ = s.Provider;

        Assert.Equal([s.R.A, s.R.B], (await SwrPageAsync(s)).Order());
        s.Clock.Advance(PastTtl);
        s.Probe.Hold();

        var pages = await Task.WhenAll(Enumerable.Range(0, readers).Select(_ => Task.Run(() => SwrPageAsync(s))))
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.All(pages, p => Assert.Equal([s.R.A, s.R.B], p.Order()));
        await s.Probe.WaitMapsAsync(2);
        Assert.Equal(1, InFlightCount(s.R.Cache));

        s.Probe.Release();
        await WaitIdleAsync(s.R.Cache);

        // Негативна перевірка: дублікати фону встигли б дійти до запиту; невелика пауза лише для них.
        await Task.Delay(300);
        Assert.Equal(2, s.Probe.Maps);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task SWR_протермінований_понад_вікно_завантажується_синхронно()
    {
        var s = await SwrRigAsync();
        using var _ = s.R.Cache;
        await using var __ = s.Provider;

        Assert.Equal([s.R.A, s.R.B], (await SwrPageAsync(s)).Order());
        await EditAsync(s.R.Fresh, s.R.Period, s.R.Chain.ColumnDefIds[1], Now.AddHours(1));
        s.Clock.Advance(StaleResultsMapCache.Ttl + StaleResultsMapCache.StaleWhileRevalidateWindow + TimeSpan.FromSeconds(1));

        var page = await SwrPageAsync(s);

        Assert.Equal(new[] { s.R.A, s.R.B, s.R.Fresh }.Order(), page.Order());
        Assert.Equal(2, s.Probe.Maps);
        Assert.Equal(0, InFlightCount(s.R.Cache));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task SWR_не_змінює_доступ_і_ключ_карти_чужі_документи_не_віддаються()
    {
        var s = await SwrRigAsync();
        using var _ = s.R.Cache;
        await using var __ = s.Provider;

        Assert.Equal([s.R.A, s.R.B], (await SwrPageAsync(s)).Order());
        s.Clock.Advance(PastTtl);
        s.Probe.Hold();

        // Стару карту читач бачить, але сторінка лишається в межах його проєктів.
        var hidden = await SwrPageAsync(s, [s.R.Chain.ProjectId + 1_000_000]).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(hidden);

        s.Probe.Release();
        await WaitIdleAsync(s.R.Cache);

        // Інший ключ (автор правки) - окрема карта, а не зріз «*».
        await using var db = SwrContext(s);
        var byUser = await SwrStore(s, db).ListAsync(
            projectId: null, new PeriodKeyFilter(s.R.Period),
            new DocumentListFilter(null, null, ResultsStale: true, StaleByUserId: 62),
            new CursorRequest(Limit: 200), [s.R.Chain.ProjectId], CancellationToken.None);
        Assert.Equal([s.R.B], byUser.Items.Select(d => d.Id));
    }
}
