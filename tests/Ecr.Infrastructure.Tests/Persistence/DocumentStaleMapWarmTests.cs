using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// D-430 (LP-45): прогрів карти застарілості (<see cref="StaleMapWarmer"/>) і кеш відбитка довідників 60 с. Тести
/// рахують SQL-команди (інтерсептор), а не час; годинник рухається вручну.
/// </summary>
/// <remarks>
/// Мутації (detached-worktree): у <c>StaleMapWarmer.WarmOnceAsync</c> прибрати виклик <c>WarmAsync</c> - червоні
/// <c>Прогрів_...</c>; у <c>StaleResultsMapCache.RegistryFresh</c> прибрати перевірку віку (<c>LoadedAt</c>) -
/// червоний <c>Відбиток_довідників_...після_TTL</c>; прибрати перевірку <c>e.Epoch == epochNow</c> - червоні
/// <c>Відбиток_довідників_...епохи</c> і <c>...власна_правка_довідника</c>; прибрати
/// <c>StaleCountsEpoch.Find(db)?.Invalidate(db)</c> в <c>UnitOfWork</c> - червоний <c>...власна_правка_довідника</c>.
/// </remarks>
public sealed partial class DocumentStaleResultsStoreTests
{
    private const string RegistryFingerprintMarker = "CHECKSUM(Id, DataChangedAt)";

    /// <summary>Рахує запити карти й запити відбитка довідників; може провалювати запит відбитка.</summary>
    private sealed class WarmProbe : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private int maps;
        private int registryFingerprints;
        private int failMap;
        private int failRegistry;

        public int Maps => Volatile.Read(ref maps);

        public int RegistryFingerprints => Volatile.Read(ref registryFingerprints);

        public void FailNextMapQuery() => Volatile.Write(ref failMap, 1);

        public void FailRegistryQueries(bool on) => Volatile.Write(ref failRegistry, on ? 1 : 0);

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("MIN(t.At)", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref maps);
                if (Interlocked.Exchange(ref failMap, 0) == 1)
                {
                    throw new InvalidOperationException("Warm probe: збій запиту карти");
                }
            }
            else if (command.CommandText.Contains(RegistryFingerprintMarker, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref registryFingerprints);
                if (Volatile.Read(ref failRegistry) == 1)
                {
                    throw new InvalidOperationException("Warm probe: збій відбитка довідників");
                }
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed record WarmRig(MapRig R, MovableClock Clock, WarmProbe Probe, ServiceProvider Provider, StaleMapWarmer Warmer);

    private async Task<WarmRig> WarmRigAsync()
    {
        var r = await RigAsync();
        var probe = new WarmProbe();
        var clock = new MovableClock(Now.AddHours(2));
        var services = new ServiceCollection();
        services.AddDbContext<EcrDbContext>(o => o.UseSqlServer(sql.ConnectionString).AddInterceptors(probe));
        var provider = services.BuildServiceProvider();
        var warmer = new StaleMapWarmer(
            provider.GetRequiredService<IServiceScopeFactory>(), r.Cache, clock, r.Epoch, NullLogger<StaleMapWarmer>.Instance);
        return new WarmRig(r, clock, probe, provider, warmer);
    }

    private static DocumentStore WarmStore(WarmRig w, EcrDbContext db) => new(db, w.R.Cache, w.Clock, w.R.Epoch);

    private async Task<HashSet<long>> WarmPageAsync(WarmRig w)
    {
        await using var db = InterceptedContext(w.Probe, w.R.Epoch);
        return await StaleOnPageAsync(WarmStore(w, db), w.R);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прогрів_завантажує_карту_рівно_раз_і_перелік_не_робить_синхронного_запиту_карти()
    {
        var w = await WarmRigAsync();
        using var cacheLease = w.R.Cache;
        await using var providerLease = w.Provider;

        var warmed = await w.Warmer.WarmOnceAsync(CancellationToken.None);

        Assert.Contains(w.R.Period, warmed);
        Assert.Equal(warmed.Count, w.Probe.Maps);

        // Читач: ключ «*» уже в кеші, а епоха й відбиток збігаються - жодного запиту карти.
        Assert.Equal([w.R.A, w.R.B], (await WarmPageAsync(w)).Order());
        Assert.Equal(warmed.Count, w.Probe.Maps);

        // Другий прохід у межах TTL - лише попадання.
        _ = await w.Warmer.WarmOnceAsync(CancellationToken.None);
        Assert.Equal(warmed.Count, w.Probe.Maps);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прогрів_після_TTL_оновлює_карту_у_фоновому_проході_а_не_у_читача()
    {
        var w = await WarmRigAsync();
        using var cacheLease = w.R.Cache;
        await using var providerLease = w.Provider;

        var warmed = await w.Warmer.WarmOnceAsync(CancellationToken.None);
        await EditAsync(w.R.Fresh, w.R.Period, w.R.Chain.ColumnDefIds[1], Now.AddHours(1));
        w.R.Epoch.Bump(w.R.Period);
        w.Clock.Advance(StaleResultsMapCache.Ttl + TimeSpan.FromSeconds(1));

        _ = await w.Warmer.WarmOnceAsync(CancellationToken.None);
        var before = w.Probe.Maps;
        Assert.True(before >= warmed.Count + 1);

        Assert.Equal(new[] { w.R.A, w.R.B, w.R.Fresh }.Order(), (await WarmPageAsync(w)).Order());
        Assert.Equal(before, w.Probe.Maps);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прогрів_помилка_не_кешується_не_валить_прохід_і_наступний_прохід_довантажує()
    {
        var w = await WarmRigAsync();
        using var cacheLease = w.R.Cache;
        await using var providerLease = w.Provider;

        w.Probe.FailNextMapQuery();
        var first = await w.Warmer.WarmOnceAsync(CancellationToken.None);
        var attempted = w.Probe.Maps;
        Assert.Equal(attempted - 1, first.Count);
        Assert.True(attempted >= 2);

        var second = await w.Warmer.WarmOnceAsync(CancellationToken.None);

        // Решта були попаданнями; довантажено лише період, що не вдався.
        Assert.Equal(attempted, second.Count);
        Assert.Equal(attempted + 1, w.Probe.Maps);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прогрів_не_дає_чужому_ключу_автора_прогрітий_запис_доступ_не_порушується()
    {
        var w = await WarmRigAsync();
        using var cacheLease = w.R.Cache;
        await using var providerLease = w.Provider;

        _ = await w.Warmer.WarmOnceAsync(CancellationToken.None);
        var mapsAfterWarm = w.Probe.Maps;

        // Проєкт поза доступом читача: порожньо навіть при прогрітій карті.
        await using (var db = InterceptedContext(w.Probe, w.R.Epoch))
        {
            var hidden = await StaleOnPageAsync(WarmStore(w, db), w.R, [w.R.Chain.ProjectId + 1_000_000]);
            Assert.Empty(hidden);
        }

        // Фільтр автора - власний ключ: прогрітий «*» його не обслуговує (окремий запит карти) і дає лише B.
        await using var db2 = InterceptedContext(w.Probe, w.R.Epoch);
        var byUser = await WarmStore(w, db2).ListAsync(
            projectId: null, new PeriodKeyFilter(w.R.Period),
            new DocumentListFilter(null, null, ResultsStale: true, StaleByUserId: 62),
            new CursorRequest(Limit: 200), [w.R.Chain.ProjectId], CancellationToken.None);
        Assert.Equal([w.R.B], byUser.Items.Select(d => d.Id));
        Assert.Equal(mapsAfterWarm + 1, w.Probe.Maps);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відбиток_довідників_кешується_60_с_один_запит_на_N_читань_і_свіжий_після_TTL()
    {
        var w = await WarmRigAsync();
        using var cacheLease = w.R.Cache;
        await using var providerLease = w.Provider;

        for (var i = 0; i < 4; i++)
        {
            _ = await WarmPageAsync(w);
        }

        Assert.Equal(1, w.Probe.RegistryFingerprints);

        w.Clock.Advance(StaleResultsMapCache.RegistryFingerprintTtl - TimeSpan.FromSeconds(1));
        _ = await WarmPageAsync(w);
        Assert.Equal(1, w.Probe.RegistryFingerprints);

        w.Clock.Advance(TimeSpan.FromSeconds(2));
        _ = await WarmPageAsync(w);
        Assert.Equal(2, w.Probe.RegistryFingerprints);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відбиток_довідників_після_підйому_епохи_читається_свіжий()
    {
        var w = await WarmRigAsync();
        using var cacheLease = w.R.Cache;
        await using var providerLease = w.Provider;

        _ = await WarmPageAsync(w);
        _ = await WarmPageAsync(w);
        Assert.Equal(1, w.Probe.RegistryFingerprints);

        // Правка комірок періоду довідників не стосується: кеш відбитка не скидається.
        w.R.Epoch.Bump(w.R.Period);
        _ = await WarmPageAsync(w);
        Assert.Equal(1, w.Probe.RegistryFingerprints);

        // Глобальний підйом (довідник, невідомий період) - скидає.
        w.R.Epoch.Bump();
        _ = await WarmPageAsync(w);
        Assert.Equal(2, w.Probe.RegistryFingerprints);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відбиток_довідників_власна_правка_довідника_скидає_кеш_одразу_без_TTL()
    {
        var (registryId, r) = await RegistryRigAsync(Now.AddHours(-1), withEdge: true);
        var probe = new WarmProbe();
        var clock = new MovableClock(Now.AddHours(2));
        using var _ = r.Cache;

        await using (var read = InterceptedContext(probe, r.Epoch))
        {
            Assert.False(await FlagAsync(new DocumentStore(read, r.Cache, clock, r.Epoch), r, r.Fresh));
        }

        Assert.Equal(1, probe.RegistryFingerprints);

        // Власний запис значень довідника: ревізія даних росте, UnitOfWork ставить мітку і піднімає епоху.
        await using (var write = InterceptedContext(probe, r.Epoch))
        {
            var registry = await write.RegistryDefs.SingleAsync(d => d.Id == registryId);
            registry.BumpDataRevision();
            await new UnitOfWork(write, new TestClock(Now.AddHours(1))).SaveChangesAsync(CancellationToken.None);
        }

        await using var after = InterceptedContext(probe, r.Epoch);
        Assert.True(await FlagAsync(new DocumentStore(after, r.Cache, clock, r.Epoch), r, r.Fresh));
        Assert.Equal(2, probe.RegistryFingerprints);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відбиток_довідників_збій_читання_віддає_старе_до_2_TTL_а_далі_помилка()
    {
        var w = await WarmRigAsync();
        using var cacheLease = w.R.Cache;
        await using var providerLease = w.Provider;

        _ = await WarmPageAsync(w);
        w.Probe.FailRegistryQueries(true);
        w.Clock.Advance(StaleResultsMapCache.RegistryFingerprintTtl + TimeSpan.FromSeconds(1));

        // Старий запис (< 2xTTL) рятує читача; збій не кешується як успіх.
        _ = await WarmPageAsync(w);
        Assert.True(w.Probe.RegistryFingerprints >= 2, "збій мав бути спробою читання, а не мовчки пропущеним");

        w.Clock.Advance(StaleResultsMapCache.RegistryFingerprintTtl);
        await Assert.ThrowsAnyAsync<Exception>(() => WarmPageAsync(w));

        w.Probe.FailRegistryQueries(false);
        _ = await WarmPageAsync(w);
    }
}
