// tests/Ecr.Infrastructure.Tests/Persistence/CalculationResultTableColumnTests.cs
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// D-401: <c>TableInstanceId bigint NULL</c> у <c>calc.CalculationResult/Input/Step</c>; CRY01: ключ
/// <c>IX_CalculationResult_DocRun</c> — <c>(PeriodKey, DocumentId, CalculationRunId, Kind, TableInstanceId)</c> на
/// <c>ps_ByPeriodKey</c>; перебудова індексу на наявній базі (і до D-401, і з INCLUDE D-401) ідемпотентна.
/// </summary>
/// <remarks>
/// ⛔ Мутаційний доказ: повернути <c>TableInstanceId</c> в INCLUDE у <c>07-partition-tables.sql</c> (обидва місця) —
/// червоніє <see cref="DocRun_веде_Kind_і_TableInstanceId_у_ключі_на_схемі_партиціонування"/>; прибрати блок
/// «CRY01 … для наявних баз» — червоніє <see cref="Перебудова_індексу_старої_бази_дає_ключ_CRY01_і_повтор_не_змінює_нічого"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationResultTableColumnTests(SqlServerFixture sql)
{
    private const string DocRun = "IX_CalculationResult_DocRun";

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData("calc.CalculationResult")]
    [InlineData("calc.CalculationInput")]
    [InlineData("calc.CalculationStep")]
    public async Task Колонка_TableInstanceId_є_bigint_NULL_без_значення_за_замовчуванням(string table)
    {
        var column = await ScalarAsync($"""
            SELECT CONCAT(TYPE_NAME(c.user_type_id), '|', c.is_nullable, '|', ISNULL(CAST(c.default_object_id AS varchar(20)), ''))
            FROM sys.columns AS c WHERE c.object_id = OBJECT_ID(N'{table}') AND c.name = N'TableInstanceId';
            """);

        Assert.Equal("bigint|1|0", column);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task DocRun_веде_Kind_і_TableInstanceId_у_ключі_на_схемі_партиціонування()
    {
        Assert.Equal(CurrentShape, await DocRunStateAsync());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData("SourceRowKey, Value, UnitId, SubstanceEntryId")]
    [InlineData("SourceRowKey, Value, UnitId, SubstanceEntryId, TableInstanceId")]
    public async Task Перебудова_індексу_старої_бази_дає_ключ_CRY01_і_повтор_не_змінює_нічого(string includeTail)
    {
        var block = ReadUpgradeBlock();

        try
        {
            // Стара база: до D-401 (без TableInstanceId) або D-401 (TableInstanceId в INCLUDE), на тій самій схемі.
            await ExecuteAsync($"""
                CREATE NONCLUSTERED INDEX {DocRun}
                    ON calc.CalculationResult (PeriodKey, DocumentId, CalculationRunId)
                    INCLUDE (MethodologyVersionId, OutputCode, {includeTail})
                    WITH (DROP_EXISTING = ON) ON ps_ByPeriodKey (PeriodKey);
                """);
            Assert.NotEqual(CurrentShape, await DocRunStateAsync());

            await ExecuteAsync(block);
            Assert.Equal(CurrentShape, await DocRunStateAsync());

            // Повторний прохід — ключ уже CRY01, індекс не перебудовується.
            var before = await ScalarAsync(IndexIdentityQuery);
            await ExecuteAsync(block);
            Assert.Equal(CurrentShape, await DocRunStateAsync());
            Assert.Equal(before, await ScalarAsync(IndexIdentityQuery));
        }
        finally
        {
            await ExecuteAsync(block);
        }
    }

    /// <summary>Форма CRY01: ключ за порядком | INCLUDE за порядком | схема розміщення.</summary>
    private const string CurrentShape =
        "PeriodKey,DocumentId,CalculationRunId,Kind,TableInstanceId"
        + "|MethodologyVersionId,OutputCode,SourceRowKey,Value,UnitId,SubstanceEntryId"
        + "|ps_ByPeriodKey";

    // Ідентичність індексу: кількість рядків sys.partitions і найбільший partition_id (перебудова створює нові).
    private const string IndexIdentityQuery = $"""
        SELECT CONCAT(COUNT(*), '|', MAX(p.hobt_id))
        FROM sys.partitions AS p JOIN sys.indexes AS i ON i.object_id = p.object_id AND i.index_id = p.index_id
        WHERE i.object_id = OBJECT_ID(N'calc.CalculationResult') AND i.name = N'{DocRun}';
        """;

    private static string ReadUpgradeBlock()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Persistence", "Sql", "07-partition-tables.sql");
        var batch = SqlBatches.Split(File.ReadAllText(path))
            .Single(b => b.Contains("DROP_EXISTING = ON, SORT_IN_TEMPDB = ON", StringComparison.Ordinal));
        return batch;
    }

    private Task<string> DocRunStateAsync() => ScalarAsync($"""
        SELECT CONCAT(
            (SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal) FROM sys.index_columns AS ic
               JOIN sys.indexes AS i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
               JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE i.object_id = OBJECT_ID(N'calc.CalculationResult') AND i.name = N'{DocRun}'
                AND ic.is_included_column = 0),
            '|',
            (SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.index_column_id) FROM sys.index_columns AS ic
               JOIN sys.indexes AS i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
               JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE i.object_id = OBJECT_ID(N'calc.CalculationResult') AND i.name = N'{DocRun}'
                AND ic.is_included_column = 1),
            '|',
            (SELECT ds.name FROM sys.indexes AS i JOIN sys.data_spaces AS ds ON ds.data_space_id = i.data_space_id
              WHERE i.object_id = OBJECT_ID(N'calc.CalculationResult') AND i.name = N'{DocRun}'));
        """);

    private async Task<string> ScalarAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(true);
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        return Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(true), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private async Task ExecuteAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(true);
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync().ConfigureAwait(true);
    }
}
