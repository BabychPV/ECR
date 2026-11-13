using Ecr.Infrastructure.Caching;
using Ecr.TestKit;
using Microsoft.Extensions.Caching.SqlServer;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ecr.Infrastructure.Tests.Caching;

/// <summary>
/// Готові книги <c>.xlsx</c> у розподіленому кеші, разом із документом, з
/// якого їх побудовано (Q-180).
/// </summary>
/// <remarks>
/// ⚠ Тест інтеграційний навмисно — та сама причина, що й у
/// <see cref="ImportPreviewStoreTests"/>: побудова і забирання книги — два
/// запити, і другий може потрапити на інший інстанс застосунку.
/// </remarks>
[Collection("SqlServer")]
public sealed class ExportStoreTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збережена_книга_читається_іншим_екземпляром_разом_із_documentId()
    {
        // Два різні екземпляри — модель двох інстансів застосунку: той, що
        // побудував книгу, і той, на який потрапило завантаження.
        var writer = new ExportStore(Cache());
        var reader = new ExportStore(Cache());

        var exportId = Guid.NewGuid().ToString("N");
        var content = new byte[] { 1, 2, 3, 4, 5 };

        await writer.SaveAsync(
            exportId, documentId: 700, ownerUserId: 42, content, TimeSpan.FromMinutes(5), CancellationToken.None);

        var found = await reader.FindAsync(exportId, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(700, found.DocumentId);
        Assert.Equal(42, found.OwnerUserId);
        Assert.Equal(content, found.Content);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Невідомий_ключ_дає_порожнечу_а_не_виняток()
    {
        var store = new ExportStore(Cache());

        Assert.Null(await store.FindAsync(Guid.NewGuid().ToString("N"), CancellationToken.None));
    }

    /// <summary>
    /// X-1 (аудит 2026-11): основа імені файлу їде в тому самому конверті, що й книга, — без неї завантаження
    /// називало файл `document-{id}-{GUID}`.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ім_я_файлу_переживає_кеш_разом_із_кирилицею()
    {
        var writer = new ExportStore(Cache());
        var reader = new ExportStore(Cache());
        var content = new byte[] { 9, 8, 7 };

        var named = Guid.NewGuid().ToString("N");
        const string FileBaseName = "Звіт \"Тенгіз\" Қазақ (K) 2026-11";
        await writer.SaveAsync(
            named, documentId: 701, ownerUserId: 42, content, TimeSpan.FromMinutes(5), CancellationToken.None,
            FileBaseName);

        var foundNamed = await reader.FindAsync(named, CancellationToken.None);
        Assert.NotNull(foundNamed);
        Assert.Equal(FileBaseName, foundNamed.FileBaseName);
        Assert.Equal(content, foundNamed.Content);
        Assert.Equal(701, foundNamed.DocumentId);
        Assert.Equal(42, foundNamed.OwnerUserId);

        // Завдання, поставлене до X-1, імені не має: книга читається, ім'я — null (контролер дає запасне).
        var unnamed = Guid.NewGuid().ToString("N");
        await writer.SaveAsync(
            unnamed, documentId: 702, ownerUserId: 42, content, TimeSpan.FromMinutes(5), CancellationToken.None);

        var foundUnnamed = await reader.FindAsync(unnamed, CancellationToken.None);
        Assert.NotNull(foundUnnamed);
        Assert.Null(foundUnnamed.FileBaseName);
        Assert.Equal(content, foundUnnamed.Content);
    }

    /// <summary>Кеш поверх тієї самої бази, що й решта тестів.</summary>
    private SqlServerCache Cache()
        => new SqlServerCache(Options.Create(new SqlServerCacheOptions
        {
            ConnectionString = sql.ConnectionString,
            SchemaName = "dbo",
            TableName = "Cache",
        }));
}
