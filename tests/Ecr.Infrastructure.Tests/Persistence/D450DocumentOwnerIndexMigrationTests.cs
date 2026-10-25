// tests/Ecr.Infrastructure.Tests/Persistence/D450DocumentOwnerIndexMigrationTests.cs
using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
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
/// Міграція <c>D450DocumentOwnerIndex</c>: <c>IX_Document_CreatedByUserId (CreatedByUserId) INCLUDE (ProjectId)</c> під
/// фільтр переліку «Власник» і вибір власників (D-450).
/// </summary>
/// <remarks>
/// ⚠ Своя база, зупинена на міграції перед цією: так виглядає майданчик до оновлення. Перевіряється: форма індексу
/// (ключ, INCLUDE), Down і повторний Up, ідемпотентний <c>migration.sql</c> двічі, наявний індекс (збірка вручну до
/// оновлення) не ламає міграцію, гілка без <c>ONLINE</c> (редакція 4) і ПЛАН: відбір за власником — seek по індексу без
/// скану й key lookup.
/// <para>
/// Мутації: прибрати <c>INCLUDE (ProjectId)</c> у <c>CreateIndexSql</c> — червоніють форма (Up/Down і гілка без
/// <c>ONLINE</c>; перевірено локально). ⚠ План без INCLUDE лишається зеленим: SQL Server перетинає індекс із
/// <c>UQ_Document (ProjectId, …)</c> замість key lookup, тож план доводить лише seek по індексу без скану, а INCLUDE
/// тримає перевірка форми. Прибрати <c>INDEXPROPERTY … IS NULL</c> — червоніє тест наявного індексу (1913 «already
/// exists»).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class D450DocumentOwnerIndexMigrationTests(SqlServerFixture sql)
{
    /// <summary>Остання міграція перед цією.</summary>
    private const string BeforeMigration = "20261023160807_D443DocumentParticipants";

    private const string Index = D450DocumentOwnerIndex.IndexName;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-450")]
    public async Task Up_будує_індекс_Down_прибирає_і_повторний_Up_проходить()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;

        Assert.Equal(0, await IndexCountAsync(cs));

        // Наявні рядки до міграції: індекс будується над даними, а не над порожньою таблицею.
        await ExecuteAsync(cs, "ALTER TABLE doc.Document NOCHECK CONSTRAINT ALL;");
        await ExecuteAsync(cs, """
            INSERT INTO doc.Document (ProjectId, BusinessKey, CreatedAt, CreatedByUserId, ModifiedAt, ModifiedByUserId)
            VALUES (1, N'D450-A', '2026-01-01', 7, '2026-01-01', 7), (2, N'D450-B', '2026-01-02', 8, '2026-01-02', 8);
            """);

        await MigrateAsync(cs);
        await AssertIndexShapeAsync(cs);

        await MigrateAsync(cs, BeforeMigration);
        Assert.Equal(0, await IndexCountAsync(cs));
        Assert.Equal(1, await ScalarAsync<int>(
            cs, "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'doc.Document') AND name = N'UQ_Document'"));

        await MigrateAsync(cs);
        await AssertIndexShapeAsync(cs);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-450")]
    public async Task Ідемпотентний_скрипт_двічі_і_наявний_індекс_нічого_не_ламають()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;

        // Збірка вручну до оновлення (DBA): індекс уже є, під тим самим іменем.
        await ExecuteAsync(cs, $"CREATE NONCLUSTERED INDEX {Index} ON doc.Document (CreatedByUserId) INCLUDE (ProjectId);");

        await RunIdempotentScriptAsync(cs);
        await AssertIndexShapeAsync(cs);

        // Другий прогін — 0 змін: міграція записана, індекс на місці й він один.
        await RunIdempotentScriptAsync(cs);
        await AssertIndexShapeAsync(cs);
        Assert.Equal(1, await ScalarAsync<int>(
            cs, "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]D450DocumentOwnerIndex'"));

        // Та сама команда ще раз напряму (перезапуск після збою посередині): нічого не змінює.
        await ExecuteAsync(cs, D450DocumentOwnerIndex.CreateIndexSql(D450DocumentOwnerIndex.EngineEditionExpression));
        await AssertIndexShapeAsync(cs);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-450")]
    public async Task Гілка_без_ONLINE_для_Standard_і_Express_будує_той_самий_індекс()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;

        // ⚠ Справжньої Standard/Express у перевірці немає: редакція підставляється літералом, а текст без ONLINE
        // виконується наживо на Developer-інстансі — гарантує, що саме ця гілка синтаксично коректна.
        var offline = D450DocumentOwnerIndex.CreateIndexSql("4");
        var online = D450DocumentOwnerIndex.CreateIndexSql(D450DocumentOwnerIndex.EngineEditionExpression);
        Assert.Contains("DECLARE @d450Edition int = 4;", offline, StringComparison.Ordinal);
        Assert.Contains("WITH (ONLINE = ON)", online, StringComparison.Ordinal);

        await ExecuteAsync(cs, offline);
        await AssertIndexShapeAsync(cs);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-450")]
    public async Task Відбір_за_власником_користується_індексом_без_скану_і_key_lookup()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;

        // 30 000 документів у 30 проєктах від 500 власників: так виглядає doc.Document на великих даних.
        await ExecuteAsync(cs, "ALTER TABLE doc.Document NOCHECK CONSTRAINT ALL;");
        await ExecuteAsync(cs, """
            ;WITH n AS (SELECT TOP (30000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
                          FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b)
            INSERT INTO doc.Document (ProjectId, BusinessKey, CreatedAt, CreatedByUserId, ModifiedAt, ModifiedByUserId)
            SELECT 1 + (i % 30), CONCAT(N'D450-', i), '2026-01-01', 1 + (i % 500), '2026-01-01', 1 FROM n;
            """);

        await MigrateAsync(cs);

        // Форма фільтра `ownerUserId` переліку: власник і межа видимості за проєктами.
        var plan = await PlanAsync(cs, """
            SELECT d.Id FROM doc.Document AS d
             WHERE d.CreatedByUserId = @owner AND d.ProjectId IN (1, 2, 3, 4, 5);
            """, new SqlParameter("@owner", SqlDbType.Int) { Value = 42 });

        Assert.Contains(Index, plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Clustered Index Scan", plan, StringComparison.Ordinal);
        Assert.False(
            Regex.IsMatch(plan, "Lookup=\"(1|true)\"", RegexOptions.IgnoreCase),
            "Відбір за власником добирає ProjectId key lookup-ом — INCLUDE індексу не покриває запит.");    }

    /// <summary>Форма індексу за каталогом: ключ, INCLUDE; індекс рівно один.</summary>
    private static async Task AssertIndexShapeAsync(string cs)
    {
        Assert.Equal(1, await IndexCountAsync(cs));
        Assert.Equal("CreatedByUserId", await IndexColumnsAsync(cs, includedColumns: false));
        Assert.Equal("ProjectId", await IndexColumnsAsync(cs, includedColumns: true));
    }

    private static Task<int> IndexCountAsync(string cs)
        => ScalarAsync<int>(
            cs, $"SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'doc.Document') AND name = N'{Index}'");

    /// <summary>Стовпці індексу через кому (ключові або INCLUDE), за порядком.</summary>
    private static async Task<string> IndexColumnsAsync(string cs, bool includedColumns)
    {
        var result = await ScalarAsync<string?>(
            cs,
            $"""
            SELECT ISNULL(STRING_AGG(c.name, N',') WITHIN GROUP (ORDER BY ic.key_ordinal, ic.index_column_id), N'')
              FROM sys.indexes AS i
              JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
              JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
             WHERE i.object_id = OBJECT_ID(N'doc.Document') AND i.name = N'{Index}'
               AND ic.is_included_column = {(includedColumns ? 1 : 0)};
            """);

        return result ?? string.Empty;
    }

    /// <summary>План виконання (<c>SET STATISTICS XML</c>) тексту запиту.</summary>
    private static async Task<string> PlanAsync(string cs, string text, SqlParameter parameter)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();

        await using (var on = connection.CreateCommand())
        {
            on.CommandText = "SET STATISTICS XML ON;";
            await on.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = text;
        command.Parameters.Add(parameter);

        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            if (reader.FieldCount == 1
                && reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase)
                && await reader.ReadAsync())
            {
                return reader.GetString(0);
            }
        }
        while (await reader.NextResultAsync());

        throw new InvalidOperationException("SQL Server не повернув план виконання.");
    }

    /// <summary>Своя база (з видаленням у кінці), зупинена на міграції перед цією.</summary>
    private async Task<OwnDatabase> OwnDatabaseBeforeMigrationAsync()
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var name = builder.InitialCatalog + "_D450" + Guid.NewGuid().ToString("N")[..6];

        await ExecuteAsync(
            new SqlConnectionStringBuilder(sql.ConnectionString) { InitialCatalog = "master" }.ConnectionString,
            $"""
            IF DB_ID(N'{name}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{name}];
            END
            CREATE DATABASE [{name}] COLLATE Latin1_General_100_CI_AS_SC;
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
