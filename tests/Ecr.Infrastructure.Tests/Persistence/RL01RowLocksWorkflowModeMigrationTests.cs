// tests/Ecr.Infrastructure.Tests/Persistence/RL01RowLocksWorkflowModeMigrationTests.cs
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
/// ✎ B75: міграція <c>RL01_RowLocksWorkflowMode</c> на базі, зупиненій перед нею (так виглядає майданчик до оновлення):
/// наявні версії отримують <c>WorkflowMode = 0</c> (з поданням), з'являється <c>doc.RowLock</c> з обмеженнями; <c>Down</c>
/// прибирає обидва; ідемпотентний скрипт двічі — другий прогін нічого не міняє.
/// </summary>
/// <remarks>
/// Мутації: (1) прибрати <c>defaultValue</c> колонки — червоніє «…наявні_версії…» (NOT NULL без DEFAULT не додається);
/// (2) прибрати <c>CK_RowLock_Range</c> — червоніє вставка порожнього проміжку.
/// </remarks>
[Collection("SqlServer")]
public sealed class RL01RowLocksWorkflowModeMigrationTests(SqlServerFixture sql)
{
    /// <summary>Остання міграція перед цією.</summary>
    private const string BeforeMigration = "20261115035944_B127_ColumnRenameProject";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B75")]
    public async Task Up_наявні_версії_з_поданням_таблиця_блокувань_з_обмеженнями_Down_прибирає()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;

        await ExecuteAsync(cs, """
            ALTER TABLE cfg.TemplateVersion NOCHECK CONSTRAINT ALL;
            ALTER TABLE cfg.Template NOCHECK CONSTRAINT ALL;
            SET IDENTITY_INSERT cfg.Template ON;
            INSERT INTO cfg.Template (Id, Code, NameL10n, CreatedAt, CreatedByUserId) VALUES (1, N'T1', N'{"en":"T"}', '2026-01-01', 1);
            SET IDENTITY_INSERT cfg.Template OFF;
            INSERT INTO cfg.TemplateVersion (TemplateId, [Version], Status, CreatedAt, CreatedByUserId) VALUES (1, N'1.0', 0, '2026-01-01', 1);
            """);

        await MigrateAsync(cs);

        Assert.Equal(0, await ScalarAsync<int>(cs, "SELECT CAST(WorkflowMode AS int) FROM cfg.TemplateVersion"));
        Assert.Equal(1, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sys.tables WHERE name = N'RowLock' AND SCHEMA_NAME(schema_id) = N'doc'"));

        // CK_RowLock_Range: порожній проміжок база не приймає (FK вимкнено — документ і користувач тут не потрібні).
        var ex = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(cs, """
            ALTER TABLE doc.RowLock NOCHECK CONSTRAINT FK_RowLock_Document;
            ALTER TABLE doc.RowLock NOCHECK CONSTRAINT FK_RowLock_Owner;
            INSERT INTO doc.RowLock (DocumentId, PeriodKey, SheetCode, TableCode, RowKey, LockFrom, LockUntil, OwnerUserId, CreatedAt)
            VALUES (1, 202610, N'S', N'T', N'R', '2026-10-02', '2026-10-01', 1, '2026-10-01');
            """));
        Assert.Contains("CK_RowLock_Range", ex.Message, StringComparison.Ordinal);

        await MigrateAsync(cs, BeforeMigration);
        Assert.Equal(0, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sys.tables WHERE name = N'RowLock' AND SCHEMA_NAME(schema_id) = N'doc'"));
        Assert.Equal(0, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'cfg.TemplateVersion') AND name = N'WorkflowMode'"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B75")]
    public async Task Ідемпотентний_скрипт_двічі_застосовує_RL01_один_раз()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;

        await RunIdempotentScriptAsync(cs);
        await RunIdempotentScriptAsync(cs);

        Assert.Equal(1, await ScalarAsync<int>(
            cs, "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]RL01[_]RowLocksWorkflowMode'"));
        Assert.Equal(1, await ScalarAsync<int>(
            cs, "SELECT COUNT(*) FROM sys.default_constraints WHERE name = N'DF_TV_WorkflowMode'"));
        Assert.Equal(1, await ScalarAsync<int>(
            cs, "SELECT COUNT(*) FROM sys.indexes WHERE name = N'IX_RowLock_Doc_Period' AND has_filter = 1"));
    }

    /// <summary>Своя база (з видаленням у кінці), зупинена на міграції перед цією.</summary>
    private async Task<OwnDatabase> OwnDatabaseBeforeMigrationAsync()
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var name = builder.InitialCatalog + "_RL01" + Guid.NewGuid().ToString("N")[..6];

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

    /// <summary>Той самий <c>migration.sql</c> (<c>--idempotent</c>), пакетами по <c>GO</c>.</summary>
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
