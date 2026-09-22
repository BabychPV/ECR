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
    private static readonly string[] Keys = ["common.save", "common.cancel"];

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
            var before = await store.GetRevisionAsync(CancellationToken.None);

            var after = await store.SetManyAsync(
                [.. Keys.Select(k => new UiStringWrite(k, "kz", "т " + k, UiStringScope.Public, null, at))],
                CancellationToken.None);

            Assert.Equal(before + 1, after);
            Assert.Equal(after, await store.SetManyAsync([], CancellationToken.None));
            Assert.Equal(after, await store.GetRevisionAsync(CancellationToken.None));

            var rows = await store.ListForExportAsync("kz", CancellationToken.None);
            var save = rows.Single(r => r.Key == "common.save");
            Assert.Equal(("т common.save", at), (save.Value, save.ModifiedAt));
            Assert.True(await store.LanguageExistsAsync("kz", CancellationToken.None));
            Assert.False(await store.LanguageExistsAsync("xx", CancellationToken.None));
        }
        finally
        {
            await using var connection = new SqlConnection(sql.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM sys_ecr.UiString WHERE LanguageCode = N'kz' AND [Key] IN (N'common.save', N'common.cancel');";
            await command.ExecuteNonQueryAsync();
        }
    }
}
