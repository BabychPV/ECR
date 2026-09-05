using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Аудит: пакетність, незмінність, партиціонування по <c>ChangedAt</c>.</summary>
[Collection("SqlServer")]
public sealed class AuditTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.21")]
    public async Task Аудит_ста_комірок_пишеться_одним_запитом_а_не_ста()
    {
        var doc = await BuildAsync();
        var changes = Changes(doc, count: 100);

        await using var db = CreateContext([]);

        // ⚠ Рахуємо ПОХОДИ ДО СЕРВЕРА, а не команди EF: AuditWriter пише
        // напряму через SqlCommand, і лічильник EF його не бачить.
        // SqlConnection.RetrieveStatistics() рахує саме те, що потрібно.
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        connection.StatisticsEnabled = true;
        connection.ResetStatistics();

        await new AuditWriter(db).WriteCellChangesAsync(changes, CancellationToken.None);

        var roundtrips = (long)connection.RetrieveStatistics()["ServerRoundtrips"]!;

        // Пакетність тут не оптимізація, а умова бюджету: 300 мс на 100
        // комірок не витримає ста походів до сервера. 100 рядків при чанку 150
        // мають лягти в ОДИН INSERT.
        Assert.Equal(1, roundtrips);
        Assert.Equal(100, await CountAsync(doc.DocumentId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-3.9")]
    public async Task Автором_зміни_є_UserId_а_не_SID()
    {
        var doc = await BuildAsync();

        await using var db = CreateContext([]);
        await new AuditWriter(db).WriteCellChangesAsync(Changes(doc, count: 1), CancellationToken.None);

        // R-A2: у локального користувача SID не існує взагалі, тому автором
        // дії завжди є наш UserId. Стовпця під SID в aud.CellChange немає — і
        // це перевіряється прямо на схемі, а не на домовленості.
        var hasSidColumn = await ScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'aud.CellChange') " +
            "AND name LIKE N'%Sid%'");
        Assert.Equal(0, hasSidColumn);

        var author = await ScalarAsync<int>(
            $"SELECT TOP (1) ChangedByUserId FROM aud.CellChange WHERE DocumentId = {doc.DocumentId}");
        Assert.Equal(42, author);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.9")]
    public async Task Зміна_у_стані_Grace_позначається_як_пізня()
    {
        var doc = await BuildAsync();
        var late = Changes(doc, count: 1, isLateEdit: true);

        await using var db = CreateContext([]);
        await new AuditWriter(db).WriteCellChangesAsync(late, CancellationToken.None);

        // D-70: пізня правка — це не помилка, а факт, який має лишитися в
        // журналі. Без прапорця «звіт подали 3-го, а правили 20-го» не видно.
        var flagged = await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = {doc.DocumentId} AND IsLateEdit = 1");
        Assert.Equal(1, flagged);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Аудит_партиціонується_за_моментом_зміни_а_не_за_звітним_періодом()
    {
        // Ключ партиціонування аудиту — ChangedAt, а не PeriodKey: це різні
        // осі, і плутанина між ними ламає і архівацію аудиту, і його читання.
        var partitionColumn = await ScalarAsync<string>("""
            SELECT c.name
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'aud.CellChange') AND ic.partition_ordinal = 1
            """);

        Assert.Equal("ChangedAt", partitionColumn);

        var scheme = await ScalarAsync<string>("""
            SELECT ds.name
            FROM sys.indexes i
            JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
            WHERE i.object_id = OBJECT_ID(N'aud.CellChange') AND i.index_id = 1
            """);

        Assert.Equal("ps_AuditByMonth", scheme);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.3")]
    public async Task Зміна_за_січень_у_березні_потрапляє_в_березневу_партицію_аудиту()
    {
        var doc = await BuildAsync(periodKey: 202601);

        // Звітний період — січень, момент зміни — березень. Саме той випадок,
        // заради якого аудит партиціонується окремо.
        var march = new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc);
        await using var db = CreateContext([]);
        await new AuditWriter(db).WriteCellChangesAsync(
            Changes(doc, count: 1, changedAt: march), CancellationToken.None);

        var rows = await QueryAsync($"""
            SELECT CAST($PARTITION.pf_AuditByMonth(ChangedAt) AS nvarchar(10))
                   + N'|' + CAST(PeriodKey AS nvarchar(10))
            FROM aud.CellChange
            WHERE DocumentId = {doc.DocumentId} AND ChangedAt = '2026-03-15T12:00:00'
            """);

        var marchPartition = await ScalarAsync<int>(
            "SELECT $PARTITION.pf_AuditByMonth('2026-03-15T12:00:00')");
        var januaryPartition = await ScalarAsync<int>(
            "SELECT $PARTITION.pf_AuditByMonth('2026-01-15T12:00:00')");

        Assert.NotEqual(januaryPartition, marchPartition);
        Assert.Contains($"{marchPartition}|202601", rows);
    }

    private async Task<TestDocument> BuildAsync(int periodKey = 202601)
        => await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(periodKey: periodKey, ct: CancellationToken.None);

    private static List<CellChangeRecord> Changes(
        TestDocument doc, int count, bool isLateEdit = false, DateTime? changedAt = null)
    {
        var at = changedAt ?? new DateTime(2026, 1, 20, 10, 0, 0, DateTimeKind.Utc);
        return [.. Enumerable.Range(0, count).Select(i => new CellChangeRecord(
            at,
            new CellAddress(doc.PeriodKey, doc.RowIds[i % doc.RowIds.Count], doc.ColumnDefIds[1]),
            doc.DocumentId,
            $"R{i}",
            OldValue: null,
            NewValue: i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ChangedByUserId: 42,
            Origin: "UserEdit",
            IsLateEdit: isLateEdit,
            CorrelationId: "test"))];
    }

    private EcrDbContext CreateContext(List<string> executed)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .LogTo(executed.Add, [RelationalEventId.CommandExecuted])
            .Options);

    /// <summary>Скільки записів аудиту в конкретного документа.</summary>
    /// <remarks>
    /// ⚠ Саме за <c>DocumentId</c>, а не за <c>PeriodKey</c>: усі тести класу
    /// працюють в одному періоді, і лічильник за періодом рахував би ще й
    /// чужі рядки — тест «проходив» би або падав залежно від порядку.
    /// </remarks>
    private Task<int> CountAsync(long documentId)
        => ScalarAsync<int>($"SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = {documentId}");

    private async Task<T> ScalarAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task<List<string>> QueryAsync(string query)
    {
        var rows = new List<string>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
