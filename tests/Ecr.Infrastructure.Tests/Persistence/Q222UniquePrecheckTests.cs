// tests/Ecr.Infrastructure.Tests/Persistence/Q222UniquePrecheckTests.cs
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
/// Аудит D2 (Q-222): унікальні індекси міграції <c>Q222MissingForeignKeysAndConstraints</c>
/// на базі з дублями зупиняють оновлення ЗРОЗУМІЛО, ДО зміни схеми і без видалення даних;
/// після усунення дублів міграція проходить, індекси відхиляють новий дубль.
/// </summary>
/// <remarks>
/// ⚠ Свіжої бази перевірка не стосується (порожні таблиці), тому тест будує ВЛАСНУ базу,
/// зупинену на міграції перед Q222. Механізм — той самий, що в
/// <see cref="U1UnitForeignKeysMigrationTests"/>: перевірку вставляє
/// <see cref="EcrMigrationsSqlGenerator"/>, тож діють обидва шляхи застосування.
/// </remarks>
[Collection("SqlServer")]
public sealed class Q222UniquePrecheckTests(SqlServerFixture sql)
{
    /// <summary>Остання міграція перед Q222.</summary>
    private const string BeforeQ222 = "20260910054347_Q206DropScriptVersion";

    private const string Sid = "S-1-5-21-D2-DUP";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "AUDIT-D2")]
    public async Task Дублі_під_унікальні_індекси_зупиняють_Q222_з_переліком_до_зміни_схеми_а_після_усунення_проходить()
    {
        var connectionString = await CreateDatabaseBeforeQ222Async();

        // ── Фаза «відмова»: по парі дублів під кожен із трьох індексів ──────────────
        await InsertAsync(connectionString, "sec.RoleAssignment", ("PrincipalSid", $"N'{Sid}'"), ("RoleId", "7"));
        await InsertAsync(connectionString, "sec.RoleAssignment", ("PrincipalSid", $"N'{Sid}'"), ("RoleId", "7"));
        await InsertAsync(connectionString, "sec.RoleAssignment", ("UserId", "42"), ("RoleId", "8"));
        await InsertAsync(connectionString, "sec.RoleAssignment", ("UserId", "42"), ("RoleId", "8"));
        // Category і ValidFrom — NULL в обох: нормалізація має зчепити їх в одну групу.
        await InsertAsync(connectionString, "calc.MethodologyConstant", ("MethodologyVersionId", "5"), ("Code", "N'K1'"));
        await InsertAsync(connectionString, "calc.MethodologyConstant", ("MethodologyVersionId", "5"), ("Code", "N'K1'"));
        // Не дубль: той самий код у ІНШІЙ версії методології.
        await InsertAsync(connectionString, "calc.MethodologyConstant", ("MethodologyVersionId", "6"), ("Code", "N'K1'"));

        // Мутація: прибрати Q222UniquePrecheck із EcrMigrationsSqlGenerator — обидва шляхи
        // приносять 1505 (без переліку) або проходять без помилки: червоне.
        var viaScript = await Record.ExceptionAsync(() => RunIdempotentScriptAsync(connectionString));
        var viaMigrate = await Record.ExceptionAsync(() => MigrateAsync(connectionString));

        foreach (var (path, error) in new[] { ("migration.sql", viaScript), ("MigrateAsync", viaMigrate) })
        {
            var sqlError = FindSqlException(error);
            Assert.True(
                sqlError is { Number: Q222UniquePrecheck.ErrorNumber },
                $"{path}: очікувалась передперевірка {Q222UniquePrecheck.ErrorNumber}, а прийшло: " +
                $"{(sqlError is null ? error?.ToString() ?? "жодної помилки" : $"SQL {sqlError.Number}: {sqlError.Message}")}");

            Assert.Contains($"UQ_RoleAssignment_Sid (sec.RoleAssignment): 1 груп; (SID {Sid}, роль 7) x2", sqlError!.Message, StringComparison.Ordinal);
            Assert.Contains("UQ_RoleAssignment_User (sec.RoleAssignment): 1 груп; (користувач 42, роль 8) x2", sqlError.Message, StringComparison.Ordinal);
            Assert.Contains(
                "UQ_MethodologyConstant (calc.MethodologyConstant): 1 груп; (версія методології 5, код K1, категорія , діє з 1900-01-01) x2",
                sqlError.Message,
                StringComparison.Ordinal);
            Assert.Contains("operations-runbook.md", sqlError.Message, StringComparison.Ordinal);
        }

        // ⛔ Зупинка ДО зміни схеми: міграція не записана, індексів і обчислюваних стовпців
        // немає, жоден рядок не видалено.
        Assert.Equal(0, await ScalarAsync<int>(
            connectionString,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]Q222MissingForeignKeysAndConstraints'"));
        Assert.Equal(0, await UniqueIndexesAsync(connectionString));
        Assert.Equal(0, await ScalarAsync<int>(
            connectionString, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'calc.MethodologyConstant') AND name = N'CategoryNorm'"));
        Assert.Equal(4, await ScalarAsync<int>(connectionString, "SELECT COUNT(*) FROM sec.RoleAssignment"));
        Assert.Equal(3, await ScalarAsync<int>(connectionString, "SELECT COUNT(*) FROM calc.MethodologyConstant"));

        // ── Фаза «усунено» (runbook §8.3): лишити по одному рядку кожної групи ──────
        await ExecuteAsync(connectionString, "DELETE FROM sec.RoleAssignment WHERE Id NOT IN (SELECT MIN(Id) FROM sec.RoleAssignment GROUP BY PrincipalSid, UserId, RoleId);");
        await ExecuteAsync(connectionString, "DELETE FROM calc.MethodologyConstant WHERE Id NOT IN (SELECT MIN(Id) FROM calc.MethodologyConstant GROUP BY MethodologyVersionId, Code);");

        await MigrateAsync(connectionString);

        Assert.Equal(3, await UniqueIndexesAsync(connectionString));

        // Індекси справді тримають: дубль відхиляється ключем, не застосунком (2601).
        var rejected = await Record.ExceptionAsync(
            () => InsertAsync(connectionString, "sec.RoleAssignment", ("UserId", "42"), ("RoleId", "8")));
        Assert.True(FindSqlException(rejected) is { Number: 2601 }, $"очікувалось 2601, а прийшло: {rejected}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "AUDIT-D2")]
    public async Task Чиста_база_має_три_унікальні_індекси_і_відхиляє_дубль()
    {
        Assert.Equal(3, await UniqueIndexesAsync(sql.ConnectionString));

        // Фільтровані індекси: рядки з NULL у ключі не рахуються дублями (призначення особі
        // не має SID, призначення групі не має користувача).
        // ⚠ Дубль тут не вставляється: InsertAsync вимикає обмеження таблиці, а ця база спільна.
        // Відхилення дубля доводить перший тест, на власній базі.
        Assert.Equal(2, await ScalarAsync<int>(
            sql.ConnectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'sec.RoleAssignment') AND is_unique = 1 "
            + "AND ((name = N'UQ_RoleAssignment_Sid' AND filter_definition LIKE N'%PrincipalSid%IS NOT NULL%') "
            + "OR (name = N'UQ_RoleAssignment_User' AND filter_definition LIKE N'%UserId%IS NOT NULL%'))"));
    }

    /// <summary>Скільки з трьох індексів Q222 стоїть у базі.</summary>
    private static Task<int> UniqueIndexesAsync(string connectionString)
        => ScalarAsync<int>(
            connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE is_unique = 1 "
            + "AND name IN (N'UQ_RoleAssignment_Sid', N'UQ_RoleAssignment_User', N'UQ_MethodologyConstant')");

    /// <summary>Своя база, зупинена на міграції перед Q222.</summary>
    private async Task<string> CreateDatabaseBeforeQ222Async()
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var name = builder.InitialCatalog + "_Q222";

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
        await MigrateAsync(builder.ConnectionString, BeforeQ222);

        return builder.ConnectionString;
    }

    /// <summary>Той самий <c>migration.sql</c>, що генерують <c>deploy-ecr.ps1</c> і інсталятор.</summary>
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
    /// Рядок із заданими стовпцями; решта обов'язкових — нейтральні значення за типом,
    /// обмеження таблиці вимкнено (як у <see cref="U1UnitForeignKeysMigrationTests"/>).
    /// </summary>
    private static async Task<int> InsertAsync(
        string connectionString, string table, params (string Column, string Literal)[] values)
    {
        var given = string.Join(
            " UNION ALL ",
            values.Select(v => $"SELECT N'{v.Column}', N'{v.Literal.Replace("'", "''", StringComparison.Ordinal)}'"));

        return await ScalarAsync<int>(
            connectionString,
            $"""
            SET NOCOUNT ON;
            DECLARE @obj int = OBJECT_ID(N'{table}');
            DECLARE @given TABLE (ColumnName sysname, Literal nvarchar(200));
            INSERT @given (ColumnName, Literal) {given};

            DECLARE @cols nvarchar(max), @vals nvarchar(max);
            SELECT
                @cols = STRING_AGG(CAST(QUOTENAME(c.name) AS nvarchar(max)), N', ') WITHIN GROUP (ORDER BY c.column_id),
                @vals = STRING_AGG(CAST(COALESCE(g.Literal,
                    CASE
                        WHEN ty.name IN (N'int', N'bigint', N'smallint', N'tinyint', N'bit', N'decimal', N'numeric', N'float', N'real', N'money')
                            THEN N'0'
                        WHEN ty.name IN (N'nvarchar', N'varchar', N'nchar', N'char') THEN N'N''x'''
                        WHEN ty.name IN (N'datetime2', N'datetime', N'date', N'datetimeoffset', N'smalldatetime')
                            THEN N'''2026-01-01'''
                        WHEN ty.name = N'time' THEN N'''00:00'''
                        WHEN ty.name = N'uniqueidentifier' THEN N'NEWID()'
                        WHEN ty.name IN (N'varbinary', N'binary') THEN N'0x00'
                        ELSE N'NULL'
                    END) AS nvarchar(max)), N', ') WITHIN GROUP (ORDER BY c.column_id)
            FROM sys.columns AS c
            JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN @given AS g ON g.ColumnName = c.name
            WHERE c.object_id = @obj
              AND c.is_identity = 0
              AND c.is_computed = 0
              AND c.generated_always_type = 0
              AND ty.name NOT IN (N'timestamp', N'rowversion')
              AND (g.ColumnName IS NOT NULL OR (c.is_nullable = 0 AND c.default_object_id = 0));

            EXEC (N'ALTER TABLE {table} NOCHECK CONSTRAINT ALL');

            DECLARE @insert nvarchar(max) =
                N'INSERT INTO {table} (' + @cols + N') VALUES (' + @vals + N'); SELECT CAST(SCOPE_IDENTITY() AS int);';
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
