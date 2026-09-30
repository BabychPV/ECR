// tests/Ecr.Infrastructure.Tests/Jobs/JobQueueSchemaTests.cs
using Ecr.Domain.Entities.Integration;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Міграція <c>MI02JobQueue</c> (MI-02, <c>D-208</c>): колонки черги в
/// <c>itg.JobProgress</c>, <c>CK_JobProgress_QueueShape</c> і три індекси черги.
/// </summary>
/// <remarks>
/// ⚠ Рядки пишуться СИРИМ SQL, а не через EF: черга (F1b) теж пише сирим SQL,
/// і саме цей шлях має впиратися в індекси й CHECK. Кожен рядок має власний
/// <c>JobId</c>/<c>TargetKey</c> з GUID — база спільна для колекції.
/// <para>Мутаційні докази — в описі коміту.</para>
/// </remarks>
[Collection("SqlServer")]
public sealed class JobQueueSchemaTests(SqlServerFixture sql)
{
    private const string Prefix = "mi02-";

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData("Lane", "varchar", 32, null)]
    [InlineData("Payload", "nvarchar", -1, null)]
    [InlineData("AvailableAt", "datetime2", null, 3)]
    [InlineData("LeaseUntil", "datetime2", null, 3)]
    [InlineData("CancelRequestedAt", "datetime2", null, 3)]
    [InlineData("ClaimToken", "uniqueidentifier", null, null)]
    [InlineData("TargetKey", "nvarchar", 200, null)]
    [InlineData("ReclaimCount", "int", null, null)]
    public async Task Колонка_черги_є_з_типом_і_nullable(string column, string type, int? length, int? precision)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DATA_TYPE, CHARACTER_MAXIMUM_LENGTH,
                   CASE WHEN DATA_TYPE = 'datetime2' THEN DATETIME_PRECISION END, IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'itg' AND TABLE_NAME = 'JobProgress' AND COLUMN_NAME = @column
            """;
        command.Parameters.AddWithValue("@column", column);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"немає колонки itg.JobProgress.{column}");
        Assert.Equal(
            (type, length, precision, "YES"),
            (reader.GetString(0),
             reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1),
             reader.IsDBNull(2) ? (int?)null : reader.GetInt16(2),
             reader.GetString(3)));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData("UX_JobProgress_Target_Queued", "TargetKey", "", true, "([State]='Queued' AND [TargetKey] IS NOT NULL)")]
    [InlineData("UX_JobProgress_Target_Running", "TargetKey", "", true, "([State]='Running' AND [TargetKey] IS NOT NULL)")]
    [InlineData(
        "IX_JobProgress_Claim", "Lane,State,AvailableAt", "Attempt,LeaseUntil,ReclaimCount,TargetKey", false,
        "([Lane] IS NOT NULL AND ([State] IN ('Queued', 'Running')))")]
    public async Task Індекс_черги_має_ключ_INCLUDE_унікальність_і_фільтр(
        string index, string keys, string includes, bool unique, string filter)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
                 FROM sys.index_columns ic
                 JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                 WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0),
                ISNULL((SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY c.name)
                 FROM sys.index_columns ic
                 JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                 WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1), N''),
                i.is_unique,
                ISNULL(i.filter_definition, N'')
            FROM sys.indexes i
            WHERE i.object_id = OBJECT_ID(N'itg.JobProgress') AND i.name = @index
            """;
        command.Parameters.AddWithValue("@index", index);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"{index} немає в itg.JobProgress");
        Assert.Equal(
            (keys, includes, unique, filter),
            (reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetString(3)));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData("Queued")]
    [InlineData("Running")]
    public async Task Другий_рядок_того_самого_стану_на_ту_саму_ціль_база_не_пускає(string state)
    {
        var target = Target();
        var ids = new List<string>();

        try
        {
            ids.Add(await InsertAsync(state, target));

            var error = await Assert.ThrowsAsync<SqlException>(async () => ids.Add(await InsertAsync(state, target)));

            // 2601 — дубль ключа унікального індексу: саме це claim (F1b) читає
            // як «нічого не взяв», а не як помилку (правка А «Аудиту»).
            Assert.Equal(2601, error.Number);
            Assert.Contains($"UX_JobProgress_Target_{state}", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteAsync(ids);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Один_Running_і_один_Queued_позаду_на_ціль_дозволені_завершені_і_без_цілі_не_обмежені()
    {
        var target = Target();
        var ids = new List<string>();

        try
        {
            // «1 Running + 1 Queued позаду» — рівно те, заради чого індексів два.
            ids.Add(await InsertAsync("Running", target));
            ids.Add(await InsertAsync("Queued", target));

            // Історія на ту саму ціль фільтром не обмежена.
            ids.Add(await InsertAsync("Succeeded", target));
            ids.Add(await InsertAsync("Succeeded", target));
            ids.Add(await InsertAsync("Failed", target));

            // Задачі без цілі (дельти перерахунку до CAL-01) не коалесцюються.
            ids.Add(await InsertAsync("Queued", targetKey: null));
            ids.Add(await InsertAsync("Queued", targetKey: null));
            ids.Add(await InsertAsync("Running", targetKey: null));
            ids.Add(await InsertAsync("Running", targetKey: null));

            Assert.Equal(9, await CountAsync(ids));
        }
        finally
        {
            await DeleteAsync(ids);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Рядок_черги_без_AvailableAt_відкидає_CHECK()
    {
        var ids = new List<string>();

        try
        {
            ids.Add(await InsertAsync("Queued", Target(), lane: "default", availableAt: true));

            var error = await Assert.ThrowsAsync<SqlException>(
                async () => ids.Add(await InsertAsync("Queued", Target(), lane: "default", availableAt: false)));

            Assert.Equal(547, error.Number);
            Assert.Contains("CK_JobProgress_QueueShape", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteAsync(ids);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Рядок_старого_світу_без_лейна_пишеться_як_раніше_і_сирим_SQL_і_через_EF()
    {
        var ids = new List<string>();

        try
        {
            // Дзеркало Quartz: Lane, AvailableAt і решта колонок черги — NULL.
            ids.Add(await InsertAsync("Queued", targetKey: null, lane: null, availableAt: false));

            var viaEf = $"{Prefix}{Guid.NewGuid():N}";
            ids.Add(viaEf);
            await using (var db = sql.CreateContext())
            {
                db.JobProgresses.Add(new JobProgress(viaEf, "Ecr.Test.Mi02Job", new DateTime(2031, 3, 5, 9, 0, 0, DateTimeKind.Utc)));
                await db.SaveChangesAsync();
            }

            await using var check = sql.CreateContext();
            var row = await check.JobProgresses.AsNoTracking().SingleAsync(p => p.JobId == viaEf);
            Assert.All(
                new object?[]
                {
                    row.Lane, row.Payload, row.AvailableAt, row.LeaseUntil, row.ClaimToken, row.TargetKey,
                    row.CancelRequestedAt, row.ReclaimCount,
                },
                Assert.Null);
            Assert.Equal(2, await CountAsync(ids));
        }
        finally
        {
            await DeleteAsync(ids);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Запис_із_QUOTED_IDENTIFIER_OFF_фільтровані_індекси_відкидають()
    {
        // Правка В «Аудиту»: сесія без QUOTED_IDENTIFIER ON (sqlcmd без -I,
        // крок агента SQL) після цієї міграції не пише в itg.JobProgress
        // ВЗАГАЛІ — і це стосується й рядків старого світу. Без пулу: інакше
        // OFF перейшов би до наступного тесту через пул з'єднань.
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString) { Pooling = false };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();

        await using (var off = connection.CreateCommand())
        {
            off.CommandText = "SET QUOTED_IDENTIFIER OFF;";
            await off.ExecuteNonQueryAsync();
        }

        var jobId = $"{Prefix}{Guid.NewGuid():N}";
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO itg.JobProgress (JobId, JobCode, State, StartedAt, UpdatedAt)
            VALUES (@id, 'Ecr.Test.Mi02Job', 'Queued', SYSUTCDATETIME(), SYSUTCDATETIME());
            """;
        insert.Parameters.AddWithValue("@id", jobId);

        var error = await Assert.ThrowsAsync<SqlException>(() => insert.ExecuteNonQueryAsync());

        // 1934 — «INSERT failed because the following SET options have incorrect settings».
        Assert.Equal(1934, error.Number);
        Assert.Equal(0, await CountAsync([jobId]));
    }

    private static string Target() => $"Ecr.Test.Mi02Job~{Guid.NewGuid():N}";

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task<string> InsertAsync(
        string state, string? targetKey, string? lane = "default", bool availableAt = true)
    {
        var jobId = $"{Prefix}{Guid.NewGuid():N}";

        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();

        // ⚠ SET-опції явно — як у кожному сирому SQL черги (правка В).
        command.CommandText = """
            SET QUOTED_IDENTIFIER ON;
            SET ANSI_NULLS ON;
            INSERT INTO itg.JobProgress (JobId, JobCode, State, StartedAt, UpdatedAt, Lane, AvailableAt, TargetKey, Payload)
            VALUES (@id, 'Ecr.Test.Mi02Job', @state, SYSUTCDATETIME(), SYSUTCDATETIME(), @lane,
                    CASE WHEN @available = 1 THEN SYSUTCDATETIME() END, @target,
                    CASE WHEN @lane IS NOT NULL THEN N'{}' END);
            """;
        command.Parameters.AddWithValue("@id", jobId);
        command.Parameters.AddWithValue("@state", state);
        command.Parameters.AddWithValue("@lane", (object?)lane ?? DBNull.Value);
        command.Parameters.AddWithValue("@available", availableAt);
        command.Parameters.AddWithValue("@target", (object?)targetKey ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();

        return jobId;
    }

    private async Task<int> CountAsync(IReadOnlyCollection<string> ids)
    {
        await using var db = sql.CreateContext();
        return await db.JobProgresses.CountAsync(p => ids.Contains(p.JobId));
    }

    private async Task DeleteAsync(IReadOnlyCollection<string> ids)
    {
        await using var db = sql.CreateContext();
        await db.JobProgresses.Where(p => ids.Contains(p.JobId)).ExecuteDeleteAsync();
    }
}
