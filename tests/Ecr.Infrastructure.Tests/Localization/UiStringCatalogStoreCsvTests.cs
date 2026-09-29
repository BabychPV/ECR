using Ecr.Application.Ports;
using Ecr.Infrastructure.Localization;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Infrastructure.Tests.Localization;

/// <summary>Пакетний запис і рядки експорту над справжньою таблицею (<c>BE-13</c> ч.2).</summary>
[Collection("SqlServer")]
public sealed class UiStringCatalogStoreCsvTests(SqlServerFixture sql)
{
    // ✎ 2026-09-29: власні ключі тесту, а не посіяні `common.save`/`common.cancel`:
    // сід тепер заводить і їхні переклади kz (рішення людини), і `finally`, що
    // їх видаляв, лишав би базу, якій наступний сід повертає рядки з
    // інкрементом Revision — `SeedTests.Повторний_запуск_без_нових_рядків_не_піднімає_Revision`
    // падав би залежно від порядку тестів у колекції.
    private static readonly string[] Keys = ["test.csvStore.save", "test.csvStore.cancel"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пакет_пишеться_однією_ревізією_а_порожній_ревізії_не_рухає()
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var store = new UiStringCatalogStore(db, memory);
        var at = new DateTime(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc);

        try
        {
            // Еталон: експорт перелічує ключі мови за замовчуванням.
            await store.SetManyAsync(
                [.. Keys.Select(k => new UiStringWrite(k, "en", "e " + k, UiStringScope.Public, null, at))],
                CancellationToken.None);

            var before = await store.GetRevisionAsync(CancellationToken.None);

            var after = await store.SetManyAsync(
                [.. Keys.Select(k => new UiStringWrite(k, "kz", "т " + k, UiStringScope.Public, null, at))],
                CancellationToken.None);

            Assert.Equal(before + 1, after);
            Assert.Equal(after, await store.SetManyAsync([], CancellationToken.None));
            Assert.Equal(after, await store.GetRevisionAsync(CancellationToken.None));

            var rows = await store.ListForExportAsync("kz", CancellationToken.None);
            var save = rows.Single(r => r.Key == Keys[0]);
            Assert.Equal(("т " + Keys[0], at), (save.Value, save.ModifiedAt));
            Assert.True(await store.LanguageExistsAsync("kz", CancellationToken.None));
            Assert.False(await store.LanguageExistsAsync("xx", CancellationToken.None));
        }
        finally
        {
            await using var connection = new SqlConnection(sql.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM sys_ecr.UiString WHERE [Key] IN (@a, @b);";
            command.Parameters.AddWithValue("@a", Keys[0]);
            command.Parameters.AddWithValue("@b", Keys[1]);
            await command.ExecuteNonQueryAsync();
        }
    }
}
