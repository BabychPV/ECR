// tests/Ecr.Infrastructure.Tests/Persistence/D148ScalePrecheckTests.cs
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
/// Аудит D1: оновлення наявної бази через <c>D148*Scale16</c> зі значенням
/// <c>|x| ≥ 1e12</c> зупиняється ЗРОЗУМІЛО і ДО зміни схеми, а не SQL-помилкою
/// 8115 посеред ланцюга.
/// </summary>
/// <remarks>
/// ⚠ Три міграції <c>D148*Scale16</c> переводять десять стовпців із
/// <c>decimal(28,10)</c> у <c>decimal(28,16)</c>: ціла частина тимчасово
/// скорочується з 18 розрядів до 12 (наступні <c>D148*Precision34</c>
/// повертають 18). Значення на кшталт 1 ТДж у джоулях (1e12) не вміщується,
/// і <c>ALTER COLUMN</c> падає «Arithmetic overflow» без імені стовпця й без
/// кількості рядків.
///
/// ⚠ Свіжої бази це не стосується: на порожніх таблицях переводити нічого.
/// Тест тому будує ВЛАСНУ базу, зупинену на міграції перед D148, — так, як
/// виглядає майданчик замовника, розгорнутий до 2026-09-20.
///
/// ⚠ Один тест, дві фази: фаза «межа» доводить, що передперевірка не
/// блокує значень, які <c>decimal(28,16)</c> вміщує, і вона можлива лише
/// після фази «відмова» на тій самій базі (xUnit не впорядковує тести класу).
/// </remarks>
[Collection("SqlServer")]
public sealed class D148ScalePrecheckTests(SqlServerFixture sql)
{
    /// <summary>Остання міграція перед серією D-148.</summary>
    private const string BeforeD148 = "20260920022059_B2CollectionScheduleState";

    /// <summary>Номер помилки передперевірки (<c>THROW</c>).</summary>
    private const int PrecheckErrorNumber = 50148;

    /// <summary>Усі десять стовпців, які звужують <c>D148*Scale16</c>, і знак значення.</summary>
    /// <remarks>
    /// ⚠ Перелік повний навмисно: пропущений тут стовпець означав би, що
    /// передперевірка може його не бачити, а тест цього не помітить.
    /// Знак чергується, бо межа — за модулем.
    /// </remarks>
    private static readonly (string Table, string Column, int Sign)[] Columns =
    [
        ("doc.CellValue", "ValueNumeric", 1),
        ("doc.DocumentIndexValue", "ValueNumeric", -1),
        ("rpt.ReportRow", "ValueNumeric", 1),
        ("ext.RawDataPoint", "ValueNumeric", -1),
        ("dic.RegistryValue", "ValueNumeric", 1),
        ("calc.CalculationResult", "Value", -1),
        ("calc.CalculationInput", "Value", 1),
        ("calc.CalculationStep", "Value", -1),
        ("calc.MethodologyConstant", "Value", 1),
        ("calc.TestCase", "Tolerance", -1),
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-148")]
    public async Task Значення_поза_межею_зупиняє_D148_до_зміни_схеми_а_межа_проходить()
    {
        var connectionString = await CreateDatabaseBeforeD148Async();

        // ── Фаза «відмова»: 1e12 у кожному зі стовпців ──────────────────────
        foreach (var (table, column, sign) in Columns)
        {
            await InsertRowAsync(connectionString, table, column, sign * 1_000_000_000_000m);
        }

        // Два шляхи застосування, обидва мають зупинитись однаково:
        // ідемпотентний скрипт — ним розгортають прод (`deploy-ecr.ps1`,
        // пакет інсталятора); `MigrateAsync` — режим `StartupMode=Migrate`.
        var viaScript = await Record.ExceptionAsync(() => RunIdempotentScriptAsync(connectionString));
        var viaMigrate = await Record.ExceptionAsync(() => MigrateToLatestAsync(connectionString));

        foreach (var (path, error) in new[] { ("migration.sql", viaScript), ("MigrateAsync", viaMigrate) })
        {
            var sqlError = FindSqlException(error);
            Assert.True(
                sqlError is { Number: PrecheckErrorNumber },
                $"{path}: очікувалась передперевірка {PrecheckErrorNumber}, а прийшло: " +
                $"{(sqlError is null ? error?.ToString() ?? "жодної помилки" : $"SQL {sqlError.Number}: {sqlError.Message}")}");

            // Повідомлення називає КОЖЕН стовпець, кількість рядків і максимум —
            // інакше оператор знає, що зламано, і не знає де.
            foreach (var (table, column, _) in Columns)
            {
                Assert.Contains($"{table}.{column}: рядків 1, max |x| = 1000000000000", sqlError!.Message, StringComparison.Ordinal);
            }

            Assert.Contains("operations-runbook.md", sqlError!.Message, StringComparison.Ordinal);
        }

        // ⛔ Зупинка ДО зміни схеми: жодна з D148 не записана, тип не змінено.
        Assert.Equal(0, await ScalarAsync<int>(
            connectionString,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]D148%'"));
        foreach (var (table, column, _) in Columns)
        {
            Assert.Equal("28,10", await ColumnTypeAsync(connectionString, table, column));
        }

        // ── Фаза «межа»: найбільше значення, яке вміщує decimal(28,16) ──────
        const decimal Boundary = 999_999_999_999.9999999999m;
        foreach (var (table, column, sign) in Columns)
        {
            await ExecuteAsync(
                connectionString,
                $"UPDATE {table} SET {column} = @v WHERE ABS({column}) >= 1000000000000",
                "@v",
                sign * Boundary);
        }

        await MigrateToLatestAsync(connectionString);

        foreach (var (table, column, sign) in Columns)
        {
            Assert.Equal("34,16", await ColumnTypeAsync(connectionString, table, column));
            Assert.Equal(sign * Boundary, await ScalarAsync<decimal>(connectionString, $"SELECT {column} FROM {table}"));
        }
    }

    /// <summary>Своя база, зупинена на міграції перед D148.</summary>
    /// <remarks>
    /// Без файлових груп і схем партиціонування — як у
    /// <c>SchemaValidatorTests.CreateBareDatabaseAsync</c>: міграції лягають на
    /// <c>PRIMARY</c>, а для передперевірки фізичне розміщення байдуже.
    /// </remarks>
    private async Task<string> CreateDatabaseBeforeD148Async()
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var name = builder.InitialCatalog + "_D148";

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

        await using var db = CreateContext(builder.ConnectionString);
        await db.GetService<IMigrator>().MigrateAsync(BeforeD148);

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

    private static async Task MigrateToLatestAsync(string connectionString)
    {
        await using var db = CreateContext(connectionString);
        await db.Database.MigrateAsync();
    }

    /// <summary>
    /// Рядок, у якому задано лише потрібний стовпець; решта обов'язкових —
    /// нейтральні значення за типом, обмеження таблиці вимкнено.
    /// </summary>
    /// <remarks>
    /// ⚠ Перевіряється не зв'язність даних, а тип одного стовпця, тож
    /// будувати десять графів сутностей заради одного числа — зайве.
    /// <c>NOCHECK CONSTRAINT ALL</c> діє лише на цю тестову базу.
    /// </remarks>
    private static Task InsertRowAsync(string connectionString, string table, string column, decimal value)
        => ExecuteAsync(
            connectionString,
            $"""
            DECLARE @obj int = OBJECT_ID(N'{table}');
            DECLARE @cols nvarchar(max), @vals nvarchar(max);

            SELECT
                @cols = STRING_AGG(CAST(QUOTENAME(c.name) AS nvarchar(max)), N', ') WITHIN GROUP (ORDER BY c.column_id),
                @vals = STRING_AGG(CAST(
                    CASE
                        WHEN c.name = N'{column}' THEN N'@v'
                        WHEN ty.name IN (N'int', N'bigint', N'smallint', N'tinyint', N'bit', N'decimal', N'numeric', N'float', N'real', N'money')
                            THEN N'0'
                        WHEN ty.name IN (N'nvarchar', N'varchar', N'nchar', N'char') THEN N'N''x'''
                        WHEN ty.name IN (N'datetime2', N'datetime', N'date', N'datetimeoffset', N'smalldatetime')
                            THEN N'''2026-01-01'''
                        WHEN ty.name = N'time' THEN N'''00:00'''
                        WHEN ty.name = N'uniqueidentifier' THEN N'NEWID()'
                        WHEN ty.name IN (N'varbinary', N'binary') THEN N'0x00'
                        ELSE N'NULL'
                    END AS nvarchar(max)), N', ') WITHIN GROUP (ORDER BY c.column_id)
            FROM sys.columns AS c
            JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
            WHERE c.object_id = @obj
              AND c.is_identity = 0
              AND c.is_computed = 0
              AND ty.name NOT IN (N'timestamp', N'rowversion')
              AND (c.name = N'{column}' OR (c.is_nullable = 0 AND c.default_object_id = 0));

            EXEC (N'ALTER TABLE {table} NOCHECK CONSTRAINT ALL');

            DECLARE @insert nvarchar(max) = N'INSERT INTO {table} (' + @cols + N') VALUES (' + @vals + N');';
            EXEC sys.sp_executesql @insert, N'@v decimal(28,10)', @v = @value;
            """,
            "@value",
            value);

    private static async Task<string> ColumnTypeAsync(string connectionString, string table, string column)
        => await ScalarAsync<string>(
            connectionString,
            $"""
            SELECT CONCAT(c.precision, ',', c.scale)
            FROM sys.columns AS c
            WHERE c.object_id = OBJECT_ID(N'{table}') AND c.name = N'{column}'
            """);

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

    /// <summary>Виконує текст із одним параметром <c>decimal(28,10)</c> на ім'я <paramref name="parameterName"/>.</summary>
    private static async Task ExecuteAsync(string connectionString, string text, string parameterName, decimal value)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = 300;

        var parameter = command.Parameters.Add(parameterName, System.Data.SqlDbType.Decimal);
        parameter.Precision = 28;
        parameter.Scale = 10;
        parameter.Value = value;

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string query)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        var result = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result!, typeof(T), CultureInfo.InvariantCulture);
    }
}
