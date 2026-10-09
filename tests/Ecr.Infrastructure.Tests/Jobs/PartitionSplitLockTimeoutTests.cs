using System.Diagnostics;
using System.Globalization;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// <c>arc.usp_EnsurePartitions</c>: SPLIT <c>pf_ByPeriodKey</c> не чекає Sch-M вічно
/// за довгим читачем, а повторює спробу з паузою (аудит 09.10c, U1-02).
/// </summary>
/// <remarks>
/// ⛔ Без <c>LOCK_TIMEOUT</c> запит Sch-M ставав у чергу за найдовшим читачем
/// гарячих таблиць, а за ним — усі нові запити користувачів. Провал до того ж не
/// лишав сліду в <c>itg.MaintenanceRun</c>.
/// <para>
/// ⚠ Читача відтворює окрема сесія з <c>TABLOCK, HOLDLOCK</c> у відкритій
/// транзакції: S на <c>doc.CellValue</c> несумісне з Sch-M так само, як Sch-S
/// нічного скану. «Сьогодні» — параметр (2035-05-15), тож межа 203506 гарантовано
/// нова; усе, що процедура додала (і в <c>pf_ByPeriodKey</c>, і в
/// <c>pf_AuditByMonth</c>), та її рядки журналу прибираються у <c>finally</c>:
/// інакше <c>PartitionCheckJob*Tests</c> бачили б зайвий запас меж.
/// </para>
/// <para>
/// ⚠ Повтор доводиться не таймінгом, а станом: тест чекає, поки сесія процедури
/// стане на <c>WAITFOR</c> (тобто спроба вже впала по 1222 і йде пауза), і лише
/// тоді відпускає читача.
/// </para>
/// Мутації: (1) прибрати <c>SET LOCK_TIMEOUT</c> з динамічного пакета — перший тест
/// замість 1222 падає за таймаутом команди, другий не бачить <c>WAITFOR</c>;
/// (2) прибрати повтор (одразу <c>THROW</c>) — другий тест червоний (1222 замість успіху).
/// </remarks>
[Collection("SqlServer")]
public sealed class PartitionSplitLockTimeoutTests(SqlServerFixture sql)
{
    private const string Today = "2035-05-15";
    private const int NewKey = 203506;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Audit", "U1-02")]
    public async Task SPLIT_за_довгим_читачем_вичерпує_спроби_і_пише_Failed_а_не_висить()
    {
        var snapshot = await SnapshotAsync();
        Assert.False(await BoundaryExistsAsync(NewKey), "межа 203506 вже є — тест нічого б не довів");

        try
        {
            await using var reader = await HoldReaderLockAsync();

            var watch = Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
                $"EXEC arc.usp_EnsurePartitions @MonthsAhead = 1, @Today = '{Today}', "
                + "@LockTimeoutMs = 300, @MaxAttempts = 2, @RetryDelaySeconds = 1;",
                timeoutSeconds: 60));
            watch.Stop();

            Assert.Equal(1222, error.Number);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"процедура чекала {watch.Elapsed}");
            Assert.False(await BoundaryExistsAsync(NewKey));

            var (status, details) = await LastRunAsync(snapshot.LastRunId);
            Assert.Equal("Failed", status);
            Assert.Contains("\"error\":1222", details, StringComparison.Ordinal);
            Assert.Contains("\"attempts\":2", details, StringComparison.Ordinal);
        }
        finally
        {
            await RestoreAsync(snapshot);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Audit", "U1-02")]
    public async Task SPLIT_повторює_спробу_після_паузи_і_проходить_коли_читач_відпустив()
    {
        var snapshot = await SnapshotAsync();
        Assert.False(await BoundaryExistsAsync(NewKey), "межа 203506 вже є — тест нічого б не довів");

        try
        {
            var reader = await HoldReaderLockAsync();
            Task run;
            int spid;

            await using (var caller = new SqlConnection(sql.ConnectionString))
            {
                await caller.OpenAsync();
                spid = await SpidAsync(caller);

                await using var command = caller.CreateCommand();
                command.CommandTimeout = 120;
                command.CommandText =
                    $"EXEC arc.usp_EnsurePartitions @MonthsAhead = 1, @Today = '{Today}', "
                    + "@LockTimeoutMs = 300, @MaxAttempts = 50, @RetryDelaySeconds = 1;";
                run = command.ExecuteNonQueryAsync();

                try
                {
                    // Перша спроба впала по 1222, процедура на паузі — Sch-M не тримає й не чекає.
                    await WaitForWaitTypeAsync(spid, "WAITFOR", TimeSpan.FromSeconds(30));
                }
                finally
                {
                    await reader.DisposeAsync();
                }

                await run;
            }

            Assert.True(await BoundaryExistsAsync(NewKey));

            var (status, details) = await LastRunAsync(snapshot.LastRunId);
            Assert.Equal("Succeeded", status);
            Assert.Contains("\"added\":1", details, StringComparison.Ordinal);
            Assert.DoesNotContain("\"retries\":0", details, StringComparison.Ordinal);
        }
        finally
        {
            await RestoreAsync(snapshot);
        }
    }

    /// <summary>Сесія-читач: S на всю <c>doc.CellValue</c> до кінця транзакції.</summary>
    private async Task<HeldLock> HoldReaderLockAsync()
    {
        var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        var transaction = connection.BeginTransaction();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT_BIG(*) FROM doc.CellValue WITH (TABLOCK, HOLDLOCK);";
            await command.ExecuteScalarAsync();
        }

        return new HeldLock(connection, transaction);
    }

    private async Task WaitForWaitTypeAsync(int spid, string waitType, TimeSpan limit)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < limit)
        {
            var current = await ScalarAsync<string>(
                $"SELECT wait_type FROM sys.dm_exec_requests WHERE session_id = {spid}");
            if (string.Equals(current, waitType, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"сесія {spid} так і не стала на {waitType} за {limit}: SPLIT чекає Sch-M без межі");
    }

    private static async Task<int> SpidAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(@@SPID AS int);";
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task<bool> BoundaryExistsAsync(int key)
        => await ScalarAsync<int>(string.Create(CultureInfo.InvariantCulture, $"""
            SELECT COUNT(*) FROM sys.partition_range_values rv
            JOIN sys.partition_functions pf ON pf.function_id = rv.function_id
            WHERE pf.name = N'pf_ByPeriodKey' AND CAST(rv.value AS int) = {key}
            """)) == 1;

    private async Task<Snapshot> SnapshotAsync()
        => new(
            await ListAsync("SELECT CAST(CAST(rv.value AS int) AS nvarchar(30)) FROM sys.partition_range_values rv "
                + "JOIN sys.partition_functions pf ON pf.function_id = rv.function_id WHERE pf.name = N'pf_ByPeriodKey'"),
            await ListAsync("SELECT CONVERT(nvarchar(30), CAST(rv.value AS datetime2(3)), 126) FROM sys.partition_range_values rv "
                + "JOIN sys.partition_functions pf ON pf.function_id = rv.function_id WHERE pf.name = N'pf_AuditByMonth'"),
            await ScalarAsync<long>("SELECT ISNULL(MAX(Id), 0) FROM itg.MaintenanceRun"));

    /// <summary>Прибирає все, що додала процедура: нові межі обох функцій і рядки журналу.</summary>
    private async Task RestoreAsync(Snapshot before)
    {
        var after = await SnapshotAsync();

        foreach (var key in after.PeriodBoundaries.Except(before.PeriodBoundaries, StringComparer.Ordinal))
        {
            await ExecuteAsync($"ALTER PARTITION FUNCTION pf_ByPeriodKey() MERGE RANGE ({key});");
        }

        foreach (var at in after.AuditBoundaries.Except(before.AuditBoundaries, StringComparer.Ordinal))
        {
            await ExecuteAsync($"ALTER PARTITION FUNCTION pf_AuditByMonth() MERGE RANGE ('{at}');");
        }

        await ExecuteAsync(string.Create(CultureInfo.InvariantCulture,
            $"DELETE FROM itg.MaintenanceRun WHERE Id > {before.LastRunId} AND JobCode = N'EnsurePartitions';"));
    }

    private async Task<(string Status, string Details)> LastRunAsync(long sinceId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = string.Create(CultureInfo.InvariantCulture,
            $"SELECT TOP 1 Status, ISNULL(DetailsJson, N'') FROM itg.MaintenanceRun "
            + $"WHERE Id > {sinceId} AND JobCode = N'EnsurePartitions' ORDER BY Id DESC");

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "процедура не залишила рядка в itg.MaintenanceRun");
        return (reader.GetString(0), reader.GetString(1));
    }

    private async Task<List<string>> ListAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;

        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private async Task ExecuteAsync(string sqlText, int timeoutSeconds = 30)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = timeoutSeconds;
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

    private sealed record Snapshot(List<string> PeriodBoundaries, List<string> AuditBoundaries, long LastRunId);

    private sealed class HeldLock(SqlConnection connection, SqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.RollbackAsync();
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
