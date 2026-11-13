// tests/Ecr.Infrastructure.Tests/Persistence/RS2PermitHistoryCopyOrphansMigrationTests.cs
using System.Globalization;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Міграція <c>RS2_PermitHistoryCopyOrphans</c> (D-3, аудит 2026-11-10): одноразово прибирає історію дозволів
/// (<c>doc.PermitSegment</c>, <c>doc.CellPermitPart</c>) копій аркушів, яких уже немає в <c>doc.DocumentSheet</c>.
/// </summary>
/// <remarks>
/// ⚠ Своя база, зупинена на міграції перед цією: так виглядає майданчик до оновлення. Сирота зникає, живий сегмент
/// чинної копії і рядки без копії (<c>SheetCopyId IS NULL</c> — оригінал) лишаються; повторний прогін нічого не міняє.
/// <para>
/// Мутація: прибрати з <c>Up</c> будь-який із двох <c>DELETE</c> — червоніє відповідна перевірка.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class RS2PermitHistoryCopyOrphansMigrationTests(SqlServerFixture sql)
{
    /// <summary>Остання міграція перед цією.</summary>
    private const string BeforeMigration = "20261113213338_OB01_OutboxRetryBackoff";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-3")]
    public async Task Up_removes_orphans_and_keeps_live_copy_history_and_rerun_changes_nothing()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;

        await ArrangeAsync(cs);
        Assert.Equal(3, await CountAsync(cs, "doc.PermitSegment"));
        Assert.Equal(3, await CountAsync(cs, "doc.CellPermitPart"));

        await MigrateAsync(cs);

        // Лишились: сегмент і частина живої копії (SheetCopyId = 101) та оригінала (NULL).
        Assert.Equal([101L], await SheetCopyIdsAsync(cs, "doc.PermitSegment", withNull: false));
        Assert.Equal(2, await CountAsync(cs, "doc.PermitSegment"));
        Assert.Equal([101L], await SheetCopyIdsAsync(cs, "doc.CellPermitPart", withNull: false));
        Assert.Equal(2, await CountAsync(cs, "doc.CellPermitPart"));

        // Down порожній; повторний Up (другий прогін) нічого не міняє.
        await MigrateAsync(cs, BeforeMigration);
        Assert.Equal(2, await CountAsync(cs, "doc.PermitSegment"));
        await MigrateAsync(cs);
        Assert.Equal(2, await CountAsync(cs, "doc.PermitSegment"));
        Assert.Equal(2, await CountAsync(cs, "doc.CellPermitPart"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-3")]
    public async Task Idempotent_script_twice_removes_orphans_once()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;

        await ArrangeAsync(cs);

        await RunIdempotentScriptAsync(cs);
        Assert.Equal(2, await CountAsync(cs, "doc.PermitSegment"));
        Assert.Equal(2, await CountAsync(cs, "doc.CellPermitPart"));

        await RunIdempotentScriptAsync(cs);
        Assert.Equal(2, await CountAsync(cs, "doc.PermitSegment"));
        Assert.Equal(1, await ScalarAsync<int>(
            cs, "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]RS2[_]PermitHistoryCopyOrphans'"));
    }

    /// <summary>
    /// Копія 101 жива; копія 202 видалена (сирота); оригінал — <c>SheetCopyId IS NULL</c>. Зовнішні ключі вимкнені:
    /// довідники й документ для цієї міграції не потрібні.
    /// </summary>
    private static async Task ArrangeAsync(string cs)
    {
        await ExecuteAsync(cs, """
            ALTER TABLE doc.DocumentSheet NOCHECK CONSTRAINT ALL;
            ALTER TABLE doc.PermitSegment NOCHECK CONSTRAINT ALL;
            ALTER TABLE doc.CellPermitPart NOCHECK CONSTRAINT ALL;

            SET IDENTITY_INSERT doc.DocumentSheet ON;
            INSERT INTO doc.DocumentSheet (Id, DocumentId, SheetDefId, CopyOrdinal, IsIncluded)
            VALUES (101, 1, 1, 2, 1);
            SET IDENTITY_INSERT doc.DocumentSheet OFF;

            INSERT INTO doc.PermitSegment (DocumentId, TableDefId, SheetCopyId, RowKey, ColumnDefId, ValidFrom, RegistryEntryId, CreatedByUserId, CreatedAt)
            VALUES (1, 1, 101, N'r1', 1, '2026-03-15', 1, 1, '2026-03-01'),
                   (1, 1, 202, N'r1', 1, '2026-03-15', 1, 1, '2026-03-01'),
                   (1, 1, NULL, N'r1', 1, '2026-03-15', 1, 1, '2026-03-01');

            INSERT INTO doc.CellPermitPart (PeriodKey, DocumentId, TableDefId, SheetCopyId, RowKey, ColumnDefId, PartNo, RegistryEntryId, DayFrom, DayTo, ValueNumeric)
            VALUES (202603, 1, 1, 101, N'r1', 1, 1, 1, '2026-03-01', '2026-03-14', 5),
                   (202603, 1, 1, 202, N'r1', 1, 1, 1, '2026-03-01', '2026-03-14', 5),
                   (202603, 1, 1, NULL, N'r1', 1, 1, 1, '2026-03-01', '2026-03-14', 5);
            """);
    }

    private static Task<int> CountAsync(string cs, string table)
        => ScalarAsync<int>(cs, $"SELECT COUNT(*) FROM {table}");

    private static async Task<IReadOnlyList<long>> SheetCopyIdsAsync(string cs, string table, bool withNull)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT DISTINCT SheetCopyId FROM {table} WHERE SheetCopyId IS NOT NULL OR {(withNull ? 1 : 0)} = 1 ORDER BY SheetCopyId";
        var result = new List<long>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }

    /// <summary>Своя база (з видаленням у кінці), зупинена на міграції перед цією.</summary>
    private async Task<OwnDatabase> OwnDatabaseBeforeMigrationAsync()
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var name = builder.InitialCatalog + "_RS2" + Guid.NewGuid().ToString("N")[..6];

        await ExecuteAsync(
            new SqlConnectionStringBuilder(sql.ConnectionString) { InitialCatalog = "master" }.ConnectionString,
            $"""
            IF DB_ID(N'{name}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{name}];
            END
            CREATE DATABASE [{name}]{TestDatabaseFiles.Clause(name)} COLLATE Latin1_General_100_CI_AS_SC;
            """);

        builder.InitialCatalog = name;
        var database = new OwnDatabase(builder.ConnectionString, name, sql.ConnectionString);
        await MigrateAsync(database.ConnectionString, BeforeMigration);

        return database;
    }

    /// <summary>Своя база тесту; <c>DisposeAsync</c> її знищує.</summary>
    private sealed class OwnDatabase(string connectionString, string name, string serverConnectionString) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;

        public async ValueTask DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            await ExecuteAsync(
                new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = "master" }.ConnectionString,
                $"""
                IF DB_ID(N'{name}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{name}];
                END
                """);
        }
    }

    /// <summary>
    /// Той самий <c>migration.sql</c>, що генерують <c>deploy-ecr.ps1</c> і <c>build-installer.ps1</c>
    /// (<c>--idempotent</c>), пакетами по <c>GO</c>; перша ж помилка зупиняє, як <c>sqlcmd -b</c>.
    /// </summary>
    private static async Task RunIdempotentScriptAsync(string connectionString)
    {
        string script;
        await using (var db = CreateContext(connectionString))
        {
            script = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var batch in SqlBatches.Split(script))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            command.CommandTimeout = 300;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task MigrateAsync(string connectionString, string? target = null)
    {
        await using var db = CreateContext(connectionString);
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private static EcrDbContext CreateContext(string connectionString)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    private static async Task ExecuteAsync(string connectionString, string text)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string query)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        command.CommandTimeout = 300;
        var result = await command.ExecuteScalarAsync();

        if (result is null or DBNull)
        {
            return default!;
        }

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(result, target, CultureInfo.InvariantCulture);
    }
}
