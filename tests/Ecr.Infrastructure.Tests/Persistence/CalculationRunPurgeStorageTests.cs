// tests/Ecr.Infrastructure.Tests/Persistence/CalculationRunPurgeStorageTests.cs
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Окрема база для перевірки оновлення: тест перебудовує індекси й стиснення таблиць <c>calc.*</c>.</summary>
public sealed class CalcStorageUpgradeDatabase : IAsyncLifetime
{
    private readonly SqlServerFixture database = SqlServerFixture.WithOwnDatabase("_l3upg");

    /// <summary>Рядок підключення до окремої бази.</summary>
    public string ConnectionString => database.ConnectionString;

    /// <inheritdoc />
    public async Task InitializeAsync() => await database.InitializeAsync();

    /// <inheritdoc />
    public async Task DisposeAsync() => await database.DisposeAsync();
}

/// <summary>
/// <c>D-560</c> / PSS-01: фізична модель під прибирання прогонів — індекси з провідним <c>CalculationRunId</c> на
/// <c>calc.CalculationResult/Step/Input</c> (вирівняні, PAGE) і PAGE-стиснення порожніх партицій
/// (<c>07-partition-tables.sql</c>); шлях оновлення наявної бази не чіпає непорожніх партицій і ідемпотентний.
/// </summary>
/// <remarks>
/// Мутації: прибрати блок «індекси ЗА ПРОГОНОМ» з <c>07</c> — червоніють обидва тести; прибрати блок стиснення —
/// червоніє «порожні партиції стиснено»; стиснути й непорожні партиції — червоніє «непорожня лишилась NONE»;
/// перебудовувати індекс на повторному прогоні — червоніє ідентичність <c>hobt_id</c>.
/// </remarks>
public sealed class CalculationRunPurgeStorageTests(CalcStorageUpgradeDatabase database)
    : IClassFixture<CalcStorageUpgradeDatabase>
{
    private static readonly (string Table, string Index)[] RunIndexes =
    [
        ("calc.CalculationResult", "IX_CalculationResult_Run"),
        ("calc.CalculationStep", "IX_CalculationStep_Run"),
        ("calc.CalculationInput", "IX_CalculationInput_Run"),
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Нова_база_має_індекси_за_прогоном_і_стиснені_таблиці_розрахунку()
    {
        foreach (var (table, index) in RunIndexes)
        {
            Assert.Equal("CalculationRunId|ps_ByPeriodKey|PAGE", await IndexShapeAsync(table, index));
        }

        // Нова база: усі партиції всіх індексів трьох таблиць - PAGE.
        Assert.Equal(0, await ScalarIntAsync("""
            SELECT COUNT(*) FROM sys.partitions AS p
             WHERE p.object_id IN (OBJECT_ID(N'calc.CalculationResult'), OBJECT_ID(N'calc.CalculationStep'),
                                   OBJECT_ID(N'calc.CalculationInput'))
               AND p.data_compression_desc <> N'PAGE';
            """));
    }

    /// <summary>
    /// Наявна база до D-560: без індексів за прогоном, таблиці нестиснені, у партиції 202603 є дані. Скрипт будує
    /// індекси, стискає ЛИШЕ порожні партиції (непорожня лишається NONE — крок DBA), даних не губить; повтор нічого
    /// не перебудовує.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Оновлення_будує_індекси_стискає_лише_порожні_партиції_і_ідемпотентне()
    {
        var stand = await PurgeKit.ArrangeAsync(database.ConnectionString, 202603);
        await using (var db = stand.Builder.CreateContext())
        {
            var run = await PurgeKit.AddRunAsync(db, stand, "Current", new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc));
            await PurgeKit.InsertRowsAsync(db, stand, run, 30, 30, 30, "U");
        }

        // Стан «до D-560».
        foreach (var (table, index) in RunIndexes)
        {
            await ExecuteAsync($"DROP INDEX {index} ON {table}; ALTER INDEX ALL ON {table} REBUILD WITH (DATA_COMPRESSION = NONE);");
        }

        try
        {
            await UpgradeAndRepeatAsync();
        }
        finally
        {
            // Стан «нова база» для сусіднього тесту класу (порядок тестів не гарантований).
            foreach (var (table, _) in RunIndexes)
            {
                await ExecuteAsync($"ALTER INDEX ALL ON {table} REBUILD WITH (DATA_COMPRESSION = PAGE);");
            }
        }
    }

    private async Task UpgradeAndRepeatAsync()
    {
        var rowsBefore = await RowsAsync();
        var populated = await ScalarIntAsync("SELECT $PARTITION.pf_ByPeriodKey(202603);");

        await RunScript07Async();

        foreach (var (table, index) in RunIndexes)
        {
            Assert.Equal("CalculationRunId|ps_ByPeriodKey|PAGE", await IndexShapeAsync(table, index));
        }

        // Непорожня партиція кроків не перебудовувалась; порожні - стиснено всі.
        Assert.Equal("NONE", await CompressionAsync("calc.CalculationStep", 1, populated));
        Assert.Equal(0, await ScalarIntAsync($"""
            SELECT COUNT(*) FROM sys.partitions AS p
             WHERE p.object_id IN (OBJECT_ID(N'calc.CalculationResult'), OBJECT_ID(N'calc.CalculationStep'),
                                   OBJECT_ID(N'calc.CalculationInput'))
               AND p.partition_number <> {populated}
               AND p.data_compression_desc <> N'PAGE';
            """));
        Assert.Equal(rowsBefore, await RowsAsync());

        // Повтор: індекси не перебудовуються, нічого не падає.
        var identity = await ScalarStringAsync("""
            SELECT STRING_AGG(CONVERT(nvarchar(max), CONCAT(i.name, ':', p.partition_number, ':', p.hobt_id)), ',')
                   WITHIN GROUP (ORDER BY i.name, p.partition_number)
              FROM sys.indexes AS i JOIN sys.partitions AS p ON p.object_id = i.object_id AND p.index_id = i.index_id
             WHERE i.name IN (N'IX_CalculationResult_Run', N'IX_CalculationStep_Run', N'IX_CalculationInput_Run');
            """);
        await RunScript07Async();
        Assert.Equal(identity, await ScalarStringAsync("""
            SELECT STRING_AGG(CONVERT(nvarchar(max), CONCAT(i.name, ':', p.partition_number, ':', p.hobt_id)), ',')
                   WITHIN GROUP (ORDER BY i.name, p.partition_number)
              FROM sys.indexes AS i JOIN sys.partitions AS p ON p.object_id = i.object_id AND p.index_id = i.index_id
             WHERE i.name IN (N'IX_CalculationResult_Run', N'IX_CalculationStep_Run', N'IX_CalculationInput_Run');
            """));
        Assert.Equal(rowsBefore, await RowsAsync());
    }

    private Task<string> RowsAsync() => ScalarStringAsync("""
        SELECT CONCAT((SELECT COUNT_BIG(*) FROM calc.CalculationResult), '|',
                      (SELECT COUNT_BIG(*) FROM calc.CalculationStep), '|',
                      (SELECT COUNT_BIG(*) FROM calc.CalculationInput));
        """);

    private Task<string> IndexShapeAsync(string table, string index) => ScalarStringAsync($"""
        SELECT CONCAT(
            (SELECT STRING_AGG(c.name COLLATE DATABASE_DEFAULT, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
               FROM sys.index_columns AS ic
               JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0),
            '|', ds.name COLLATE DATABASE_DEFAULT, '|',
            (SELECT MIN(p.data_compression_desc COLLATE DATABASE_DEFAULT) FROM sys.partitions AS p
              WHERE p.object_id = i.object_id AND p.index_id = i.index_id))
        FROM sys.indexes AS i JOIN sys.data_spaces AS ds ON ds.data_space_id = i.data_space_id
        WHERE i.object_id = OBJECT_ID(N'{table}') AND i.name = N'{index}';
        """);

    private Task<string> CompressionAsync(string table, int indexId, int partition) => ScalarStringAsync($"""
        SELECT data_compression_desc FROM sys.partitions
         WHERE object_id = OBJECT_ID(N'{table}') AND index_id = {indexId} AND partition_number = {partition};
        """);

    /// <summary>Увесь <c>07-partition-tables.sql</c> на одному з'єднанні, батч за батчем (як <c>sqlcmd</c>).</summary>
    private async Task RunScript07Async()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Persistence", "Sql", "07-partition-tables.sql");
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        foreach (var batch in SqlBatches.Split(await File.ReadAllTextAsync(path)))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            command.CommandTimeout = 300;
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task ExecuteAsync(string text)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> ScalarIntAsync(string text)
        => Convert.ToInt32(await ScalarAsync(text), System.Globalization.CultureInfo.InvariantCulture);

    private async Task<string> ScalarStringAsync(string text)
        => Convert.ToString(await ScalarAsync(text), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    private async Task<object?> ScalarAsync(string text)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        return await command.ExecuteScalarAsync();
    }
}
