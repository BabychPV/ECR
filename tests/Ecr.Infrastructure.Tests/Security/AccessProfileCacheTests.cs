// tests/Ecr.Infrastructure.Tests/Security/AccessProfileCacheTests.cs
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Caching;
using Ecr.TestKit;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// Кеш профілю доступу. Профіль будується **раз на сесію** (ФВ-6.10), але
/// відкликання прав діє **негайно** — через зміну `SecurityStamp`, яка дає
/// інший ключ кешу.
/// </summary>
public sealed class AccessProfileCacheTests : IDisposable
{
    private const int UserId = 7;

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Профіль_будується_один_раз_на_сесію()
    {
        var cache = new AccessProfileCache(_memory);
        var builds = 0;

        var first = await cache.GetOrCreateAsync(UserId, "s1", "", _ => Build(ref builds, "s1"), CancellationToken.None);
        var second = await cache.GetOrCreateAsync(UserId, "s1", "", _ => Build(ref builds, "s1"), CancellationToken.None);

        // Резолвити права на кожну комірку — гарантована смерть продуктивності:
        // на права відведено 50 мс на весь запит відкриття таблиці 500×60.
        Assert.Equal(1, builds);
        Assert.Same(first, second);
    }

    /// <remarks>
    /// Мутація: прибрати перевірку <c>_dirty</c> у <c>GetOrCreateAsync</c> (читання) або в <c>BuildAsync</c>
    /// (запис) — червоне; прибрати скидання прапорця в <c>InvalidateAll</c> — червоне.
    /// </remarks>
    [Fact]
    public async Task Збій_скидання_вмикає_fail_closed_і_профілі_не_кешуються_до_успішного_InvalidateAll()
    {
        var cache = new AccessProfileCache(_memory);
        var builds = 0;
        await cache.GetOrCreateAsync(UserId, "s1", "", _ => Build(ref builds, "s1"), CancellationToken.None);

        cache.MarkInvalidationFailed();

        // застарілий запис не читається, нові не кешуються: сховище опитується щоразу
        await cache.GetOrCreateAsync(UserId, "s1", "", _ => Build(ref builds, "s1"), CancellationToken.None);
        await cache.GetOrCreateAsync(UserId, "s1", "", _ => Build(ref builds, "s1"), CancellationToken.None);
        Assert.Equal(3, builds);

        cache.InvalidateAll(); // успішне повне скидання знімає прапорець

        await cache.GetOrCreateAsync(UserId, "s1", "", _ => Build(ref builds, "s1"), CancellationToken.None);
        await cache.GetOrCreateAsync(UserId, "s1", "", _ => Build(ref builds, "s1"), CancellationToken.None);
        Assert.Equal(4, builds);
    }

    /// <remarks>Мутація: <c>InvalidateUser</c> знімає прапорець dirty — червоне.</remarks>
    [Fact]
    public async Task InvalidateUser_не_знімає_dirty_кеш_лишається_fail_closed()
    {
        var cache = new AccessProfileCache(_memory);
        var builds = 0;
        cache.MarkInvalidationFailed();

        cache.InvalidateUser(UserId);

        await cache.GetOrCreateAsync(UserId, "s1", "", _ => Build(ref builds, "s1"), CancellationToken.None);
        await cache.GetOrCreateAsync(UserId, "s1", "", _ => Build(ref builds, "s1"), CancellationToken.None);
        Assert.Equal(2, builds);
    }

    /// <remarks>
    /// Мутація: <c>InvalidateAll</c> знімає прапорець безумовно (<c>_cleanGen = _failGen</c> наприкінці) — червоне.
    /// Детерміновано: збій реєструється з колбека скасування токена «усе», тобто всередині <c>InvalidateAll</c>.
    /// </remarks>
    [Fact]
    public async Task InvalidateAll_що_стартував_до_збою_не_знімає_пізніший_збій()
    {
        var spy = new SpyMemory(_memory);
        var cache = new AccessProfileCache(spy);
        var builds = 0;
        await cache.GetOrCreateAsync(UserId, "s1", "", _ => Build(ref builds, "s1"), CancellationToken.None);
        spy.Entry!.ExpirationTokens[0].RegisterChangeCallback(_ => cache.MarkInvalidationFailed(), null);

        cache.InvalidateAll(); // збій з'являється, поки скидання ще триває

        await cache.GetOrCreateAsync(UserId, "s1", "", _ => Build(ref builds, "s1"), CancellationToken.None);
        await cache.GetOrCreateAsync(UserId, "s1", "", _ => Build(ref builds, "s1"), CancellationToken.None);
        Assert.Equal(3, builds); // dirty лишився: профіль будується щоразу

        cache.InvalidateAll(); // наступне повне скидання знімає
        await cache.GetOrCreateAsync(UserId, "s1", "", _ => Build(ref builds, "s1"), CancellationToken.None);
        await cache.GetOrCreateAsync(UserId, "s1", "", _ => Build(ref builds, "s1"), CancellationToken.None);
        Assert.Equal(4, builds);
    }

    /// <remarks>
    /// Поточна семантика: <c>InvalidateAll</c> НЕ ковтає виняток скасування токена «усе» (колбек кидає →
    /// <see cref="AggregateException"/> виходить назовні, бо <c>CancellationTokenSource.Cancel</c> збирає винятки колбеків),
    /// а прапорець dirty знімається лише ПІСЛЯ успішного скасування. Мутація: піднімати <c>_cleanGen</c> до
    /// <c>old.Cancel()</c> або у <c>finally</c> — червоне (профіль закешувався б попри збій скидання).
    /// </remarks>
    [Fact]
    public async Task InvalidateAll_що_завершився_винятком_не_знімає_dirty()
    {
        var spy = new SpyMemory(_memory);
        var cache = new AccessProfileCache(spy);
        var builds = 0;
        await cache.GetOrCreateAsync(UserId, "s1", "", _ => Build(ref builds, "s1"), CancellationToken.None);
        spy.Entry!.ExpirationTokens[0].RegisterChangeCallback(_ => throw new InvalidOperationException("збій скидання"), null);
        cache.MarkInvalidationFailed();

        var thrown = Assert.Throws<AggregateException>(() => cache.InvalidateAll());
        Assert.IsType<InvalidOperationException>(Assert.Single(thrown.InnerExceptions));

        await cache.GetOrCreateAsync(UserId, "s1", "", _ => Build(ref builds, "s1"), CancellationToken.None);
        await cache.GetOrCreateAsync(UserId, "s1", "", _ => Build(ref builds, "s1"), CancellationToken.None);
        Assert.Equal(3, builds); // dirty лишився: профіль будується щоразу
    }
    private sealed class SpyMemory(IMemoryCache inner) : IMemoryCache
    {
        public ICacheEntry? Entry { get; private set; }
        public ICacheEntry CreateEntry(object key) => Entry = inner.CreateEntry(key);
        public bool TryGetValue(object key, out object? value) => inner.TryGetValue(key, out value);
        public void Remove(object key) => inner.Remove(key);
        public void Dispose() { }
    }

    /// <remarks>Мутація: прибрати повторну спробу за зміною епохи в <c>GetOrCreateAsync</c> — приєднаний отримує профіль до скидання, червоне.</remarks>
    [Fact]
    public async Task Скидання_під_час_польоту_не_віддає_профіль_побудований_до_нього_жодному_з_учасників()
    {
        var cache = new AccessProfileCache(_memory);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builds = 0;
        AccessProfile? stale = null;

        var initiator = cache.GetOrCreateAsync(UserId, "s1", "", async _ =>
        {
            var p = await Build(ref builds, "s1");
            if (stale is null)
            {
                stale = p; // лише перша (до скидання) побудова; повторна — свіжа
                started.TrySetResult();
            }

            await release.Task;
            return p;
        }, CancellationToken.None);
        await started.Task;
        var joiner = cache.GetOrCreateAsync(UserId, "s1", "", _ => Build(ref builds, "s1"), CancellationToken.None);

        cache.InvalidateAll(); // «коміт» гранта між початком польоту та відповіддю
        release.SetResult();

        var results = await Task.WhenAll(initiator, joiner);

        Assert.All(results, r => Assert.NotSame(stale, r));
    }

    /// <remarks>
    /// ent7 P3-5. Друге скидання під час ДРУГОГО польоту: профіль другої спроби теж побудовано до скидання.
    /// Мутація: повернути <c>built.Profile</c> з другої спроби безумовно (без побудови поза польотом) — червоне.
    /// </remarks>
    [Fact]
    public async Task Друге_скидання_під_час_другого_польоту_не_віддає_профіль_другої_спроби()
    {
        var cache = new AccessProfileCache(_memory);
        var gates = new[]
        {
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var started = new[]
        {
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var calls = 0;
        var profiles = new List<AccessProfile>();

        var request = cache.GetOrCreateAsync(UserId, "s1", "", async _ =>
        {
            var n = Interlocked.Increment(ref calls) - 1;
            var builds = 0;
            var p = await Build(ref builds, "s1");
            lock (profiles)
            {
                profiles.Add(p);
            }

            if (n < 2)
            {
                started[n].TrySetResult();
                await gates[n].Task;
            }

            return p;
        }, CancellationToken.None);

        await started[0].Task;
        cache.InvalidateAll();
        gates[0].SetResult();
        await started[1].Task; // друга спроба побудована під епохою 1 ...
        cache.InvalidateAll(); // ... і скидання №2 сталося до її завершення
        gates[1].SetResult();

        var result = await request;

        Assert.Equal(3, calls);
        Assert.Same(profiles[2], result);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Зміна_SecurityStamp_дає_інший_ключ_кешу()
    {
        var cache = new AccessProfileCache(_memory);
        var builds = 0;

        await cache.GetOrCreateAsync(UserId, "s1", "", _ => Build(ref builds, "s1"), CancellationToken.None);
        await cache.GetOrCreateAsync(UserId, "s2", "", _ => Build(ref builds, "s2"), CancellationToken.None);

        Assert.Equal(2, builds);

        // Старий запис нікуди не подівся — він просто більше не адресується.
        // Саме тому явна інвалідація не потрібна: її роль виконує ключ.
        Assert.True(_memory.TryGetValue(AccessProfileCache.Key(UserId, "s1", ""), out _));
        Assert.True(_memory.TryGetValue(AccessProfileCache.Key(UserId, "s2", ""), out _));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Відкликання_ролі_діє_негайно_а_не_після_закінчення_cookie()
    {
        var cache = new AccessProfileCache(_memory);
        var builds = 0;

        var before = await cache.GetOrCreateAsync(
            UserId, "s1", "", _ => Build(ref builds, "s1", GrantLevel.Manage), CancellationToken.None);
        Assert.Equal(GrantLevel.Manage, before.LevelFor(ResourceKind.Project, AccessBuilder.ProjectId));

        // Відкликання ролі крутить SecurityStamp у sec.User; наступний запит
        // приходить уже з новим штампом.
        var after = await cache.GetOrCreateAsync(
            UserId, "s2", "", _ => Build(ref builds, "s2", GrantLevel.None), CancellationToken.None);

        // ⚠ Якби ключ був лише за userId, відкликана роль жила б до кінця
        // cookie — тобто «негайно» перетворилося б на «колись», і це була б
        // тиха діра, яку не видно ні в логах, ні в UI.
        Assert.Equal(GrantLevel.None, after.LevelFor(ResourceKind.Project, AccessBuilder.ProjectId));
        Assert.Equal(2, builds);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Профіль_симуляції_не_потрапляє_в_кеш()
    {
        var cache = new AccessProfileCache(_memory);
        var builds = 0;

        var first = await cache.GetOrCreateAsync(
            UserId, "s1", "", _ => Simulated(ref builds), CancellationToken.None);
        var second = await cache.GetOrCreateAsync(
            UserId, "s1", "", _ => Simulated(ref builds), CancellationToken.None);

        // ⚠ Ключ складається з користувача і штампа, тому профіль суб'єкта ліг
        // би саме туди, звідки його візьме справжній користувач — разом із
        // чужими правами і прапорцем IsSimulation (ФВ-6.16a п. 4).
        Assert.True(first.IsSimulation);
        Assert.True(second.IsSimulation);
        Assert.Equal(2, builds);
        Assert.False(_memory.TryGetValue(AccessProfileCache.Key(UserId, "s1", ""), out _));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task InvalidateUser_видаляє_всі_записи_користувача_за_будь_якого_відбитку_груп_і_не_чіпає_інших()
    {
        var cache = new AccessProfileCache(_memory);
        var builds = 0;
        await cache.GetOrCreateAsync(UserId, "s1", "", _ => Build(ref builds, "s1"), CancellationToken.None);
        await cache.GetOrCreateAsync(UserId, "s1", "fp-groups", _ => Build(ref builds, "s1"), CancellationToken.None);
        await cache.GetOrCreateAsync(UserId + 1, "s9", "", _ => Build(ref builds, "s9"), CancellationToken.None);

        cache.InvalidateUser(UserId);

        // ⛔ Мутація: eviction лише за ключем із порожнім відбитком лишає запис із групами (fail-open).
        Assert.False(_memory.TryGetValue(AccessProfileCache.Key(UserId, "s1", ""), out _));
        Assert.False(_memory.TryGetValue(AccessProfileCache.Key(UserId, "s1", "fp-groups"), out _));
        Assert.True(_memory.TryGetValue(AccessProfileCache.Key(UserId + 1, "s9", ""), out _));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task InvalidateAll_скидає_весь_кеш_і_нові_записи_кешуються_знову()
    {
        var cache = new AccessProfileCache(_memory);
        var builds = 0;
        await cache.GetOrCreateAsync(UserId, "s1", "a", _ => Build(ref builds, "s1"), CancellationToken.None);
        await cache.GetOrCreateAsync(UserId + 1, "s9", "b", _ => Build(ref builds, "s9"), CancellationToken.None);

        cache.InvalidateAll();

        Assert.False(_memory.TryGetValue(AccessProfileCache.Key(UserId, "s1", "a"), out _));
        Assert.False(_memory.TryGetValue(AccessProfileCache.Key(UserId + 1, "s9", "b"), out _));

        await cache.GetOrCreateAsync(UserId, "s1", "a", _ => Build(ref builds, "s1"), CancellationToken.None);
        Assert.True(_memory.TryGetValue(AccessProfileCache.Key(UserId, "s1", "a"), out _));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Переповнення_переліку_скидає_реально_закешовані_профілі_усіх_користувачів()
    {
        var cache = new AccessProfileCache(_memory);
        var builds = 0;
        await cache.GetOrCreateAsync(UserId, "s1", "g", _ => Build(ref builds, "s1"), CancellationToken.None);
        await cache.GetOrCreateAsync(UserId + 1, "s9", "", _ => Build(ref builds, "s9"), CancellationToken.None);
        Assert.True(_memory.TryGetValue(AccessProfileCache.Key(UserId, "s1", "g"), out _));

        // Перелік користувачів неповний (Overflow) — Ids не називає нікого, скидатись мусить усе.
        GrantProfileInvalidation.Run(
            cache, Substitute.For<ILogger>(), new GrantedUsers([], Overflow: true));

        // ⛔ Мутація: InvalidateAll → no-op лишає ці записи (fail-open).
        Assert.False(_memory.TryGetValue(AccessProfileCache.Key(UserId, "s1", "g"), out _));
        Assert.False(_memory.TryGetValue(AccessProfileCache.Key(UserId + 1, "s9", ""), out _));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Профіль_побудований_під_час_скидання_не_кешується_застарілим()
    {
        var cache = new AccessProfileCache(_memory);
        var builds = 0;

        await cache.GetOrCreateAsync(UserId, "s1", "", _ =>
        {
            cache.InvalidateUser(UserId); // скидання посеред побудови
            return Build(ref builds, "s1");
        }, CancellationToken.None);

        Assert.False(_memory.TryGetValue(AccessProfileCache.Key(UserId, "s1", ""), out _));
    }

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    private static Task<AccessProfile> Build(ref int builds, string stamp, GrantLevel level = GrantLevel.Write)
    {
        builds++;

        var builder = new AccessBuilder { UserId = UserId };
        if (level != GrantLevel.None)
        {
            builder.Grant(ResourceKind.Project, AccessBuilder.ProjectId, level);
        }

        var profile = builder.Build();
        return Task.FromResult(new AccessProfile
        {
            CacheKey = AccessProfileCache.Key(UserId, stamp, ""),
            UserId = profile.UserId,
            SecurityStamp = stamp,
            Permissions = profile.Permissions,
            Grants = profile.Grants,
            Denies = profile.Denies,
            RoleIds = profile.RoleIds,
        });
    }

    private static Task<AccessProfile> Simulated(ref int builds)
    {
        builds++;
        return Task.FromResult(
            new AccessBuilder { UserId = UserId }
                .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Manage)
                .Build(simulation: true, simulatedFor: 42));
    }
}
