// tests/Ecr.Infrastructure.Tests/Persistence/B18HotPathIndexesOnlineTests.cs
using System.Globalization;
using Ecr.Infrastructure.Persistence.Migrations;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Індекси B18 будуються <c>ONLINE</c> лише там, де він є (аудит L10-16).
/// </summary>
/// <remarks>
/// ⚠ Справжньої Standard тут немає (інстанс — Developer, <c>EngineEdition = 3</c>), тож
/// редакція підставляється літералом у <see cref="B18HotPathIndexes.IndexesSql"/>, і обидві
/// гілки виконуються наживо: індекси знімаються й ставляться знову, а надруковані
/// команди показують, з <c>ONLINE</c> чи без. Відмову 1712 справжньої Standard тест не
/// відтворює — лише те, що на «2» і «4» команда її не викликала б.
///
/// Мутація: умову <c>IN (3, 5, 8)</c> замінити на <c>&gt;= 2</c> — червоне на «2» і «4»;
/// прибрати <c>@b18Online</c> з команд — червоне на «3», «5», «8».
/// </remarks>
[Collection("SqlServer")]
public sealed class B18HotPathIndexesOnlineTests(SqlServerFixture sql)
{
    private static readonly (string Table, string Index)[] Indexes =
    [
        ("doc.TableRow", "IX_TableRow_Live"),
        ("doc.CellValue", "IX_CellValue_RegistryEntry"),
        ("doc.CellValue", "IX_CellValue_Unit"),
        ("cfg.ColumnDef", "IX_ColumnDef_LookupRegistryDefId"),
        ("cfg.ColumnDef", "IX_ColumnDef_UnitId"),
    ];

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(8, true)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Audit", "L10-16")]
    public async Task Індекси_B18_ONLINE_лише_на_Enterprise_і_Azure_і_лягають_поруч_із_кластерним(
        int engineEdition, bool online)
    {
        var edition = engineEdition.ToString(CultureInfo.InvariantCulture);

        try
        {
            await DropAllAsync();

            // ⚠ Editions 5 і 8 (Azure) на Developer виконуються наживо так само, як 3:
            // ONLINE тут є. На «2» і «4» — офлайн-гілка, теж наживо.
            var printed = await ExecuteCollectingPrintAsync(B18HotPathIndexes.IndexesSql(edition));

            Assert.Equal(Indexes.Length, printed.Count);
            Assert.All(printed, statement =>
                Assert.Equal(online, statement.Contains("ONLINE = ON", StringComparison.Ordinal)));

            foreach (var (table, index) in Indexes)
            {
                // Той самий простір даних, що в кластерного індексу таблиці: на
                // партиційованій базі doc.* — схема партиціонування.
                Assert.Equal(1, await ScalarAsync<int>($"""
                    SELECT COUNT(*)
                    FROM sys.indexes AS ix
                    JOIN sys.indexes AS cl ON cl.object_id = ix.object_id AND cl.index_id IN (0, 1)
                    WHERE ix.object_id = OBJECT_ID(N'{table}') AND ix.name = N'{index}'
                      AND ix.data_space_id = cl.data_space_id
                    """));
            }

            // Повтор нічого не перебудовує: усі індекси вже на місці.
            Assert.Empty(await ExecuteCollectingPrintAsync(B18HotPathIndexes.IndexesSql(edition)));
        }
        finally
        {
            // Спільна база лишається з індексами, хоч би що сталося вище.
            await ExecuteCollectingPrintAsync(
                B18HotPathIndexes.IndexesSql(B18HotPathIndexes.EngineEditionExpression));
        }
    }

    private async Task DropAllAsync()
    {
        foreach (var (table, index) in Indexes)
        {
            await ExecuteCollectingPrintAsync($"""
                IF INDEXPROPERTY(OBJECT_ID(N'{table}'), N'{index}', 'IndexID') IS NOT NULL
                    DROP INDEX [{index}] ON {table};
                """);
        }
    }

    private async Task<List<string>> ExecuteCollectingPrintAsync(string text)
    {
        var printed = new List<string>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        // ⚠ Одна подія може нести кілька PRINT — кожен окремим елементом Errors.
        connection.InfoMessage += (_, e) => printed.AddRange(e.Errors.Cast<SqlError>().Select(m => m.Message));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync();
        return printed;
    }

    private async Task<T> ScalarAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        var result = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result!, typeof(T), CultureInfo.InvariantCulture);
    }
}
