// tests/Ecr.Infrastructure.Tests/Persistence/RV01ValueRefIndexMigrationTests.cs
using System.Globalization;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Persistence.Migrations;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Міграція <c>RV01_ValueRefIndex</c> (D-5, аудит 2026-11-10): фільтрований індекс
/// <c>IX_RegistryValue_ValueRef (ValueRefEntryId) WHERE ValueRefEntryId IS NOT NULL</c> на <c>dic.RegistryValue</c> —
/// з <c>ONLINE</c> там, де він є, і перевіркою наявності.
/// </summary>
/// <remarks>
/// ⚠ Своя база, зупинена на міграції перед цією: так виглядає майданчик до оновлення. Перевіряється: форма індексу
/// (ключ, фільтр), Down і повторний Up, ідемпотентний <c>migration.sql</c> двічі, наявний індекс (збірка вручну до
/// оновлення) не ламає міграцію, гілка без <c>ONLINE</c> (редакція 4).
/// <para>
/// Мутація: прибрати <c>INDEXPROPERTY … IS NULL</c> у <c>CreateIndexSql</c> — червоніє тест наявного індексу
/// (1913 «already exists»); прибрати <c>WHERE ValueRefEntryId IS NOT NULL</c> — червоніє форма.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class RV01ValueRefIndexMigrationTests(SqlServerFixture sql)
{
    /// <summary>Остання міграція перед цією.</summary>
    private const string BeforeMigration = "20261104084809_CI01_CalculationIssue";

    private const string ThisMigration = "20261104185622_RV01_ValueRefIndex";

    private const string Index = "IX_RegistryValue_ValueRef";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-5")]
    public async Task Up_Down_Up_builds_drops_and_rebuilds_the_index()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;

        Assert.Equal(0, await IndexCountAsync(cs));

        await MigrateAsync(cs, ThisMigration);
        await AssertIndexShapeAsync(cs);

        await MigrateAsync(cs, BeforeMigration);
        Assert.Equal(0, await IndexCountAsync(cs));

        await MigrateAsync(cs, ThisMigration);
        await AssertIndexShapeAsync(cs);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-5")]
    public async Task Ідемпотентний_скрипт_двічі_і_наявний_індекс_нічого_не_ламають()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;

        // Збірка вручну до оновлення (DBA): індекс уже є, під тим самим іменем.
        await ExecuteAsync(
            cs, $"CREATE NONCLUSTERED INDEX {Index} ON dic.RegistryValue (ValueRefEntryId) WHERE ValueRefEntryId IS NOT NULL;");

        await RunIdempotentScriptAsync(cs);
        await AssertIndexShapeAsync(cs);

        // Другий прогін — 0 змін: міграція записана, індекс на місці й він один.
        await RunIdempotentScriptAsync(cs);
        await AssertIndexShapeAsync(cs);
        Assert.Equal(1, await ScalarAsync<int>(
            cs, "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]RV01[_]ValueRefIndex'"));

        // Та сама команда ще раз напряму (перезапуск після збою посередині): нічого не змінює.
        await ExecuteAsync(cs, RV01_ValueRefIndex.CreateIndexSql(D450DocumentOwnerIndex.EngineEditionExpression));
        await AssertIndexShapeAsync(cs);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-5")]
    public async Task Гілка_без_ONLINE_для_Standard_і_Express_будує_той_самий_індекс()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;

        // ⚠ Справжньої Standard/Express у перевірці немає: редакція підставляється літералом, а текст без ONLINE
        // виконується наживо на Developer-інстансі — гарантує, що саме ця гілка синтаксично коректна.
        var offline = RV01_ValueRefIndex.CreateIndexSql("4");
        var online = RV01_ValueRefIndex.CreateIndexSql(D450DocumentOwnerIndex.EngineEditionExpression);
        Assert.Contains("DECLARE @rv01Edition int = 4;", offline, StringComparison.Ordinal);
        Assert.Contains("WITH (ONLINE = ON)", online, StringComparison.Ordinal);

        await ExecuteAsync(cs, offline);
        await AssertIndexShapeAsync(cs);
    }

    /// <summary>Форма індексу за каталогом: ключ, фільтр NOT NULL; індекс рівно один.</summary>
    private static async Task AssertIndexShapeAsync(string cs)
    {
        Assert.Equal(1, await IndexCountAsync(cs));
        Assert.Equal("ValueRefEntryId", await ScalarAsync<string>(
            cs,
            $"""
            SELECT STRING_AGG(c.name, N',') WITHIN GROUP (ORDER BY ic.key_ordinal)
              FROM sys.indexes AS i
              JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
              JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
             WHERE i.object_id = OBJECT_ID(N'dic.RegistryValue') AND i.name = N'{Index}' AND ic.is_included_column = 0;
            """));
        var filter = await ScalarAsync<string>(
            cs,
            $"SELECT filter_definition FROM sys.indexes WHERE object_id = OBJECT_ID(N'dic.RegistryValue') AND name = N'{Index}'");
        Assert.Contains("ValueRefEntryId", filter, StringComparison.Ordinal);
        Assert.Contains("NOT NULL", filter, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<int> IndexCountAsync(string cs)
        => ScalarAsync<int>(
            cs, $"SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dic.RegistryValue') AND name = N'{Index}'");

    /// <summary>Своя база (з видаленням у кінці), зупинена на міграції перед цією.</summary>
    private async Task<OwnDatabase> OwnDatabaseBeforeMigrationAsync()
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var name = builder.InitialCatalog + "_RV01" + Guid.NewGuid().ToString("N")[..6];

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
