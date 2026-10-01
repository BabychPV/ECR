// tests/Ecr.Infrastructure.Tests/Persistence/JobProgressHotQueriesScanTests.cs
using System.Globalization;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Вимір 2026-10-01 (docs/build/perf/jobs-stale-health-2026-10-01.md): гарячі читання
/// <c>itg.JobProgress</c> не ростуть від ЧУЖИХ рядків — <c>GetFanOutAsync</c> (опитування статусу
/// задачі) і <c>CountSucceededWithMessageKeyAsync</c> (анонімний <c>/health/ready</c>).
/// </summary>
/// <remarks>
/// ⛔ Доказ — <c>sys.dm_exec_sessions.logical_reads</c> до й після 40 000 чужих рядків.
/// Мутаційний доказ: прибрати <c>IX_JobProgress_FanOutParent</c> (або повернути
/// <c>JSON_VALUE(Payload…)</c> у предикат) чи <c>IX_JobProgress_State_UpdatedAt</c> — відповідний
/// тест червоніє (читання ростуть на сотні сторінок).
/// </remarks>
[Collection("SqlServer")]
public sealed class JobProgressHotQueriesScanTests(SqlServerFixture sql)
{
    private const int ForeignRows = 8_000;
    private const int Slack = 8;

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Статус_розкладу_не_читає_чужих_рядків_журналу()
    {
        var parent = $"parent-{_tag}";
        await ExecAsync($$"""
            INSERT itg.JobProgress (JobId, JobCode, [Percent], State, StartedAt, UpdatedAt)
            VALUES (N'{{parent}}', N'recalc', 100, N'Succeeded', SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT itg.JobProgress (JobId, JobCode, [Percent], State, StartedAt, UpdatedAt, Lane, AvailableAt, TargetKey, Payload)
            SELECT N'child-{{_tag}}-' + CAST(v.n AS nvarchar(5)), N'recalc', 100, v.s, SYSUTCDATETIME(), SYSUTCDATETIME(),
                   'recalc', SYSUTCDATETIME(), N'IRecalculationJob~doc' + CAST(v.n AS nvarchar(5)) + N'~{{_tag}}',
                   N'{"fanOutParentJobId":"{{parent}}"}'
            FROM (VALUES (1, N'Succeeded'), (2, N'Succeeded'), (3, N'Failed')) AS v(n, s);
            """);

        async Task<(long Reads, int Total, int Succeeded, int Failed)> MeasureAsync()
        {
            long reads = 0;
            Ecr.Application.Ports.FanOutStatus? status = null;
            reads = await ReadsAsync(async db => status = await new JobProgressStore(db)
                .GetFanOutAsync(parent, CancellationToken.None));
            Assert.NotNull(status);
            return (reads, status!.Total, status.Succeeded, status.Failed);
        }

        var before = await MeasureAsync();
        Assert.Equal((3, 2, 1), (before.Total, before.Succeeded, before.Failed));

        // Чужі рядки: інші батьки + рядки без Payload (історія звичайних задач).
        await ExecAsync($$"""
            INSERT itg.JobProgress (JobId, JobCode, [Percent], State, StartedAt, UpdatedAt, Lane, AvailableAt, TargetKey, Payload)
            SELECT TOP ({{ForeignRows}}) N'foreign-{{_tag}}-' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS nvarchar(10)),
                   N'recalc', 100, N'Succeeded', DATEADD(DAY, -2, SYSUTCDATETIME()), DATEADD(DAY, -2, SYSUTCDATETIME()),
                   'recalc', SYSUTCDATETIME(),
                   N'IRecalculationJob~doc9~f{{_tag}}' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS nvarchar(10)),
                   N'{"fanOutParentJobId":"other-' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT 1)) % 500 AS nvarchar(5)) + N'"}'
            FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b
            """);

        await MeasureAsync(); // розігрів: оновлення статистики після масової вставки
        var after = await MeasureAsync();

        Assert.Equal((3, 2, 1), (after.Total, after.Succeeded, after.Failed));
        Assert.True(
            after.Reads <= before.Reads + Slack,
            $"Статус розкладу: читань {before.Reads} -> {after.Reads} після {ForeignRows} чужих рядків.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Лічильник_готовності_за_ключем_не_читає_чужих_рядків_журналу()
    {
        var key = $"job.recalc.overBudget.{_tag}";
        var since = DateTime.UtcNow.AddDays(-1);

        await ExecAsync($$"""
            INSERT itg.JobProgress (JobId, JobCode, [Percent], Message, State, StartedAt, UpdatedAt)
            VALUES (N'own-{{_tag}}', N'recalc', 100, N'{"key":"{{key}}"}', N'Succeeded', SYSUTCDATETIME(), SYSUTCDATETIME());
            """);

        async Task<(long Reads, int Count)> MeasureAsync()
        {
            var count = 0;
            var reads = await ReadsAsync(async db => count = await new JobProgressStore(db)
                .CountSucceededWithMessageKeyAsync(key, since, CancellationToken.None));
            return (reads, count);
        }

        // ⛔ Порівняння «до/після» саме по собі хибнозелене: «до» — це скан усієї СПІЛЬНОЇ тестової
        // бази (сотні сторінок чужого шуму), тож без індексу after ≈ before. Тому доказ — ПЛАН:
        // запит мусить шукати (seek) за IX_JobProgress_State_UpdatedAt. Без індексу seeks не зростає.
        var seeksBefore = await IndexSeeksAsync("IX_JobProgress_State_UpdatedAt");
        var before = await MeasureAsync();
        Assert.Equal(1, before.Count);
        var seeksAfter = await IndexSeeksAsync("IX_JobProgress_State_UpdatedAt");
        Assert.True(
            seeksAfter > seeksBefore,
            $"Лічильник готовності не шукає за IX_JobProgress_State_UpdatedAt (seeks {seeksBefore} -> {seeksAfter}): індекс прибрано чи план скан.");

        // Чужа історія за межами вікна (30 діб зберігання): доба не має її читати.
        await ExecAsync($$"""
            INSERT itg.JobProgress (JobId, JobCode, [Percent], Message, State, StartedAt, UpdatedAt)
            SELECT TOP ({{ForeignRows}}) N'hist-{{_tag}}-' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS nvarchar(10)),
                   N'recalc', 100, N'{"key":"job.done","args":null}', N'Succeeded',
                   DATEADD(DAY, -10, SYSUTCDATETIME()), DATEADD(DAY, -10, SYSUTCDATETIME())
            FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b
            """);

        await MeasureAsync();
        var after = await MeasureAsync();

        Assert.Equal(1, after.Count);
        Assert.True(
            after.Reads <= before.Reads + Slack,
            $"Лічильник готовності: читань {before.Reads} -> {after.Reads} після {ForeignRows} давніх рядків.");
    }

    private async Task ExecAsync(string commandText)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> IndexSeeksAsync(string indexName)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var q = connection.CreateCommand();
        q.CommandText = """
            SELECT ISNULL(SUM(CAST(us.user_seeks AS bigint)), 0)
            FROM sys.indexes i
            LEFT JOIN sys.dm_db_index_usage_stats us
                   ON us.database_id = DB_ID() AND us.object_id = i.object_id AND us.index_id = i.index_id
            WHERE i.object_id = OBJECT_ID(N'itg.JobProgress') AND i.name = @name
            """;
        q.Parameters.AddWithValue("@name", indexName);
        var value = await q.ExecuteScalarAsync();
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private async Task<long> ReadsAsync(Func<EcrDbContext, Task> action)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        async Task<long> SessionReadsAsync()
        {
            await using var q = connection.CreateCommand();
            q.CommandText = "SELECT logical_reads FROM sys.dm_exec_sessions WHERE session_id = @@SPID";
            return Convert.ToInt64(await q.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(connection).Options);
        var before = await SessionReadsAsync();
        await action(db);
        return await SessionReadsAsync() - before;
    }
}
