using Ecr.Infrastructure.Localization;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Infrastructure.Tests.Localization;

/// <summary>
/// AN-40 / L9-13: ознаку «у мові є власний переклад» (<c>R-16</c>) рахує база, а не перемикач мови, що тягнув
/// повні каталоги кожної мови.
/// </summary>
/// <remarks>
/// Мутації (лише локально, у гілку не пушились):
/// <list type="bullet">
/// <item>прибрати <c>d.[Key] IS NULL OR</c> — власний ключ, якого немає англійською, перестає рахуватися;</item>
/// <item>прибрати <c>COLLATE Latin1_General_BIN2</c> — правка лише регістру перестає рахуватися;</item>
/// <item>прибрати <c>l.IsActive = 1</c> — вимкнена мова потрапляє в перелік.</item>
/// </list>
/// Кожна червонить <see cref="Мова_з_перекладом_лише_тоді_коли_є_рядок_що_відрізняється_від_мови_за_замовчуванням"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class UiStringTranslatedLanguagesTests(SqlServerFixture sql)
{
    private const string Code = "qt";

    private const string Key = "nav.templates";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Audit", "L9-13")]
    public async Task Мова_з_перекладом_лише_тоді_коли_є_рядок_що_відрізняється_від_мови_за_замовчуванням()
    {
        await CleanupAsync();

        try
        {
            await ExecuteAsync(
                $"INSERT INTO sys_ecr.Language (Code, NameNative, Ordinal, IsDefault, IsActive) VALUES (N'{Code}', N'Test', 999, 0, 1);");

            // Засіяні мови: ru і kz перекладені, мова за замовчуванням — ні (вона й є еталон).
            var seeded = await TranslatedAsync();
            Assert.Contains("ru", seeded);
            Assert.Contains("kz", seeded);
            Assert.DoesNotContain("en", seeded);

            // Мова без жодного рядка — не перекладена.
            Assert.DoesNotContain(Code, seeded);

            // Рядок, побайтово рівний англійському, — не переклад.
            await ExecuteAsync(
                $"INSERT INTO sys_ecr.UiString ([Key], LanguageCode, Value, Scope, ModifiedAt) "
                + $"SELECT [Key], N'{Code}', Value, Scope, SYSUTCDATETIME() FROM sys_ecr.UiString WHERE [Key] = N'{Key}' AND LanguageCode = N'en';");
            Assert.DoesNotContain(Code, await TranslatedAsync());

            // Відрізняється лише регістром — уже переклад (так само порівнював і клієнт).
            await ExecuteAsync(
                $"UPDATE sys_ecr.UiString SET Value = UPPER(Value) WHERE [Key] = N'{Key}' AND LanguageCode = N'{Code}';");
            Assert.Contains(Code, await TranslatedAsync());

            // Власний ключ, якого немає англійською, — теж переклад.
            await ExecuteAsync($"DELETE FROM sys_ecr.UiString WHERE LanguageCode = N'{Code}';");
            await ExecuteAsync(
                $"INSERT INTO sys_ecr.UiString ([Key], LanguageCode, Value, Scope, ModifiedAt) "
                + $"VALUES (N'an40.l913.only.{Code}', N'{Code}', N'x', 1, SYSUTCDATETIME());");
            Assert.Contains(Code, await TranslatedAsync());

            // Вимкнена мова не пропонується зовсім.
            await ExecuteAsync($"UPDATE sys_ecr.Language SET IsActive = 0 WHERE Code = N'{Code}';");
            Assert.DoesNotContain(Code, await TranslatedAsync());
        }
        finally
        {
            await CleanupAsync();
        }
    }

    private async Task<IReadOnlySet<string>> TranslatedAsync()
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var store = new UiStringCatalogStore(db, memory);

        var result = await store.ListTranslatedLanguagesAsync(CancellationToken.None);

        Assert.NotNull(result);
        return result;
    }

    private Task CleanupAsync()
        => ExecuteAsync(
            $"DELETE FROM sys_ecr.UiString WHERE LanguageCode = N'{Code}'; DELETE FROM sys_ecr.Language WHERE Code = N'{Code}';");

    private async Task ExecuteAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Текст збирається з констант тесту.
        command.CommandText = text;
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync();
    }
}
