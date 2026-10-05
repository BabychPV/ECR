// tests/Ecr.Infrastructure.Tests/Persistence/AN34SourceEntityRegistryUniqueMigrationTests.cs
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
/// Міграція <c>AN34SourceEntityRegistryUnique</c> (аудит 1D, L4-01): база, де дві сутності одного
/// з'єднання вже прив'язані до одного довідника, зупиняється ЗРОЗУМІЛО і ДО зміни схеми; після
/// розведення міграція проходить, індекс тримає, і міграція відкочується.
/// </summary>
/// <remarks>
/// ⚠ Свіжої бази передперевірка не стосується: у порожній таблиці дублям нема де взятись. Тест
/// будує ВЛАСНУ базу, зупинену на міграції перед цією, - так виглядає майданчик, розгорнутий до
/// індексу, де дві сутності вже прив'язались до одного довідника. Механізм і форма - ті самі, що в
/// <see cref="U1UnitForeignKeysMigrationTests"/>.
/// <para>
/// Мутація (2026-10-05, власний worktree): прибрати <c>migrationBuilder.Sql(PrecheckSql)</c> з
/// <c>Up</c> - обидва шляхи дають 1505 («duplicate key») замість 50401: червоний перший тест.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class AN34SourceEntityRegistryUniqueMigrationTests(SqlServerFixture sql)
{
    /// <summary>Остання міграція перед AN34.</summary>
    private const string BeforeAn34 = "20261001220406_FV1315ScheduleDependency";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "AN-34-L4-01")]
    public async Task Дві_сутності_одного_з_єднання_на_один_довідник_зупиняють_міграцію_з_переліком_а_розведена_база_проходить_і_відкочується()
    {
        var connectionString = await CreateDatabaseBeforeAn34Async();

        // ── Фаза «відмова»: з'єднання 1 - довідник 5 тримають ДВІ сутності (одна вимкнена);
        // з'єднання 2 - довідник 5 тримає одна (це не дубль); дві непривʼязані - не дубль.
        var first = await InsertAsync(connectionString, ("DataSourceId", "1"), ("Code", "N'PlantA'"), ("RegistryDefId", "5"));
        var second = await InsertAsync(
            connectionString, ("DataSourceId", "1"), ("Code", "N'PlantB'"), ("RegistryDefId", "5"), ("IsActive", "0"));
        await InsertAsync(connectionString, ("DataSourceId", "2"), ("Code", "N'PlantA'"), ("RegistryDefId", "5"));
        await InsertAsync(connectionString, ("DataSourceId", "1"), ("Code", "N'Free1'"));
        await InsertAsync(connectionString, ("DataSourceId", "1"), ("Code", "N'Free2'"));

        // Два шляхи застосування - ідемпотентний скрипт (deploy-ecr.ps1) і MigrateAsync -
        // зупиняються однаково. Мутація: прибрати Sql(PrecheckSql) - обидва дають 1505.
        var viaScript = await Record.ExceptionAsync(() => RunIdempotentScriptAsync(connectionString));
        var viaMigrate = await Record.ExceptionAsync(() => MigrateAsync(connectionString));

        foreach (var (path, error) in new[] { ("migration.sql", viaScript), ("MigrateAsync", viaMigrate) })
        {
            var sqlError = FindSqlException(error);
            Assert.True(
                sqlError is { Number: AN34SourceEntityRegistryUnique.PrecheckErrorNumber },
                $"{path}: очікувалась передперевірка {AN34SourceEntityRegistryUnique.PrecheckErrorNumber}, а прийшло: " +
                $"{(sqlError is null ? error?.ToString() ?? "жодної помилки" : $"SQL {sqlError.Number}: {sqlError.Message}")}");

            // Перелік називає з'єднання, довідник і сутності (з позначкою вимкненої) - інакше
            // оператор знає, що зламано, і не знає де. Одна група: з'єднання 2 - не дубль.
            Assert.Contains(
                $"Груп (з'єднання, довідник): 1; перші: (з'єднання 1, довідник 5, сутності: {first} PlantA, {second} PlantB (вимкнена))",
                sqlError!.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain("з'єднання 2", sqlError.Message, StringComparison.Ordinal);
            Assert.Contains("operations-runbook.md", sqlError.Message, StringComparison.Ordinal);
        }

        // ⛔ Зупинка ДО зміни схеми: міграція не записана, індексу немає, дані не змінено.
        Assert.Equal(0, await ScalarAsync<int>(
            connectionString,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]AN34SourceEntityRegistryUnique'"));
        Assert.Equal(0, await IndexCountAsync(connectionString));
        Assert.Equal(5, await ScalarAsync<int>(
            connectionString, $"SELECT RegistryDefId FROM ext.SourceEntity WHERE Id = {second}"));

        // ── Фаза «розведено»: рішення оператора (runbook §8.4) - відв'язати зайву сутність ──
        await ExecuteAsync(connectionString, $"UPDATE ext.SourceEntity SET RegistryDefId = NULL WHERE Id = {second};");

        await MigrateAsync(connectionString);
        Assert.Equal(1, await IndexCountAsync(connectionString));

        // Індекс справді тримає: друга прив'язка з'єднання 1 до довідника 5 - 2601/2627.
        var rejected = await Record.ExceptionAsync(
            () => ExecuteAsync(connectionString, $"UPDATE ext.SourceEntity SET RegistryDefId = 5 WHERE Id = {second};"));
        var uniqueError = FindSqlException(rejected);
        Assert.True(uniqueError is { Number: 2601 or 2627 }, $"очікувалось 2601/2627, а прийшло: {rejected}");
        Assert.Contains("UQ_SourceEntity_Registry", uniqueError!.Message, StringComparison.Ordinal);

        // Непривʼязані (NULL) індекс не обмежує: ще одна сутність без довідника - можна.
        await InsertAsync(connectionString, ("DataSourceId", "1"), ("Code", "N'Free3'"));

        // ── Фаза «вниз»: Down прибирає рівно свій індекс, і вгору знову проходить ──────────
        await MigrateAsync(connectionString, BeforeAn34);
        Assert.Equal(0, await IndexCountAsync(connectionString));

        await MigrateAsync(connectionString);
        Assert.Equal(1, await IndexCountAsync(connectionString));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "AN-34-L4-01")]
    public async Task База_без_дублів_проходить_ідемпотентний_скрипт_двічі_і_вдруге_нічого_не_змінює()
    {
        var connectionString = await CreateDatabaseBeforeAn34Async();
        await InsertAsync(connectionString, ("DataSourceId", "1"), ("Code", "N'PlantA'"), ("RegistryDefId", "5"));
        await InsertAsync(connectionString, ("DataSourceId", "1"), ("Code", "N'PlantB'"), ("RegistryDefId", "6"));

        await RunIdempotentScriptAsync(connectionString);
        Assert.Equal(1, await IndexCountAsync(connectionString));

        // Другий прогін - 0 змін: міграція вже записана, ні передперевірка, ні індекс не повторюються.
        await RunIdempotentScriptAsync(connectionString);
        Assert.Equal(1, await IndexCountAsync(connectionString));
        Assert.Equal(1, await ScalarAsync<int>(
            connectionString,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]AN34SourceEntityRegistryUnique'"));
    }

    /// <summary>Скільки є індексу <c>UQ_SourceEntity_Registry</c> (фільтрований унікальний).</summary>
    private static Task<int> IndexCountAsync(string connectionString)
        => ScalarAsync<int>(
            connectionString,
            """
            SELECT COUNT(*) FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'ext.SourceEntity') AND name = N'UQ_SourceEntity_Registry'
              AND is_unique = 1 AND has_filter = 1
            """);

    /// <summary>Своя база, зупинена на міграції перед AN34.</summary>
    private async Task<string> CreateDatabaseBeforeAn34Async()
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var name = builder.InitialCatalog + "_AN34" + Guid.NewGuid().ToString("N")[..6];

        await using (var connection = new SqlConnection(
            new SqlConnectionStringBuilder(sql.ConnectionString) { InitialCatalog = "master" }.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                IF DB_ID(N'{name}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{name}];
                END
                CREATE DATABASE [{name}] COLLATE Latin1_General_100_CI_AS_SC;
                """;
            await command.ExecuteNonQueryAsync();
        }

        builder.InitialCatalog = name;
        await MigrateAsync(builder.ConnectionString, BeforeAn34);

        return builder.ConnectionString;
    }

    /// <summary>
    /// Той самий <c>migration.sql</c>, що генерують <c>deploy-ecr.ps1</c> і
    /// <c>build-installer.ps1</c> (<c>--idempotent</c>), пакетами по <c>GO</c>;
    /// перша ж помилка зупиняє, як <c>sqlcmd -b</c>.
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

    /// <summary>
    /// Рядок <c>ext.SourceEntity</c>, у якому задано лише потрібні стовпці; решта обов'язкових -
    /// нейтральні значення. Обмеження вимкнено: з'єднань і довідників у цій базі немає, а
    /// перевіряється саме унікальність пари.
    /// </summary>
    /// <returns>Ідентифікатор вставленого рядка.</returns>
    private static async Task<int> InsertAsync(string connectionString, params (string Column, string Literal)[] values)
    {
        await ExecuteAsync(connectionString, "ALTER TABLE ext.SourceEntity NOCHECK CONSTRAINT ALL;");

        var columns = string.Join(", ", values.Select(v => $"[{v.Column}]"));
        // Літерали вкладаються в текст динамічного INSERT, тож апострофи подвоюються.
        var literals = string.Join(", ", values.Select(v => v.Literal)).Replace("'", "''", StringComparison.Ordinal);

        // Обов'язкові без значення за замовчуванням, яких тест не задає, - нейтральні.
        return await ScalarAsync<int>(
            connectionString,
            $"""
            SET NOCOUNT ON;
            DECLARE @obj int = OBJECT_ID(N'ext.SourceEntity');
            DECLARE @given TABLE (ColumnName sysname);
            INSERT @given (ColumnName) VALUES {string.Join(", ", values.Select(v => $"(N'{v.Column}')"))};

            DECLARE @extra nvarchar(max) = N'', @extraVals nvarchar(max) = N'';
            SELECT
                @extra = @extra + N', ' + QUOTENAME(c.name),
                @extraVals = @extraVals + N', ' + CASE
                    WHEN ty.name IN (N'int', N'bigint', N'smallint', N'tinyint', N'bit', N'decimal', N'numeric') THEN N'0'
                    WHEN ty.name IN (N'nvarchar', N'varchar', N'nchar', N'char') THEN N'N''x'''
                    WHEN ty.name IN (N'datetime2', N'datetime', N'date') THEN N'''2026-01-01'''
                    ELSE N'NULL' END
            FROM sys.columns AS c
            JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
            WHERE c.object_id = @obj
              AND c.is_identity = 0 AND c.is_computed = 0
              AND ty.name NOT IN (N'timestamp', N'rowversion')
              AND c.is_nullable = 0 AND c.default_object_id = 0
              AND c.name NOT IN (SELECT ColumnName FROM @given);

            DECLARE @insert nvarchar(max) =
                N'INSERT INTO ext.SourceEntity ({columns}' + @extra + N') VALUES ({literals}' + @extraVals + N'); '
                + N'SELECT CAST(SCOPE_IDENTITY() AS int);';
            EXEC sys.sp_executesql @insert;
            """);
    }

    private static SqlException? FindSqlException(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is SqlException found)
            {
                return found;
            }
        }

        return null;
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
        return (T)Convert.ChangeType(result!, typeof(T), CultureInfo.InvariantCulture);
    }
}
