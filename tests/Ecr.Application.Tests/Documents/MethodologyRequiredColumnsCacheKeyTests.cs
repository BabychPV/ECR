// tests/Ecr.Application.Tests/Documents/MethodologyRequiredColumnsCacheKeyTests.cs
using Ecr.Application.Documents;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// C1: вміст версії методології кешується ЛОКАЛІЗОВАНИМ до версії шаблону документа — ключ містить обидві версії.
/// </summary>
public sealed class MethodologyRequiredColumnsCacheKeyTests
{
    [Fact]
    [Trait("Finding", "C1")]
    public async Task Та_сама_версія_методології_кешується_окремо_для_кожної_версії_шаблону()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
        var cache = new MethodologyRequiredColumnsCache(memory);
        var loads = 0;

        Task<IReadOnlySet<int>> Load(int column, CancellationToken _)
        {
            loads++;
            return Task.FromResult<IReadOnlySet<int>>(new HashSet<int> { column });
        }

        var source = await cache.RequiredColumnIdsAsync(70, 2, ct => Load(12, ct), CancellationToken.None);
        var clone = await cache.RequiredColumnIdsAsync(70, 3, ct => Load(13, ct), CancellationToken.None);
        var sourceAgain = await cache.RequiredColumnIdsAsync(70, 2, ct => Load(99, ct), CancellationToken.None);

        Assert.Equal([12], source);
        Assert.Equal([13], clone);
        Assert.Equal([12], sourceAgain);
        Assert.Equal(2, loads);
    }
}
