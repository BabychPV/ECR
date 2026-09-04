using Ecr.Domain.Entities.Configuration;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Infrastructure.Tests.Caching;

/// <summary>
/// Ключ <c>v{id}:r{rev}</c> робить інвалідацію непотрібною: презентаційна
/// правка створює новий ключ, а не псує старий (D-16).
/// </summary>
/// <remarks>
/// ⚠ Тести позначені <c>Integration</c>, хоча в <c>06-tests.md</c> цієї
/// позначки не було (`Q-043`). Причина: <see cref="EcrDbContext"/> описує
/// модель SQL Server — <c>nvarchar(max)</c>, <c>rowversion</c>, тригери,
/// послідовності. На SQLite вона не створюється (<c>near "max": syntax
/// error</c>), а робити її провайдер-нейтральною означало б тестувати не ту
/// модель, яка працює в проді.
/// </remarks>
[Collection("SqlServer")]
public sealed class MetadataCacheTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повторне_читання_не_звертається_до_БД()
    {
        var doc = await ArrangeAsync();
        var memory = new MemoryCache(new MemoryCacheOptions());

        var first = new List<string>();
        await using (var db = CreateContext(first))
        {
            await new MetadataCache(memory, db).GetAsync(doc.TemplateVersionId, CancellationToken.None);
        }

        var second = new List<string>();
        await using (var db = CreateContext(second))
        {
            await new MetadataCache(memory, db).GetAsync(doc.TemplateVersionId, CancellationToken.None);
        }

        // ⚠ Точне формулювання: СТРУКТУРА вдруге не читається. Один запит
        // лишається — по PresentationRevision, і прибрати його не можна: саме
        // він робить ключ v{id}:r{rev} чесним. Без нього кеш віддавав би
        // застарілий знімок після презентаційної правки — тобто рівно ту
        // проблему когерентності, заради усунення якої ключ і придуманий.
        Assert.Equal(5, first.Count);
        Assert.Single(second);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Після_презентаційної_правки_повертається_новий_знімок()
    {
        var doc = await ArrangeAsync();
        var memory = new MemoryCache(new MemoryCacheOptions());

        TemplateVersionSnapshot before;
        await using (var db = CreateContext([]))
        {
            before = await new MetadataCache(memory, db).GetAsync(doc.TemplateVersionId, CancellationToken.None);
        }

        await BumpRevisionAsync(doc.TemplateVersionId);

        TemplateVersionSnapshot after;
        await using (var db = CreateContext([]))
        {
            after = await new MetadataCache(memory, db).GetAsync(doc.TemplateVersionId, CancellationToken.None);
        }

        Assert.Equal(0, before.PresentationRevision);
        Assert.Equal(1, after.PresentationRevision);
        Assert.NotSame(before, after);
        Assert.NotEqual(before.CacheKey, after.CacheKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Старий_знімок_лишається_валідним_для_старого_ключа()
    {
        var doc = await ArrangeAsync();
        var memory = new MemoryCache(new MemoryCacheOptions());

        TemplateVersionSnapshot before;
        await using (var db = CreateContext([]))
        {
            before = await new MetadataCache(memory, db).GetAsync(doc.TemplateVersionId, CancellationToken.None);
        }

        await BumpRevisionAsync(doc.TemplateVersionId);

        await using (var db = CreateContext([]))
        {
            await new MetadataCache(memory, db).GetAsync(doc.TemplateVersionId, CancellationToken.None);
        }

        // Новий ключ не витісняє старий: запит, який уже почався зі старою
        // ревізією, дочитує узгоджений знімок, а не половину нового.
        Assert.True(memory.TryGetValue(before.CacheKey, out TemplateVersionSnapshot? old));
        Assert.Same(before, old);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Знімок_містить_індекси_колонок_і_рядків_для_швидкого_доступу()
    {
        var doc = await ArrangeAsync();

        await using var db = CreateContext([]);
        var snapshot = await new MetadataCache(new MemoryCache(new MemoryCacheOptions()), db)
            .GetAsync(doc.TemplateVersionId, CancellationToken.None);

        var sheet = Assert.Single(snapshot.Sheets);
        var table = Assert.Single(sheet.Tables);

        // Індекси, а не лінійний обхід: резолвер посилань звертається до них
        // на кожну комірку формули, і O(n) там перетворився б на O(n²).
        Assert.Equal(doc.ColumnDefIds.Count, snapshot.ColumnsById.Count);
        Assert.Equal(doc.RowDefIds.Count, snapshot.RowsByKey.Count);
        Assert.Equal(doc.TableDefId, table.Id);
        Assert.All(doc.ColumnDefIds, id => Assert.True(snapshot.ColumnsById.ContainsKey(id)));
    }

    private async Task<TestDocument> ArrangeAsync()
        => await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(ct: CancellationToken.None);

    private EcrDbContext CreateContext(List<string> executed)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .LogTo(executed.Add, [RelationalEventId.CommandExecuted])
            .Options);

    private async Task BumpRevisionAsync(int versionId)
    {
        await using var db = CreateContext([]);
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE cfg.TemplateVersion SET PresentationRevision = PresentationRevision + 1 WHERE Id = {0}",
            versionId);
    }
}
