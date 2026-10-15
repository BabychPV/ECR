using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
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

    // ---- LP-45, lane D: кільце (усі Current-періоди), власне вікно SWR запису кільця, HotPeriodLimit ----
    // Мутації: у StaleResultsMapCache прибрати `Window` з Entry/вікно з TryStale (повернути const 120 с) - червоний
    // `Кільце_запис_живе_у_вікні_SWR_15_хв...`; у StaleMapWarmer.HotPeriodsAsync `Take(ring ? 120 : hotLimit)` -> `Take(hotLimit)` -
    // червоний `Кільце_прогріває_усі_Current_періоди...`; `Take(hotLimit)` -> `Take(2)` - червоний `HotPeriodLimit_...`.

    /// <summary>Фабрика областей фонового оновлення, що падає одразу: фоновий reload у тесті вікна не ходить у БД.</summary>
    private sealed class FailingScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("фонове оновлення вимкнено в тесті");
    }

    private static async Task<IReadOnlyList<int>> SeedCurrentPeriodsAsync(WarmRig w, int first, int count)
    {
        await using var db = w.R.Builder.CreateContext();
        var periods = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var run = new Ecr.Domain.Entities.Calculations.CalculationRun(
                w.R.Chain.ProjectId, first + i, triggeredByUserId: null, Now, null);
            run.Complete("Succeeded", Now.AddMinutes(1), "{}", errorMessage: null);
            run.MakeCurrent();
            db.CalculationRuns.Add(run);
            periods.Add(first + i);
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return periods;
    }

    /// <summary>Очікуваний набір: календарні (годинник 2026-05) + періоди з Current-прогоном (всі або N найновіших).</summary>
    private async Task<HashSet<int>> ExpectedWarmAsync(bool ring, int hotLimit)
    {
        var current = new List<int>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT PeriodKey FROM calc.CalculationRun WHERE Status = N'Current' AND PeriodKey IS NOT NULL";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            current.Add(reader.GetInt32(0));
        }

        var set = new HashSet<int> { 202605, 202604 };
        set.UnionWith(ring ? current : current.OrderByDescending(p => p).Take(hotLimit));
        return set;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Кільце_прогріває_усі_Current_періоди_по_одному_разу_і_оновлює_їх_після_TTL()
    {
        var w = await WarmRigAsync();
        using var cacheLease = w.R.Cache;
        await using var providerLease = w.Provider;
        _ = await SeedCurrentPeriodsAsync(w, 202501, 10);
        var all = await ExpectedWarmAsync(ring: true, hotLimit: 2);
        var hot = await ExpectedWarmAsync(ring: false, hotLimit: 2);
        Assert.True(all.Count > hot.Count + 5, "дані мають розрізняти кільце й гарячі");

        var warmed = await w.Warmer.WarmOnceAsync(CancellationToken.None, ring: true);

        Assert.Equal(all.Order(), warmed.Order());
        Assert.Equal(all.Count, w.Probe.Maps);

        // Другий прохід у межах TTL - лише попадання; після TTL - кожен ключ оновлено рівно раз.
        _ = await w.Warmer.WarmOnceAsync(CancellationToken.None, ring: true);
        Assert.Equal(all.Count, w.Probe.Maps);
        w.Clock.Advance(StaleResultsMapCache.Ttl + TimeSpan.FromSeconds(1));
        _ = await w.Warmer.WarmOnceAsync(CancellationToken.None, ring: true);
        Assert.Equal(all.Count * 2, w.Probe.Maps);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Кільце_запис_живе_у_вікні_SWR_15_хв_а_запис_гарячого_проходу_через_150_с_ні()
    {
        var w = await WarmRigAsync();
        using var cacheLease = w.R.Cache;
        await using var providerLease = w.Provider;
        var scopes = new FailingScopes();

        async Task<IReadOnlyDictionary<long, DateTime?>?> ReadAsync()
        {
            await using var db = InterceptedContext(w.Probe, w.R.Epoch);
            var maps = await new StaleResultsMapCache(db, w.R.Cache, w.Clock, w.R.Epoch, scopeFactory: scopes)
                .GetAsync(w.R.Period, [null], CancellationToken.None);
            return maps[0];
        }

        // Кільце: запис за 150 с (> TTL + 120 с) ще віддається як SWR - ТОЙ САМИЙ екземпляр карти, не нове завантаження.
        _ = await w.Warmer.WarmOnceAsync(CancellationToken.None, ring: true);
        var ringMap = await ReadAsync();
        Assert.NotNull(ringMap);
        w.Clock.Advance(TimeSpan.FromSeconds(150));
        Assert.Same(ringMap, await ReadAsync());

        // Гарячий прохід (вікно 120 с): через 150 с запис уже поза вікном - синхронне перезавантаження (інший екземпляр).
        var w2 = await WarmRigAsync();
        using var cacheLease2 = w2.R.Cache;
        await using var providerLease2 = w2.Provider;
        _ = await w2.Warmer.WarmOnceAsync(CancellationToken.None);
        var scopes2 = new FailingScopes();
        await using var db0 = InterceptedContext(w2.Probe, w2.R.Epoch);
        var hotMap = (await new StaleResultsMapCache(db0, w2.R.Cache, w2.Clock, w2.R.Epoch, scopeFactory: scopes2)
            .GetAsync(w2.R.Period, [null], CancellationToken.None))[0];
        w2.Clock.Advance(TimeSpan.FromSeconds(150));
        await using var db1 = InterceptedContext(w2.Probe, w2.R.Epoch);
        var after = (await new StaleResultsMapCache(db1, w2.R.Cache, w2.Clock, w2.R.Epoch, scopeFactory: scopes2)
            .GetAsync(w2.R.Period, [null], CancellationToken.None))[0];
        Assert.NotSame(hotMap, after);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task HotPeriodLimit_береться_з_параметра_а_не_константи_два()
    {
        var w = await WarmRigAsync();
        using var cacheLease = w.R.Cache;
        await using var providerLease = w.Provider;
        _ = await SeedCurrentPeriodsAsync(w, 202501, 10);
        var expected = await ExpectedWarmAsync(ring: false, hotLimit: 5);

        var warmed = await w.Warmer.WarmOnceAsync(CancellationToken.None, ring: false, hotLimit: 5);

        Assert.Equal(expected.Order(), warmed.Order());
        Assert.Equal(expected.Count, w.Probe.Maps);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Кільце_помилка_одного_періоду_не_зупиняє_решту_і_йде_послідовно()
    {
        var w = await WarmRigAsync();
        using var cacheLease = w.R.Cache;
        await using var providerLease = w.Provider;
        _ = await SeedCurrentPeriodsAsync(w, 202501, 10);
        var all = await ExpectedWarmAsync(ring: true, hotLimit: 2);

        w.Probe.FailNextMapQuery();
        var warmed = await w.Warmer.WarmOnceAsync(CancellationToken.None, ring: true);

        Assert.Equal(all.Count - 1, warmed.Count);
        Assert.Equal(all.Count, w.Probe.Maps);
    }
}
