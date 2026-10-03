using Ecr.Infrastructure.Localization;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Infrastructure.Tests.Localization;

/// <summary>
/// L1-02 (аудит 2026-10-03): мова з запиту не стає ключем кешу каталогу.
/// </summary>
[Collection("SqlServer")]
public sealed class UiStringCatalogLanguageCacheTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Сто_випадкових_мов_не_додають_записів_у_кеш()
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var store = new UiStringCatalogStore(db, memory);

        var english = await store.GetAsync("en", CancellationToken.None);
        var baseline = memory.Count;

        var random = new Random(20261003);
        for (var i = 0; i < 100; i++)
        {
            var code = new string([.. Enumerable.Range(0, 2 + (i % 2)).Select(_ => (char)('a' + random.Next(26)))]);
            if (code is "en" or "ru" or "kz")
            {
                continue;
            }

            var catalog = await store.GetAsync(code, CancellationToken.None);

            // Невідома мова обслуговується записом мови за замовчуванням.
            Assert.Equal("en", catalog.LanguageCode);
            Assert.Same(english, catalog);
        }

        Assert.Equal(baseline, memory.Count);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData("ru", "ru")]
    [InlineData("RU", "ru")]
    [InlineData("kz", "kz")]
    [InlineData("zz", "en")]
    [InlineData("<script>", "en")]
    public async Task Мова_звіряється_з_реєстром(string requested, string expected)
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var store = new UiStringCatalogStore(db, memory);

        Assert.Equal(expected, await store.ResolveLanguageAsync(requested, CancellationToken.None));
    }
}
