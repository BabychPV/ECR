// tests/Ecr.Infrastructure.Tests/Jobs/DbJobQueueRcsiTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Захоплення черги вимагає RCSI бази (<c>D-208</c>, правка А): <c>NOT EXISTS</c>
/// на <c>Running</c> без <c>READPAST</c> без RCSI блокується на чужих орендах.
/// </summary>
/// <remarks>
/// ⚠ База з RCSI OFF — тимчасова: вимкнути RCSI на базі фікстури означало б
/// <c>ROLLBACK IMMEDIATE</c> по з'єднаннях сусідніх тестів. Таблиця
/// <c>itg.JobProgress</c> у ній — ті самі колонки, що в базі фікстури після
/// міграцій (<c>SELECT TOP (0) … INTO</c>); індекси claim не потребує.
///
/// Мутація: прибрати виклик <c>EnsureRcsiAsync</c> у <c>ClaimAsync</c> —
/// перший тест червоний (claim бере задачу на базі без RCSI).
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class DbJobQueueRcsiTests(SqlServerFixture sql)
{
    private static readonly string[] DefaultLane = [JobLanes.Default];

    [Fact]
    public async Task Без_RCSI_claim_відмовляє_з_поясненням_і_нічого_не_захоплює()
    {
        var name = $"EcrTest_RcsiOff_{Guid.NewGuid():N}"[..32];
        var master = new SqlConnectionStringBuilder(sql.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        var target = new SqlConnectionStringBuilder(sql.ConnectionString) { InitialCatalog = name }.ConnectionString;

        await ExecAsync(master, $"CREATE DATABASE [{name}]; ALTER DATABASE [{name}] SET READ_COMMITTED_SNAPSHOT OFF;");
        try
        {
            await ExecAsync(target, "EXEC (N'CREATE SCHEMA itg');");
            await ExecAsync(target, $"SELECT TOP (0) * INTO itg.JobProgress FROM [{sql.DatabaseName}].itg.JobProgress;");
            await ExecAsync(target, """
                INSERT INTO itg.JobProgress
                    (JobId, JobCode, [State], [Percent], StartedAt, UpdatedAt, HeartbeatAt, CreatedAt,
                     Lane, Payload, AvailableAt, ReclaimCount)
                VALUES (N'rcsi-off-1', N'Ecr.Test.RcsiJob', 'Queued', 0, SYSUTCDATETIME(), SYSUTCDATETIME(),
                        SYSUTCDATETIME(), SYSUTCDATETIME(), 'default', N'{}', DATEADD(second, -1, SYSUTCDATETIME()), 0);
                """);

            Assert.Equal(0, await ScalarAsync(target,
                "SELECT CAST(is_read_committed_snapshot_on AS int) FROM sys.databases WHERE name = DB_NAME();"));

            await using var db = Context(target);
            var queue = new DbJobQueue(db, new SystemClock());

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                queue.ClaimAsync(DefaultLane, "test/rcsi-off", JobQueueLimits.DefaultLease, CancellationToken.None));

            Assert.Contains("READ_COMMITTED_SNAPSHOT", ex.Message, StringComparison.Ordinal);
            Assert.Contains("06-rcsi.sql", ex.Message, StringComparison.Ordinal);
            Assert.Contains(name, ex.Message, StringComparison.Ordinal);

            // Нічого не захоплено: задача лишилась Queued без токена й власника.
            Assert.Equal(1, await ScalarAsync(target, """
                SELECT COUNT(*) FROM itg.JobProgress
                WHERE JobId = N'rcsi-off-1' AND [State] = 'Queued' AND ClaimToken IS NULL AND LeaseUntil IS NULL;
                """));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await ExecAsync(master, $"""
                IF DB_ID(N'{name}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{name}];
                END
                """);
        }
    }

    [Fact]
    public async Task З_RCSI_claim_бере_задачу()
    {
        Assert.Equal(1, await ScalarAsync(sql.ConnectionString,
            "SELECT CAST(is_read_committed_snapshot_on AS int) FROM sys.databases WHERE name = DB_NAME();"));

        var target = $"Ecr.Test.RcsiJob~{Guid.NewGuid():N}";
        await using var db = sql.CreateContext();
        var queue = new DbJobQueue(db, new SystemClock());

        try
        {
            var queued = await queue.EnqueueAsync(
                new JobEnqueueRequest("Ecr.Test.RcsiJob", JobLanes.Default, "{}", target), CancellationToken.None);

            var claimed = await queue.ClaimAsync(DefaultLane, "test/rcsi-on", JobQueueLimits.DefaultLease, CancellationToken.None);

            Assert.NotNull(claimed);
            Assert.Equal(queued.JobId, claimed.Claim.JobId);
        }
        finally
        {
            await db.JobProgresses.Where(p => p.TargetKey == target).ExecuteDeleteAsync();
        }
    }

    private static EcrDbContext Context(string connectionString)
        => new(EfWarningGuard.Apply(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(connectionString)).Options);

    private static async Task ExecAsync(string connectionString, string text)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(string connectionString, string text)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
