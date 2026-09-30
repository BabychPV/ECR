using Ecr.Application.Security;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Observability;
using Ecr.TestKit;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Infrastructure.Tests.Caching;

/// <summary>
/// <c>ecr.cache.miss</c> / <c>ecr.cache.hit</c> (ФВ-12.7, НФ-8.6.2) для кешу профілю доступу.
/// Кеш метаданих — у <c>MetadataCacheTests</c> (потрібна БД).
/// </summary>
/// <remarks>Мутації: прибрати <c>RecordCache</c> у <c>AccessProfileCache</c> (гілка hit або miss) — червоне.</remarks>
public sealed class CacheHitRatioMetricTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Профіль_доступу_перший_виклик_промах_повторний_влучання()
    {
        using var capture = new InfrastructureMetricsCapture();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new AccessProfileCache(memory);

        await cache.GetOrCreateAsync(7, "s1", "", _ => Task.FromResult(NewProfile()), CancellationToken.None);
        Assert.Single(capture.Of(InfrastructureMetrics.CacheMiss));
        Assert.Empty(capture.Of(InfrastructureMetrics.CacheHit));

        await cache.GetOrCreateAsync(7, "s1", "", _ => Task.FromResult(NewProfile()), CancellationToken.None);

        var miss = Assert.Single(capture.Of(InfrastructureMetrics.CacheMiss));
        var hit = Assert.Single(capture.Of(InfrastructureMetrics.CacheHit));
        Assert.Equal(InfrastructureMetrics.AccessProfileCacheName, miss["cache"]);
        Assert.Equal(InfrastructureMetrics.AccessProfileCacheName, hit["cache"]);
    }

    /// <remarks>Мутація: прибрати <c>RecordAccessProfileBuild</c> у <c>BuildAsync</c> — червоне; записувати й на влучанні — червоне.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Побудова_профілю_дає_один_запис_тривалості_а_профіль_із_кешу_ні()
    {
        using var capture = new InfrastructureMetricsCapture();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new AccessProfileCache(memory);

        await cache.GetOrCreateAsync(7, "s1", "", _ => Task.FromResult(NewProfile()), CancellationToken.None);
        var build = Assert.Single(capture.Of(InfrastructureMetrics.AccessProfileBuild));
        Assert.True((double)build["value"]! >= 0);

        await cache.GetOrCreateAsync(7, "s1", "", _ => Task.FromResult(NewProfile()), CancellationToken.None);
        Assert.Single(capture.Of(InfrastructureMetrics.AccessProfileBuild));
    }

    private static AccessProfile NewProfile()
    {
        var profile = new AccessBuilder { UserId = 7 }.Build();
        return new AccessProfile
        {
            CacheKey = AccessProfileCache.Key(7, "s1", ""),
            UserId = profile.UserId,
            SecurityStamp = "s1",
            Permissions = profile.Permissions,
            Grants = profile.Grants,
            Denies = profile.Denies,
            RoleIds = profile.RoleIds,
        };
    }
}
