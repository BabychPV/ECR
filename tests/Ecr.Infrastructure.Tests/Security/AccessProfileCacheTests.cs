// tests/Ecr.Infrastructure.Tests/Security/AccessProfileCacheTests.cs
using Ecr.Application.Security;
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
        Assert.True(_memory.TryGetValue(AccessProfileCache.Key(UserId, "s1"), out _));
        Assert.True(_memory.TryGetValue(AccessProfileCache.Key(UserId, "s2"), out _));
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
        Assert.False(_memory.TryGetValue(AccessProfileCache.Key(UserId, "s1"), out _));
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
            CacheKey = AccessProfileCache.Key(UserId, stamp),
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
