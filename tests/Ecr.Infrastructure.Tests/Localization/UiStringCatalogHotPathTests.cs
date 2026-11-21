using Ecr.Application.Ports;
using Ecr.Infrastructure.Localization;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Infrastructure.Tests.Localization;

/// <summary>
/// PSS-29 (e2e RC #988): читання екрана входу — каталог рядків і перелік мов — обслуговуються з пам'яті, без бази.
/// </summary>
/// <remarks>
/// ⛔ Доказ не «лічильником», а відрізаною базою: друге сховище над ТИМ САМИМ кешем дивиться на порт, де SQL Server
/// немає, тож будь-який похід у базу кидає. До виправлення кожен <c>GetScopedAsync</c> відкривав з'єднання й читав
/// ревізію, а <c>ListLanguagesAsync</c> (публічний початковий стан) ходив у базу щоразу — тест червоний на першому ж
/// виклику.
///
/// ⛔ МУТАЦІЇ: повернути читання ревізії з бази на кожен запит — червоніє перший тест; прибрати
/// <c>RememberRevision</c> із запису — червоніє другий (правка з'являлася б лише після вікна в 5 с).
/// </remarks>
[Collection("SqlServer")]
public sealed class UiStringCatalogHotPathTests(SqlServerFixture sql)
{
    /// <summary>Порт 9 (discard) на петлі: з'єднання відхиляється одразу, без мережевого очікування.</summary>
    private const string DeadDatabase =
        "Server=tcp:127.0.0.1,9;Database=EcrNowhere;Integrated Security=true;TrustServerCertificate=True;"
        + "Connect Timeout=1;ConnectRetryCount=0;Pooling=false";

    private const string Probe = "test.hotPath.probe";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PSS-29")]
    public async Task Прогрітий_каталог_і_мови_віддаються_без_бази()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());

        await using (var live = Db(sql.ConnectionString))
        {
            var warm = new UiStringCatalogStore(live, memory);
            await warm.ListLanguagesAsync(CancellationToken.None);
            await warm.GetScopedAsync("en", UiStringScope.Public, CancellationToken.None);
            await warm.GetScopedAsync("ru", UiStringScope.Public, CancellationToken.None);
        }

        await using var dead = Db(DeadDatabase);
        var store = new UiStringCatalogStore(dead, memory);

        // ⚠ Вікно ревізії — 5 с від прогріву; усе нижче до першого кидка займає мілісекунди.
        var english = await store.GetScopedAsync("en", UiStringScope.Public, CancellationToken.None);
        var russian = await store.GetScopedAsync("RU", UiStringScope.Public, CancellationToken.None);
        var unknown = await store.GetScopedAsync("zz", UiStringScope.Public, CancellationToken.None);
        var languages = await store.ListLanguagesAsync(CancellationToken.None);
        var resolved = await store.ResolveLanguageAsync("kz", CancellationToken.None);

        Assert.Equal("en", english.LanguageCode);
        Assert.Equal("ru", russian.LanguageCode);
        Assert.Same(english, unknown);
        Assert.Contains(languages, l => l.Code == "kz");
        Assert.Equal("kz", resolved);

        // Контроль: база справді відрізана — свіже читання ревізії кидає.
        await Assert.ThrowsAsync<SqlException>(() => store.GetRevisionAsync(CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PSS-29")]
    public async Task Правку_видно_одразу_на_вузлі_що_її_записав_попри_мемо_ревізії()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        await using var db = Db(sql.ConnectionString);
        var store = new UiStringCatalogStore(db, memory);

        try
        {
            await store.SetAsync(
                new UiStringWrite(Probe, "en", "first", UiStringScope.Public, null, DateTime.UtcNow),
                CancellationToken.None);
            var before = await store.GetScopedAsync("en", UiStringScope.Public, CancellationToken.None);
            Assert.Equal("first", before.Strings[Probe]);

            await store.SetAsync(
                new UiStringWrite(Probe, "en", "second", UiStringScope.Public, null, DateTime.UtcNow),
                CancellationToken.None);
            var after = await store.GetScopedAsync("en", UiStringScope.Public, CancellationToken.None);

            Assert.Equal("second", after.Strings[Probe]);
            Assert.True(after.Revision > before.Revision, $"{before.Revision} -> {after.Revision}");
        }
        finally
        {
            await using var connection = new SqlConnection(sql.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM sys_ecr.UiString WHERE [Key] = @probe;";
            command.Parameters.AddWithValue("@probe", Probe);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static EcrDbContext Db(string connectionString)
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(connectionString).Options);
}
