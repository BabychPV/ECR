// tests/Ecr.Infrastructure.Tests/Persistence/D463OnlineIndexMigrationTests.cs
using Ecr.Infrastructure.Persistence.Migrations;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ Аудит 31.10 (F4): індекси D-463/D-465 на таблицях, що ростуть (журнал покриття, зв'язки подій), будуються
/// <c>ONLINE = ON</c> там, де він є, і повторний прогін (ідемпотентний скрипт на базі, де індекс уже є) нічого не ламає;
/// гілка Standard/Express будує той самий індекс без <c>ONLINE</c>.
/// </summary>
/// <remarks>
/// Мутації: прибрати <c>INDEXPROPERTY … IS NULL</c> — повтор падає 1913; завжди <c>ONLINE = ON</c> — гілка редакції 4
/// червона за текстом.
/// </remarks>
[Collection("SqlServer")]
public sealed class D463OnlineIndexMigrationTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Індекси_ONLINE_лише_де_є_повтор_не_ламає_гілка_Express_будує_той_самий()
    {
        Assert.Contains("ONLINE = ON", D463RawDataReadIndexes.CreateCoverageIndexSql("3"), StringComparison.Ordinal);
        Assert.Contains("WHEN @d463Edition IN (3, 5, 8)", D463RawDataReadIndexes.CreateCoverageIndexSql("4"), StringComparison.Ordinal);

        var cases = new (string Table, string Index, Func<string, string> Sql)[]
        {
            ("itg.CollectionCoverage", D463RawDataReadIndexes.CoverageIndexName, D463RawDataReadIndexes.CreateCoverageIndexSql),
            ("ext.SourceEventLink", D465SourceEventAttributeValues.MapStartIndexName, D465SourceEventAttributeValues.CreateMapStartIndexSql),
        };

        foreach (var (table, index, create) in cases)
        {
            // Схема фікстури вже має індекс (міграції): повтор — без змін і без помилки.
            Assert.Equal(1, await IndexCountAsync(table, index));
            var definition = await DefinitionAsync(table, index);
            await ExecuteAsync(create(D463RawDataReadIndexes.EngineEditionExpression));
            Assert.Equal(1, await IndexCountAsync(table, index));

            // Гілка Standard/Express (редакція 4): без ONLINE, та сама форма індексу.
            await ExecuteAsync($"DROP INDEX {index} ON {table};");
            await ExecuteAsync(create("4"));
            Assert.Equal(definition, await DefinitionAsync(table, index));

            // Гілка редакції сервера (тут Developer/Enterprise — ONLINE).
            await ExecuteAsync($"DROP INDEX {index} ON {table};");
            await ExecuteAsync(create(D463RawDataReadIndexes.EngineEditionExpression));
            Assert.Equal(definition, await DefinitionAsync(table, index));
        }
    }

    private async Task<int> IndexCountAsync(string table, string index)
        => (int)(await ScalarAsync(
            $"SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'{table}') AND name = N'{index}'"))!;

    /// <summary>Ключ, INCLUDE і фільтр індексу одним рядком.</summary>
    private async Task<string> DefinitionAsync(string table, string index)
        => (string)(await ScalarAsync(
            $"""
             SELECT STRING_AGG(CONCAT(c.name, ':', ic.is_included_column, ':', ic.key_ordinal), ',')
                        WITHIN GROUP (ORDER BY ic.is_included_column, ic.key_ordinal, c.name)
                    + '|' + ISNULL(MAX(i.filter_definition), N'')
               FROM sys.indexes i
               JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
               JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE i.object_id = OBJECT_ID(N'{table}') AND i.name = N'{index}'
             """))!;

    private async Task<object?> ScalarAsync(string statement)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        return await command.ExecuteScalarAsync();
    }

    private async Task ExecuteAsync(string statement)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }
}
