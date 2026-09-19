using Ecr.Application.Ports;
using Ecr.Infrastructure.Localization;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Infrastructure.Tests.Localization;

/// <summary>«Сирий» перелік рядків над справжньою таблицею (<c>BE-13</c>).</summary>
[Collection("SqlServer")]
public sealed class UiStringCatalogStoreRawTests(SqlServerFixture sql)
{
    private const string Key = "uiStrings.title";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Сирий_перелік_не_підміняє_відсутній_переклад_а_каталог_підміняє()
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var store = new UiStringCatalogStore(db, memory);

        try
        {
            await store.SetAsync(
                new UiStringWrite(Key, "ru", "Тексты интерфейса", UiStringScope.Private, null, DateTime.UtcNow),
                CancellationToken.None);

            var raw = await store.ListRawAsync("ru", CancellationToken.None);
            var english = await store.ListRawAsync("en", CancellationToken.None);

            // Рядків рівно стільки, скільки ключів в еталоні.
            Assert.Equal(english.Count, raw.Count);
            Assert.All(english, row => Assert.Equal(row.Reference, row.Value));

            Assert.Equal("Тексты интерфейса", raw.Single(r => r.Key == Key).Value);

            // ⛔ Сід російських рядків не має: усе, крім щойно записаного, — null.
            var other = raw.Single(r => r.Key == "common.save");
            Assert.Null(other.Value);
            Assert.False(string.IsNullOrEmpty(other.Reference));

            // Контроль: каталог для екрана ту саму прогалину закриває англійською.
            var screen = await store.GetAsync("ru", CancellationToken.None);
            Assert.Equal(other.Reference, screen.Strings["common.save"]);
        }
        finally
        {
            await using var connection = new SqlConnection(sql.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM sys_ecr.UiString WHERE [Key] = @key AND LanguageCode = N'ru';";
            command.Parameters.AddWithValue("@key", Key);
            await command.ExecuteNonQueryAsync();
        }
    }
}
