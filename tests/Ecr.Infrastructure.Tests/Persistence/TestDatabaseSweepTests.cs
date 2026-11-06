// tests/Ecr.Infrastructure.Tests/Persistence/TestDatabaseSweepTests.cs
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// 06.11: at fixture start <see cref="TestDatabaseFiles.DropStaleAsync"/> drops <c>EcrTest_*</c> older than a day — but only
/// idle ones: a database another worktree's run holds RIGHT NOW survives, and so does the caller's own.
/// </summary>
/// <remarks>
/// The probes use their own name pattern, so the sweep here never touches anyone else's database. Mutations (run, red):
/// no <c>sys.dm_exec_sessions</c> filter plus <c>SET SINGLE_USER WITH ROLLBACK IMMEDIATE</c> before the DROP — the busy probe
/// disappears (each alone is held by the other: a plain DROP of a used database fails with 3702); no <c>create_date</c>
/// condition — the fresh probes go on the first call; no <c>@keep</c> — the kept probe disappears.
/// </remarks>
[Collection("SqlServer")]
public sealed class TestDatabaseSweepTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Стара_незайнята_база_видаляється_зайнята_своя_і_свіжа_лишаються()
    {
        var tag = Guid.NewGuid().ToString("N")[..10];
        var pattern = $"EcrTest[_]Sweep[_]{tag}[_]%";
        string Name(string role) => $"EcrTest_Sweep_{tag}_{role}";
        var master = new SqlConnectionStringBuilder(sql.ConnectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString;
        string[] probes = [Name("idle"), Name("busy"), Name("kept")];

        try
        {
            foreach (var probe in probes)
            {
                await ExecAsync(master, $"CREATE DATABASE [{probe}]{TestDatabaseFiles.Clause(probe)};");
            }

            // create_date has a one-second grain: «older than zero» needs the clock to move past it.
            await Task.Delay(TimeSpan.FromSeconds(1.5));

            // Fresh by the real rule (a day): nothing goes.
            Assert.Empty(await TestDatabaseFiles.DropStaleAsync(master, TestDatabaseFiles.DefaultMaxAge, keep: null, pattern));

            await using var holder = new SqlConnection(
                new SqlConnectionStringBuilder(sql.ConnectionString) { InitialCatalog = Name("busy"), Pooling = false }.ConnectionString);
            await holder.OpenAsync();

            var dropped = await TestDatabaseFiles.DropStaleAsync(master, TimeSpan.Zero, keep: Name("kept"), pattern);

            Assert.Equal([Name("idle")], dropped);
            Assert.False(await ExistsAsync(master, Name("idle")));
            Assert.True(await ExistsAsync(master, Name("busy")), "a database in use must survive the sweep");
            Assert.True(await ExistsAsync(master, Name("kept")), "the caller's own database must survive the sweep");
        }
        finally
        {
            SqlConnection.ClearAllPools();
            foreach (var probe in probes)
            {
                await ExecAsync(master, $"""
                    IF DB_ID(N'{probe}') IS NOT NULL
                    BEGIN
                        ALTER DATABASE [{probe}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                        DROP DATABASE [{probe}];
                    END
                    """);
            }
        }
    }

    private static async Task<bool> ExistsAsync(string master, string name)
    {
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_ID(@name);";
        command.Parameters.Add(new SqlParameter("@name", name));
        return await command.ExecuteScalarAsync() is not (null or DBNull);
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }
}
