// tests/Ecr.Infrastructure.Tests/Persistence/P1126SourceCredentialsMigrationTests.cs
using System.Globalization;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Міграція <c>P1126SourceCredentials</c> (P-1126-SRCCRED, AC-R): чотири колонки облікових даних у
/// <c>ext.DataSource</c>; наявні джерела після неї — <c>Windows</c> без логіна, пароля й мітки відмови.
/// </summary>
/// <remarks>
/// ⚠ Своя база, зупинена на міграції перед цією: так виглядає майданчик до оновлення (FLERT на Sql, PI на PiSqlClient).
/// Мутація: <c>defaultValue: (byte)1</c> (UserPassword) в <c>Up</c> — наявні рядки стають UserPassword, тест червоніє.
/// </remarks>
[Collection("SqlServer")]
public sealed class P1126SourceCredentialsMigrationTests(SqlServerFixture sql)
{
    /// <summary>Остання міграція перед цією.</summary>
    private const string BeforeMigration = "20261124230554_P1124DropConsistencyRule";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P-1126-SRCCRED")]
    public async Task Up_makes_existing_sources_Windows_without_credentials_and_Down_Up_round_trips()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;
        await ArrangeAsync(cs);

        await MigrateAsync(cs);
        await AssertMigratedAsync(cs);

        // Down знімає всі чотири колонки; повторний Up дає той самий стан.
        await MigrateAsync(cs, BeforeMigration);
        Assert.Equal(0, await ScalarAsync<int>(
            cs, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'ext.DataSource') AND name IN (N'AuthMode', N'UserName', N'PasswordProtected', N'CredentialsRejectedAt')"));
        await MigrateAsync(cs);
        await AssertMigratedAsync(cs);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P-1126-SRCCRED")]
    public async Task Idempotent_script_twice_adds_columns_once()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;
        await ArrangeAsync(cs);

        await RunIdempotentScriptAsync(cs);
        await AssertMigratedAsync(cs);

        await RunIdempotentScriptAsync(cs);
        await AssertMigratedAsync(cs);
        Assert.Equal(1, await ScalarAsync<int>(
            cs, "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]P1126SourceCredentials'"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P-1126-SRCCRED")]
    public async Task Up_carries_no_data_update()
    {
        // ⛔ AC-R: наявні рядки стають Windows через DEFAULT колонки, а не через UPDATE — оновлення поверх живої бази
        // не переписує жодного рядка (і не будить тригерів аудиту).
        await using var db = CreateContext(sql.ConnectionString);
        var script = db.GetService<IMigrator>().GenerateScript(
            fromMigration: BeforeMigration, toMigration: "P1126SourceCredentials");

        Assert.DoesNotContain("UPDATE", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DEFAULT CAST(0 AS tinyint)", script, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Обидва наявні джерела — Windows (0), без логіна, пароля й мітки відмови; EF читає їх як <see cref="SourceAuthMode.Windows"/>
    /// без пароля. Колонки мають оголошені типи.
    /// </summary>
    private static async Task AssertMigratedAsync(string cs)
    {
        Assert.Equal(2, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM ext.DataSource"));
        Assert.Equal(2, await ScalarAsync<int>(
            cs, "SELECT COUNT(*) FROM ext.DataSource WHERE AuthMode = 0 AND UserName IS NULL AND PasswordProtected IS NULL AND CredentialsRejectedAt IS NULL"));

        Assert.Equal(
            "AuthMode:tinyint:0;CredentialsRejectedAt:datetime2:1;PasswordProtected:varbinary:1;UserName:nvarchar:1",
            await ScalarAsync<string>(cs, """
                SELECT STRING_AGG(c.name + N':' + t.name + N':' + CAST(c.is_nullable AS nvarchar(1)), N';') WITHIN GROUP (ORDER BY c.name)
                  FROM sys.columns AS c JOIN sys.types AS t ON t.user_type_id = c.user_type_id
                 WHERE c.object_id = OBJECT_ID(N'ext.DataSource')
                   AND c.name IN (N'AuthMode', N'UserName', N'PasswordProtected', N'CredentialsRejectedAt')
                """));

        await using var db = CreateContext(cs);
        var sources = await db.DataSources.AsNoTracking().OrderBy(s => s.Code).ToListAsync();
        Assert.Equal(["FLERT", "PI_MAIN"], sources.Select(s => s.Code));
        Assert.All(sources, s =>
        {
            Assert.Equal(SourceAuthMode.Windows, s.AuthMode);
            Assert.Null(s.UserName);
            Assert.False(s.HasPassword);
            Assert.Null(s.CredentialsRejectedAt);
        });
    }

    /// <summary>Два джерела, як на майданчику: FLERT (Sql) і PI (PiSqlClient), обидва Windows.</summary>
    private static Task ArrangeAsync(string cs)
        => ExecuteAsync(cs, """
            INSERT INTO ext.DataSource (Code, NameL10n, Transport, Endpoint, SecretName, MaxParallel, IsActive)
            VALUES (N'FLERT', N'{"en":"FLERT"}', 2, N'Server=flert;Database=FLERT;Integrated Security=true', N'DataSource.FLERT', 4, 1),
                   (N'PI_MAIN', N'{"en":"PI"}', 1, N'Driver={PI SQL Client};AF Server=af;AF Database=ECR;Integrated Security=SSPI', N'DataSource.PI_MAIN', 4, 1);
            """);

    /// <summary>Своя база (з видаленням у кінці), зупинена на міграції перед цією.</summary>
    private async Task<OwnDatabase> OwnDatabaseBeforeMigrationAsync()
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var name = builder.InitialCatalog + "_P1126" + Guid.NewGuid().ToString("N")[..6];

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
