using System.Globalization;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// `@BatchSize` у `arc.usp_ArchiveYear` справді ділить копію на пакети (аудит L10-14).
/// </summary>
/// <remarks>
/// ⚠ Пакет 3 на 10 комірках і 5 рядках: межа пакета падає ПОСЕРЕДИНІ рядка
/// (ключ комірки — пара `TableRowId, ColumnDefId`), тож перевіряється саме
/// двоколонковий keyset, а не лише «пакет = рядок».
///
/// Мутації: (1) у циклі комірок поставити `BREAK` після першого INSERT —
/// звірка 50010, тест червоний; (2) у нижній межі пакета `ColumnDefId &lt;= @hiCol`
/// замінити на `&lt;` — губиться остання комірка кожного пакета, 50010.
/// </remarks>
[Collection("SqlServer")]
public sealed class ArchiveBatchSizeTests(SqlServerFixture sql)
{
    private const int Period = 202702;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Audit", "L10-14")]
    public async Task Малий_пакет_переносить_усі_комірки_рядки_й_екземпляри_без_втрат()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(periodKey: Period, columnCount: 3, rowCount: 5, ct: CancellationToken.None);

        // Числові колонки — друга й третя (перша текстова): 5 × 2 = 10 комірок.
        var value = 1.0000000000000001m;
        foreach (var row in doc.RowIds)
        {
            foreach (var column in doc.ColumnDefIds.Skip(1))
            {
                await ExecuteAsync($"""
                    INSERT INTO doc.CellValue
                        (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
                    VALUES ({Period}, {row}, {column}, {doc.TableDefId},
                            {value.ToString(CultureInfo.InvariantCulture)}, 0, 0);
                    """);
                value += 1m;
            }
        }

        await ExecuteAsync($"""
            UPDATE doc.Project SET Status = 4, ClosedAt = '2020-01-01'
             WHERE Id IN (SELECT DISTINCT ProjectId FROM doc.Period WHERE PeriodKey = {Period});
            """);

        var cells = await CountAsync("doc.CellValue");
        var rows = await CountAsync("doc.TableRow");
        var instances = await CountAsync("doc.TableInstance");
        var sum = await ScalarAsync<decimal>($"SELECT SUM(ValueNumeric) FROM doc.CellValue WHERE PeriodKey = {Period}");
        Assert.True(cells >= 10 && rows >= 5, $"фікстура замала: комірок {cells}, рядків {rows}");

        await ExecuteAsync(
            $"EXEC arc.usp_ArchiveYear @ProjectId = {doc.ProjectId}, "
            + $"@FromPeriodKey = {Period}, @ToPeriodKey = {Period}, @BatchSize = 3;");

        Assert.Equal(0, await CountAsync("doc.CellValue"));
        Assert.Equal(cells, await CountAsync("arc.CellValue"));
        Assert.Equal(rows, await CountAsync("arc.TableRow"));
        Assert.Equal(instances, await CountAsync("arc.TableInstance"));
        Assert.Equal(sum, await ScalarAsync<decimal>($"SELECT SUM(ValueNumeric) FROM arc.CellValue WHERE PeriodKey = {Period}"));

        // Без дублів: кожна пара ключа в архіві рівно один раз.
        Assert.Equal(cells, await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM (SELECT DISTINCT TableRowId, ColumnDefId FROM arc.CellValue WHERE PeriodKey = {Period}) AS d"));

        // Період повертається: спільна база лишається такою, як до тесту.
        await ExecuteAsync(
            $"EXEC arc.usp_RestoreYear @ProjectId = {doc.ProjectId}, @FromPeriodKey = {Period}, @ToPeriodKey = {Period};");
        Assert.Equal(cells, await CountAsync("doc.CellValue"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("NULL")]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Audit", "L10-14")]
    public async Task Непозитивний_пакет_відхиляється_до_будь_якої_зміни(string batchSize)
    {
        var runsBefore = await ScalarAsync<int>("SELECT COUNT(*) FROM itg.ArchiveRun");

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            $"EXEC arc.usp_ArchiveYear @ProjectId = 1, @FromPeriodKey = {Period}, "
            + $"@ToPeriodKey = {Period}, @BatchSize = {batchSize};"));

        Assert.Equal(50015, error.Number);
        Assert.Equal(runsBefore, await ScalarAsync<int>("SELECT COUNT(*) FROM itg.ArchiveRun"));
    }

    private Task<int> CountAsync(string table)
        => ScalarAsync<int>($"SELECT COUNT(*) FROM {table} WHERE PeriodKey = {Period}");

    private async Task ExecuteAsync(string sqlText)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;

        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }
}
