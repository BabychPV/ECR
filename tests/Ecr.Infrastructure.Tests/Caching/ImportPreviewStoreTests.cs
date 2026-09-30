using Ecr.Infrastructure.Caching;
using Ecr.TestKit;
using Microsoft.Extensions.Caching.SqlServer;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ecr.Infrastructure.Tests.Caching;

/// <summary>
/// Diff імпорту в розподіленому кеші (ФВ-4.3).
/// </summary>
/// <remarks>
/// ⚠ Тест інтеграційний навмисно. Перевіряти <c>IDistributedCache</c> у
/// пам'яті означало б перевірити не те: уся суть цього сховища в тому, що
/// перегляд і застосування — два запити, і другий може потрапити на інший
/// інстанс застосунку. Крім того, лише тут видно, що таблиця <c>dbo.Cache</c>
/// зі скрипта <c>13-cache-table.sql</c> справді існує і має ту форму, якої
/// чекає пакет.
/// </remarks>
[Collection("SqlServer")]
public sealed class ImportPreviewStoreTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-4.3")]
    public async Task Збережений_diff_читається_іншим_екземпляром_сховища()
    {
        // Два різні екземпляри — модель двох інстансів застосунку: той, що
        // будував перегляд, і той, на який потрапило застосування.
        var writer = new ImportPreviewStore(Cache());
        var reader = new ImportPreviewStore(Cache());

        var token = Guid.NewGuid().ToString("N");
        const string Payload = """{"documentId":7,"periodKey":202603}""";

        await writer.SaveAsync(token, Payload, TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.Equal(Payload, await reader.FindAsync(token, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прибраний_diff_застосувати_вдруге_не_можна()
    {
        // ⛔ Інакше той самий перегляд застосували б двічі, і вдруге він писав
        // би поверх власного результату, вважаючи його чужою правкою.
        var store = new ImportPreviewStore(Cache());
        var token = Guid.NewGuid().ToString("N");

        await store.SaveAsync(token, "{}", TimeSpan.FromMinutes(5), CancellationToken.None);
        await store.RemoveAsync(token, CancellationToken.None);

        Assert.Null(await store.FindAsync(token, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Невідомий_токен_дає_порожнечу_а_не_виняток()
    {
        var store = new ImportPreviewStore(Cache());

        Assert.Null(await store.FindAsync(Guid.NewGuid().ToString("N"), CancellationToken.None));
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
