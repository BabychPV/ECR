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
    // ✎ 2026-09-29: сід тепер заводить базові переклади ru/kz (рішення людини),
    // тож «прогалину» дають власні ключі тесту, а не посіяний `common.save`.
    // ⚠ Посіяних рядків тест не чіпає зовсім: видалений переклад повертав би
    // наступний сід — з інкрементом Revision, і
    // `SeedTests.Повторний_запуск_без_нових_рядків_не_піднімає_Revision`
    // падав би залежно від порядку тестів у колекції.
    private const string Written = "test.rawStore.written";
    private const string Gap = "test.rawStore.gap";

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
            foreach (var key in (string[])[Written, Gap])
            {
                await store.SetAsync(
                    new UiStringWrite(key, "en", "Reference " + key, UiStringScope.Private, null, DateTime.UtcNow),
                    CancellationToken.None);
            }

            await store.SetAsync(
                new UiStringWrite(Written, "ru", "Тексты интерфейса", UiStringScope.Private, null, DateTime.UtcNow),
                CancellationToken.None);

            var raw = await store.ListRawAsync("ru", CancellationToken.None);
            var english = await store.ListRawAsync("en", CancellationToken.None);

            // Рядків рівно стільки, скільки ключів в еталоні.
            Assert.Equal(english.Count, raw.Count);
            Assert.All(english, row => Assert.Equal(row.Reference, row.Value));

            Assert.Equal("Тексты интерфейса", raw.Single(r => r.Key == Written).Value);

            // ⛔ Ключ без російського рядка — null, а не англійський текст.
            var gap = raw.Single(r => r.Key == Gap);
            Assert.Null(gap.Value);
            Assert.Equal("Reference " + Gap, gap.Reference);

            // Контроль: каталог для екрана ту саму прогалину закриває англійською.
            var screen = await store.GetAsync("ru", CancellationToken.None);
            Assert.Equal(gap.Reference, screen.Strings[Gap]);
        }
        finally
        {
            await using var connection = new SqlConnection(sql.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM sys_ecr.UiString WHERE [Key] IN (@written, @gap);";
            command.Parameters.AddWithValue("@written", Written);
            command.Parameters.AddWithValue("@gap", Gap);
            await command.ExecuteNonQueryAsync();
        }
    }
}
