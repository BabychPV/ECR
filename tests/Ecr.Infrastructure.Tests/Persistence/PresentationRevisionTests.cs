using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Інкремент ревізії — **одним statement із `OUTPUT`** (R-B7).
/// Read-modify-write у застосунку заборонений, бо інстансів ≥2 і дві
/// презентаційні правки одночасно дали б однакову ревізію.
/// </summary>
[Collection("SqlServer")]
public sealed class PresentationRevisionTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Інкремент_повертає_нове_значення_одним_запитом()
    {
        var doc = await BuildAsync();
        await using var db = CreateContext();

        var connection = (SqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        connection.StatisticsEnabled = true;
        connection.ResetStatistics();

        var revision = await new TemplateVersionStore(db)
            .IncrementPresentationRevisionAsync(doc.TemplateVersionId, CancellationToken.None);

        var roundtrips = (long)connection.RetrieveStatistics()["ServerRoundtrips"]!;

        // ⚠ ОДИН похід до сервера. «Прочитати → додати → записати» — це три,
        // і між першим і третім інший інстанс встигає зробити те саме.
        Assert.Equal(1, roundtrips);
        Assert.Equal(1, revision);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Паралельні_інкременти_дають_різні_значення()
    {
        var doc = await BuildAsync();
        const int Parallel = 16;

        var results = await Task.WhenAll(Enumerable.Range(0, Parallel).Select(async _ =>
        {
            await using var db = CreateContext();
            return await new TemplateVersionStore(db)
                .IncrementPresentationRevisionAsync(doc.TemplateVersionId, CancellationToken.None);
        }));

        // ⚠ Саме тут ламається read-modify-write: два однакові значення
        // означають два різні знімки під одним ключем кешу `v{id}:r{rev}`.
        // Знайти таке потім практично неможливо — інстанси віддають різну
        // структуру, а ключ той самий.
        Assert.Equal(Parallel, results.Distinct().Count());
        Assert.Equal(Parallel, results.Max());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ключ_кешу_змінюється_після_презентаційної_правки()
    {
        var doc = await BuildAsync();
        var memory = new MemoryCache(new MemoryCacheOptions());

        string before, after;
        await using (var db = CreateContext())
        {
            before = (await new MetadataCache(memory, db)
                .GetAsync(doc.TemplateVersionId, CancellationToken.None)).CacheKey;
        }

        await using (var db = CreateContext())
        {
            await new TemplateVersionStore(db)
                .IncrementPresentationRevisionAsync(doc.TemplateVersionId, CancellationToken.None);
        }

        await using (var db = CreateContext())
        {
            after = (await new MetadataCache(memory, db)
                .GetAsync(doc.TemplateVersionId, CancellationToken.None)).CacheKey;
        }

        // Саме це й робить інвалідацію непотрібною: правка створює НОВИЙ ключ,
        // а не псує старий (D-16). Когерентність кешу між інстансами зникає
        // як клас проблеми.
        Assert.NotEqual(before, after);
        Assert.EndsWith(":r0", before, StringComparison.Ordinal);
        Assert.EndsWith(":r1", after, StringComparison.Ordinal);
    }

    private async Task<TestDocument> BuildAsync()
        => await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(ct: CancellationToken.None);

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
