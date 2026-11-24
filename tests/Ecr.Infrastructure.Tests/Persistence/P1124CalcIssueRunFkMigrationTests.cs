// tests/Ecr.Infrastructure.Tests/Persistence/P1124CalcIssueRunFkMigrationTests.cs
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
/// Міграція <c>P1124CalcIssueRunFk</c> (✎ P-1124): одноразово прибирає сироти <c>calc.CalculationIssue</c> (прогону вже
/// немає), живі проблеми таблиць, що є в документі, переводить на актуальний прогін, і ставить <c>FK_CIssue_Run</c>.
/// </summary>
/// <remarks>
/// ⚠ Своя база, зупинена на міграції перед цією: так виглядає майданчик до оновлення (копія live мала 323 сироти).
/// Мутації: прибрати з <c>Up</c> <c>DELETE</c> — ключ не створюється (547), тест червоніє; прибрати <c>UPDATE</c> — жива
/// проблема зникає разом із сиротами, тест червоніє.
/// </remarks>
[Collection("SqlServer")]
public sealed class P1124CalcIssueRunFkMigrationTests(SqlServerFixture sql)
{
    /// <summary>Остання міграція перед цією.</summary>
    private const string BeforeMigration = "20261124075117_P112320RunSourceState";

    /// <summary>Прогону з цим ключем немає: сирота.</summary>
    private const long GoneRun = 9_999_999;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P-1124")]
    public async Task Up_removes_orphans_carries_live_issues_adds_fk_and_rerun_changes_nothing()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;
        var (current, superseded) = await ArrangeAsync(cs);
        Assert.Equal(6, await CountAsync(cs));

        await MigrateAsync(cs);
        await AssertMigratedAsync(cs, current);

        // Down знімає лише ключ та індекс; повторний Up нічого не міняє.
        await MigrateAsync(cs, BeforeMigration);
        Assert.Equal(0, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sys.foreign_keys WHERE name = N'FK_CIssue_Run'"));
        await MigrateAsync(cs);
        await AssertMigratedAsync(cs, current);
        Assert.True(superseded > 0);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P-1124")]
    public async Task Idempotent_script_twice_cleans_once()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;
        var (current, _) = await ArrangeAsync(cs);

        await RunIdempotentScriptAsync(cs);
        await AssertMigratedAsync(cs, current);

        await RunIdempotentScriptAsync(cs);
        await AssertMigratedAsync(cs, current);
        Assert.Equal(1, await ScalarAsync<int>(
            cs, "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]P1124CalcIssueRunFk'"));
    }

    /// <summary>
    /// Лишились три проблеми документа 10 таблиці 5 (екземпляр є), усі — на актуальному прогоні: жива на актуальному, жива на
    /// <c>Superseded</c>, жива-сирота. Пішли: сирота таблиці без екземпляра (6) і сироти документа 20 (актуального прогону
    /// немає). Ключ стоїть і перевірений базою (<c>is_not_trusted = 0</c>).
    /// </summary>
    private static async Task AssertMigratedAsync(string cs, long current)
    {
        Assert.Equal(3, await CountAsync(cs));
        Assert.Equal(3, await ScalarAsync<int>(
            cs, $"SELECT COUNT(*) FROM calc.CalculationIssue WHERE DocumentId = 10 AND TableDefId = 5 AND CalculationRunId = {current}"));
        Assert.Equal(0, await ScalarAsync<int>(
            cs, "SELECT COUNT(*) FROM calc.CalculationIssue AS i WHERE NOT EXISTS (SELECT 1 FROM calc.CalculationRun AS r WHERE r.Id = i.CalculationRunId)"));
        Assert.Equal(1, await ScalarAsync<int>(
            cs, "SELECT COUNT(*) FROM sys.foreign_keys WHERE name = N'FK_CIssue_Run' AND is_not_trusted = 0 AND delete_referential_action = 0"));
        Assert.Equal(1, await ScalarAsync<int>(
            cs, "SELECT COUNT(*) FROM sys.indexes WHERE name = N'IX_CalculationIssue_Run' AND object_id = OBJECT_ID(N'calc.CalculationIssue')"));
    }

    /// <summary>
    /// Проєкт 1; документ 10 з екземпляром таблиці 5 (оригінал) за 202610; документ 20 з екземпляром таблиці 5, без
    /// актуального прогону. Прогони документа 10: актуальний і <c>Superseded</c>. Зовнішні ключі вимкнені: довідники не потрібні.
    /// </summary>
    private static async Task<(long Current, long Superseded)> ArrangeAsync(string cs)
    {
        await ExecuteAsync(cs, """
            ALTER TABLE doc.Document NOCHECK CONSTRAINT ALL;
            ALTER TABLE doc.TableInstance NOCHECK CONSTRAINT ALL;
            ALTER TABLE calc.CalculationRun NOCHECK CONSTRAINT ALL;

            SET IDENTITY_INSERT doc.Document ON;
            INSERT INTO doc.Document (Id, ProjectId, BusinessKey, CreatedAt, CreatedByUserId, ModifiedAt, ModifiedByUserId)
            VALUES (10, 1, N'P1124-A', '2026-11-01', 1, '2026-11-01', 1),
                   (20, 1, N'P1124-B', '2026-11-01', 1, '2026-11-01', 1);
            SET IDENTITY_INSERT doc.Document OFF;

            INSERT INTO doc.TableInstance (PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt)
            VALUES (202610, 100, 10, 5, '2026-11-01', '2026-11-01'),
                   (202610, 200, 20, 5, '2026-11-01', '2026-11-01');

            INSERT INTO calc.CalculationRun (ProjectId, PeriodKey, DocumentId, Status, StartedAt)
            VALUES (1, 202610, 10, N'Superseded', '2026-11-01');
            INSERT INTO calc.CalculationRun (ProjectId, PeriodKey, DocumentId, Status, StartedAt)
            VALUES (1, 202610, 10, N'Current', '2026-11-02');
            """);

        var superseded = await ScalarAsync<long>(cs, "SELECT Id FROM calc.CalculationRun WHERE Status = N'Superseded'");
        var current = await ScalarAsync<long>(cs, "SELECT Id FROM calc.CalculationRun WHERE Status = N'Current'");

        await ExecuteAsync(cs, string.Create(CultureInfo.InvariantCulture, $"""
            INSERT INTO calc.CalculationIssue (DocumentId, PeriodKey, TableDefId, SheetCopyId, RowKey, RowNumber, Kind, CalculationRunId, DetectedAt)
            VALUES (10, 202610, 5, NULL, N'live-current', 1, N'MissingInputs', {current}, '2026-11-02'),
                   (10, 202610, 5, NULL, N'live-superseded', 2, N'MissingInputs', {superseded}, '2026-11-01'),
                   (10, 202610, 5, NULL, N'live-orphan', 3, N'MissingInputs', {GoneRun}, '2026-10-30'),
                   (10, 202610, 6, NULL, N'dead-table', 1, N'MissingInputs', {GoneRun}, '2026-10-30'),
                   (10, 202610, 5, 777, N'dead-copy', 1, N'MissingInputs', {GoneRun}, '2026-10-30'),
                   (20, 202610, 5, NULL, N'no-current', 1, N'MissingInputs', {GoneRun}, '2026-10-30');
            """));
        return (current, superseded);
    }

    private static Task<int> CountAsync(string cs)
        => ScalarAsync<int>(cs, "SELECT COUNT(*) FROM calc.CalculationIssue");

    /// <summary>Своя база (з видаленням у кінці), зупинена на міграції перед цією.</summary>
    private async Task<OwnDatabase> OwnDatabaseBeforeMigrationAsync()
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var name = builder.InitialCatalog + "_P1124" + Guid.NewGuid().ToString("N")[..6];

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
