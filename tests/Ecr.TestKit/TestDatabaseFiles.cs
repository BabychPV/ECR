using Microsoft.Data.SqlClient;

namespace Ecr.TestKit;

/// <summary>
/// Where test databases (<c>EcrTest_*</c>) keep their files, and the start-up sweep of stale ones.
/// </summary>
/// <remarks>
/// ⛔ 06.11: <c>H:</c> twice dropped to 0.1 GB — <c>EcrTest_*</c> files piled up in <c>H:\EcrData</c> next to the live
/// <c>EcrLandUi</c> stand. Test files therefore never go there: <c>ECR_TEST_DATAPATH</c> (old name <c>ECR_TEST_DATA_PATH</c>
/// still read) → else <see cref="DefaultPath"/> when the folder exists → else the server's default data folder (CI
/// containers). A path under <see cref="StandDataPath"/> is refused with a named error instead of being used.
/// </remarks>
public static class TestDatabaseFiles
{
    /// <summary>Variable with the folder for test database files.</summary>
    public const string PathVariable = "ECR_TEST_DATAPATH";

    /// <summary>Old name of <see cref="PathVariable"/> (still read).</summary>
    public const string LegacyPathVariable = "ECR_TEST_DATA_PATH";

    /// <summary>Default folder, used only when it exists (the SQL Server service account must be able to write there).</summary>
    public const string DefaultPath = @"C:\EcrTestData";

    /// <summary>The live stand's data folder: never used for test files.</summary>
    public const string StandDataPath = @"H:\EcrData";

    /// <summary>Variable with the age in hours after which an idle <c>EcrTest_*</c> is dropped at fixture start; 0 — off.</summary>
    public const string MaxAgeVariable = "ECR_TEST_DB_MAX_AGE_HOURS";

    /// <summary>Default age of a stale test database.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(24);

    /// <summary>Folder for test database files; empty — the server default.</summary>
    /// <exception cref="InvalidOperationException">The configured folder is <see cref="StandDataPath"/> or inside it.</exception>
    public static string Resolve() => Resolve(Environment.GetEnvironmentVariable, Directory.Exists);

    /// <summary>Folder for test database files from the given environment; empty — the server default.</summary>
    /// <param name="variable">Reads an environment variable.</param>
    /// <param name="exists">Says whether a folder exists.</param>
    /// <exception cref="InvalidOperationException">The configured folder is <see cref="StandDataPath"/> or inside it.</exception>
    public static string Resolve(Func<string, string?> variable, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(variable);
        ArgumentNullException.ThrowIfNull(exists);

        var configured = variable(PathVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = variable(LegacyPathVariable);
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Normalize(configured);
            if (IsStandPath(path))
            {
                throw new InvalidOperationException(
                    $"{PathVariable}={configured}: test database files never go to {StandDataPath} (the live stand's disk). "
                    + $"Use {DefaultPath} or another folder outside it.");
            }

            return path;
        }

        return exists(DefaultPath) ? DefaultPath : string.Empty;
    }

    /// <summary>The folder is <see cref="StandDataPath"/> or inside it (case and slash direction ignored).</summary>
    /// <param name="path">Folder.</param>
    public static bool IsStandPath(string path)
    {
        var normalized = Normalize(path);
        return string.Equals(normalized, StandDataPath, StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith(StandDataPath + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Files fragment of <c>CREATE DATABASE [name]</c> (starts with a new line), or empty for the server default.
    /// </summary>
    /// <param name="databaseName">Database name (also the logical file names).</param>
    public static string Clause(string databaseName)
    {
        var path = Resolve();
        if (path.Length == 0)
        {
            return string.Empty;
        }

        Directory.CreateDirectory(path);
        return $"""

            ON PRIMARY (NAME = N'{databaseName}', FILENAME = N'{path}\{databaseName}.mdf')
            LOG ON     (NAME = N'{databaseName}_log', FILENAME = N'{path}\{databaseName}_log.ldf')
            """;
    }

    /// <summary>Age after which an idle test database is stale; <c>null</c> — the sweep is off.</summary>
    public static TimeSpan? MaxAge()
    {
        var text = Environment.GetEnvironmentVariable(MaxAgeVariable);
        if (string.IsNullOrWhiteSpace(text))
        {
            return DefaultMaxAge;
        }

        return int.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var hours)
            ? hours > 0 ? TimeSpan.FromHours(hours) : null
            : DefaultMaxAge;
    }

    /// <summary>
    /// Drops test databases older than <paramref name="maxAge"/> that nobody is connected to; returns the dropped names.
    /// </summary>
    /// <param name="masterConnection">Connection string to the server (any catalog; <c>master</c> is used).</param>
    /// <param name="maxAge">Age by <c>create_date</c>.</param>
    /// <param name="keep">Name never dropped (the caller's own database).</param>
    /// <param name="likePattern">Names to consider (<c>LIKE</c>); default — every <c>EcrTest_*</c>.</param>
    /// <remarks>
    /// ⚠ Never <c>SET SINGLE_USER WITH ROLLBACK IMMEDIATE</c> here: a database of another worktree's run that is in use
    /// RIGHT NOW must survive. Only databases without sessions are tried, and a plain <c>DROP DATABASE</c> that loses the
    /// race to a new connection (3702) or to a parallel sweep (3701) is skipped, not retried. Deterministic names
    /// (<c>EcrTest_&lt;project&gt;_&lt;worktree&gt;</c>) are re-created on every run, so their <c>create_date</c> is the last run.
    /// </remarks>
    public static async Task<IReadOnlyList<string>> DropStaleAsync(
        string masterConnection, TimeSpan maxAge, string? keep, string likePattern = "EcrTest[_]%")
    {
        var master = new SqlConnectionStringBuilder(masterConnection) { InitialCatalog = "master", TrustServerCertificate = true };
        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        var candidates = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT d.name
                FROM sys.databases d
                WHERE d.name LIKE @pattern
                  AND d.database_id > 4
                  AND d.create_date < DATEADD(SECOND, -@ageSeconds, GETDATE())
                  AND (@keep IS NULL OR d.name <> @keep)
                  AND NOT EXISTS (SELECT 1 FROM sys.dm_exec_sessions s WHERE s.database_id = d.database_id)
                ORDER BY d.create_date;
                """;
            command.Parameters.Add(new SqlParameter("@pattern", likePattern));
            command.Parameters.Add(new SqlParameter("@ageSeconds", (long)Math.Max(0, maxAge.TotalSeconds)));
            command.Parameters.Add(new SqlParameter("@keep", (object?)keep ?? DBNull.Value));
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                candidates.Add(reader.GetString(0));
            }
        }

        return await DropEachAsync(candidates, async name =>
        {
            // A failed DROP can leave the connection closed/broken; without a reopen every later DROP would throw
            // InvalidOperationException and the remaining stale databases would never go.
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.CloseAsync().ConfigureAwait(false);
                await connection.OpenAsync().ConfigureAwait(false);
            }

            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP DATABASE [{name.Replace("]", "]]", StringComparison.Ordinal)}];";
            drop.CommandTimeout = 120;
            await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="drop"/> for every name; a failed drop is skipped, never thrown — the sweep is housekeeping and
    /// must not fail the fixture (B12, 12.11: one failed DROP broke the connection, the next threw
    /// <see cref="InvalidOperationException"/> and every test of the fixture failed at start-up). Returns the dropped names.
    /// </summary>
    /// <param name="names">Databases to drop, in order.</param>
    /// <param name="drop">Drops one database.</param>
    /// <remarks>
    /// Only <see cref="SqlException"/> (in use now — 3702, already dropped by a parallel sweep — 3701, a broken connection)
    /// and <see cref="InvalidOperationException"/> (the connection is closed/broken, a reopen failed) are swallowed: anything
    /// else is a defect in the sweep itself and must stay visible.
    /// </remarks>
    public static async Task<IReadOnlyList<string>> DropEachAsync(IEnumerable<string> names, Func<string, Task> drop)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(drop);

        var dropped = new List<string>();
        foreach (var name in names)
        {
            try
            {
                await drop(name).ConfigureAwait(false);
                dropped.Add(name);
            }
            catch (Exception e) when (e is SqlException or InvalidOperationException)
            {
                // Leave it: the next run's sweep tries again.
            }
        }

        return dropped;
    }

    private static string Normalize(string path) => path.Trim().Replace('/', '\\').TrimEnd('\\');
}
