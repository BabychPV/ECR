using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Запис знахідок нічної перевірки узгодженості на реальному SQL Server
/// (<c>Q-169</c>).
/// </summary>
/// <remarks>
/// ⚠ Окремий файл від `ConsistencyCheckJobTests`: той клас перевіряє ТЕКСТ
/// джерела (виявлення правил), а не поведінку запису — саме тому N+1 у
/// `WriteIssuesAsync` не був помічений жодним існуючим тестом.
/// </remarks>
[Collection("SqlServer")]
public sealed class ConsistencyCheckJobWriteTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.7")]
    public async Task Кілька_знахідок_записуються_одним_запитом()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowCount: 2, ct: CancellationToken.None);

        // Дві осиротілі комірки: посилання на записи довідника, яких немає.
        await InsertOrphanCellAsync(doc, doc.RowIds[0], registryEntryId: -900_001);
        await InsertOrphanCellAsync(doc, doc.RowIds[1], registryEntryId: -900_002);

        var executed = new List<string>();
        await using var counting = CreateCountingContext(executed);
        var job = new ConsistencyCheckJob(
            counting, Substitute.For<IOrphanScanner>(), new TestClock(new DateTime(2026, 2, 1, 3, 0, 0, DateTimeKind.Utc)));

        await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);

        // ⛔ Q-169: дві знахідки — а команд, що торкаються
        // `aud.ConsistencyIssue`, рівно ОДНА (пакетний MERGE), не дві
        // (`IF NOT EXISTS...INSERT` на кожну).
        var writes = executed.Count(cmd => cmd.Contains("ConsistencyIssue", StringComparison.Ordinal));
        Assert.Equal(1, writes);

        // ⚠ Рахуємо ЛИШЕ рядки, які вставив цей тест: `aud.ConsistencyIssue`
        // спільний для всієї колекції SqlServer, і глобальний COUNT за
        // RuleCode збігався б із будь-якою іншою знахідкою ORPHANED_CELL з
        // паралельного тесту (`ConsistencyCheckJobDetectionTests`, Q-184).
        var count = await CountIssuesAsync("ORPHANED_CELL", doc.RowIds, CancellationToken.None);
        Assert.Equal(2, count);
    }

    private async Task InsertOrphanCellAsync(TestDocument doc, long rowId, long registryEntryId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT doc.CellValue
                (PeriodKey, TableRowId, TableDefId, ColumnDefId, ValueRegistryEntryId, IsCalculated, IsEmpty)
            VALUES (@p, @r, @t, @c, @reg, 0, 0);
            """;
        command.Parameters.AddWithValue("@p", doc.PeriodKey.Value);
        command.Parameters.AddWithValue("@r", rowId);
        command.Parameters.AddWithValue("@t", doc.TableDefId);
        command.Parameters.AddWithValue("@c", doc.ColumnDefIds[0]);
        command.Parameters.AddWithValue("@reg", registryEntryId);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private async Task<int> CountIssuesAsync(string ruleCode, IReadOnlyList<long> entityIds, CancellationToken ct)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM aud.ConsistencyIssue WHERE RuleCode = @rule AND EntityId IN ("
            + string.Join(',', entityIds) + ")";
        command.Parameters.AddWithValue("@rule", ruleCode);
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Контекст, який складає кожну виконану команду в список.</summary>
    private EcrDbContext CreateCountingContext(List<string> executed)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .LogTo(executed.Add, [RelationalEventId.CommandExecuted])
            .Options);
}
