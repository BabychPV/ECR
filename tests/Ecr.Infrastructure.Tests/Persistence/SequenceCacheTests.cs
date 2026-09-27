using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// `WR-09`: три послідовності Id мають <c>CACHE 1000</c>. EF Core 10 не
/// вміє описати кеш у моделі, тож його ставить сирий <c>ALTER SEQUENCE</c> у
/// міграції — і перевірити це можна лише на розгорнутій базі.
/// </summary>
[Collection("SqlServer")]
public sealed class SequenceCacheTests(SqlServerFixture sql)
{
    [Theory]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData("doc", "TableInstanceSeq")]
    [InlineData("doc", "TableRowSeq")]
    [InlineData("calc", "CalculationResultSeq")]
    public async Task Послідовність_має_кеш_1000(string schema, string name)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.is_cached, CAST(s.cache_size AS int)
            FROM sys.sequences s
            WHERE s.schema_id = SCHEMA_ID(@schema) AND s.name = @name
            """;
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@name", name);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"{schema}.{name} відсутня в базі");

        // is_cached = 1 саме по собі нічого не доводить: без CACHE у
        // CREATE SEQUENCE SQL Server кешує за власним розміром і дає
        // cache_size = NULL. Доказ — саме число.
        Assert.True(reader.GetBoolean(0), $"{schema}.{name}: is_cached = 0");
        Assert.False(reader.IsDBNull(1), $"{schema}.{name}: cache_size = NULL (розмір за замовчуванням, не 1000)");
        Assert.Equal(1000, reader.GetInt32(1));
    }
}
