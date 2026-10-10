// tests/Ecr.Infrastructure.Tests/Persistence/AN80CarryOverInputsAsOfCategoryRuleLengthMigrationTests.cs
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
/// Міграція <c>AN80CarryOverInputsAsOfCategoryRuleLength</c> (аудит 1B): <c>calc.CalculationRun.InputsAsOfUtc</c> (N2-03)
/// і <c>calc.CategoryRule.Expression nvarchar(4000)</c> (N2-02 / L7-01) - база, де є правило довше за межу, зупиняється
/// ЗРОЗУМІЛО і ДО зміни схеми.
/// </summary>
/// <remarks>
/// ⚠ Свіжої бази передперевірка не стосується: у порожній таблиці довгих правил немає. Тест будує ВЛАСНУ базу, зупинену
/// на міграції перед цією, - так виглядає майданчик, де правило категорії довше за 4000 символів встигли зберегти до
/// обмеження. Механізм і форма - ті самі, що в <see cref="AN37MethodologyEffectiveUniqueMigrationTests"/>.
/// <para>
/// Мутації: прибрати <c>Sql(PrecheckSql)</c> - обидва шляхи дають 8152 замість 50801; замінити
/// <c>DATALENGTH(...) &gt; 8000</c> на <c>LEN(...) &gt; 4000</c> - правило з кінцевим пробілом не потрапляє в перелік
/// і міграція падає 8152 (червоний перший тест); прибрати <c>AddColumn</c> - червоніє перевірка колонки.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class AN80CarryOverInputsAsOfCategoryRuleLengthMigrationTests(SqlServerFixture sql)
{
    /// <summary>Остання міграція перед цією.</summary>
    private const string BeforeMigration = "20261007191203_L2CategoryRule";

    /// <summary>Номер помилки передперевірки (AN-80, N2-02).</summary>
    private const int PrecheckErrorNumber = 50801;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "AN-80-N2-02")]
    public async Task Правило_довше_за_межу_зупиняє_міграцію_з_переліком_а_розведена_база_проходить_і_відкочується()
    {
        var connectionString = await CreateDatabaseBeforeMigrationAsync();

        // Наявний прогін до міграції: після неї InputsAsOfUtc лишається NULL («входи знято на старті»).
        var runId = await InsertRunAsync(connectionString);

        // ── Фаза «відмова»: правило 1 рівно 4000 (законне), правило 2 - 4001, правило 3 - 4000 + кінцевий пробіл
        // (LEN не бачить пробіл, а nvarchar(4000) його не вмістить).
        await InsertRuleAsync(connectionString, versionId: 1, new string('a', 4000));
        var longRule = await InsertRuleAsync(connectionString, versionId: 2, new string('b', 4001));
        var trailing = await InsertRuleAsync(connectionString, versionId: 3, new string('c', 4000) + " ");

        var viaScript = await Record.ExceptionAsync(() => RunIdempotentScriptAsync(connectionString));
        var viaMigrate = await Record.ExceptionAsync(() => MigrateAsync(connectionString));

        foreach (var (path, error) in new[] { ("migration.sql", viaScript), ("MigrateAsync", viaMigrate) })
        {
            var sqlError = FindSqlException(error);
            Assert.True(
                sqlError is { Number: PrecheckErrorNumber },
                $"{path}: очікувалась передперевірка {PrecheckErrorNumber}, а прийшло: " +
                $"{(sqlError is null ? error?.ToString() ?? "жодної помилки" : $"SQL {sqlError.Number}: {sqlError.Message}")}");

            // Перелік називає правило, версію методології й довжину - інакше оператор знає, що зламано, і не знає де.
            Assert.Contains(
                $"Правил: 2; перші: (правило {longRule}, версія методології 2, довжина 4001); "
                + $"(правило {trailing}, версія методології 3, довжина 4001)",
                sqlError!.Message,
                StringComparison.Ordinal);
            Assert.Contains("operations-runbook.md", sqlError.Message, StringComparison.Ordinal);
        }

        // ⛔ Зупинка ДО зміни схеми: міграція не записана, колонки немає, тип вираз не змінено, дані не змінено.
        Assert.Equal(0, await ScalarAsync<int>(
            connectionString,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]AN80CarryOverInputsAsOfCategoryRuleLength'"));
        Assert.Equal(0, await ColumnCountAsync(connectionString, "CalculationRun", "InputsAsOfUtc"));
        Assert.Equal(-1, await ExpressionMaxLengthAsync(connectionString));
        Assert.Equal(4001, await ScalarAsync<int>(
            connectionString, $"SELECT DATALENGTH(Expression) / 2 FROM calc.CategoryRule WHERE Id = {longRule}"));

        // ── Фаза «розведено»: рішення оператора (runbook §8.7) - скоротити вирази ──────────────────────────────
        await ExecuteAsync(connectionString, $"UPDATE calc.CategoryRule SET Expression = N'1' WHERE Id IN ({longRule}, {trailing});");

        await MigrateAsync(connectionString);

        Assert.Equal(1, await ColumnCountAsync(connectionString, "CalculationRun", "InputsAsOfUtc", "datetime2", "YES"));
        Assert.Equal(3, await ScalarAsync<int>(
            connectionString,
            "SELECT DATETIME_PRECISION FROM INFORMATION_SCHEMA.COLUMNS "
            + "WHERE TABLE_SCHEMA = N'calc' AND TABLE_NAME = N'CalculationRun' AND COLUMN_NAME = N'InputsAsOfUtc'"));
        Assert.Equal(4000, await ExpressionMaxLengthAsync(connectionString));
        Assert.Equal(1, await ScalarAsync<int>(
            connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = N'calc' AND TABLE_NAME = N'CategoryRule' "
            + "AND COLUMN_NAME = N'Expression' AND IS_NULLABLE = N'NO'"));

        // Наявний прогін не зачеплено: NULL = «входи знято на старті».
        Assert.Equal(1, await ScalarAsync<int>(
            connectionString, $"SELECT COUNT(*) FROM calc.CalculationRun WHERE Id = {runId} AND InputsAsOfUtc IS NULL"));

        // Довге правило база тепер відхиляє сама (8152), а 4000 символів - законні.
        var rejected = await Record.ExceptionAsync(() => InsertRuleAsync(connectionString, versionId: 4, new string('d', 4001)));
        Assert.True(FindSqlException(rejected) is { Number: 2628 or 8152 }, $"очікувалось 2628/8152, а прийшло: {rejected}");
        await InsertRuleAsync(connectionString, versionId: 5, new string('e', 4000));

        // ── Фаза «вниз»: Down прибирає колонку й повертає nvarchar(max), і вгору знову проходить ───────────────
        await MigrateAsync(connectionString, BeforeMigration);
        Assert.Equal(0, await ColumnCountAsync(connectionString, "CalculationRun", "InputsAsOfUtc"));
        Assert.Equal(-1, await ExpressionMaxLengthAsync(connectionString));

        await MigrateAsync(connectionString);
        Assert.Equal(1, await ColumnCountAsync(connectionString, "CalculationRun", "InputsAsOfUtc"));
        Assert.Equal(4000, await ExpressionMaxLengthAsync(connectionString));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "AN-80-N2-02")]
    public async Task База_без_довгих_правил_проходить_ідемпотентний_скрипт_двічі_і_вдруге_нічого_не_змінює()
    {
        var connectionString = await CreateDatabaseBeforeMigrationAsync();
        await InsertRuleAsync(connectionString, versionId: 1, new string('a', 4000));

        await RunIdempotentScriptAsync(connectionString);
        Assert.Equal(1, await ColumnCountAsync(connectionString, "CalculationRun", "InputsAsOfUtc"));
        Assert.Equal(4000, await ExpressionMaxLengthAsync(connectionString));

        // Другий прогін - 0 змін: міграція вже записана, ні передперевірка, ні зміна схеми не повторюються.
        await RunIdempotentScriptAsync(connectionString);
        Assert.Equal(1, await ColumnCountAsync(connectionString, "CalculationRun", "InputsAsOfUtc"));
        Assert.Equal(1, await ScalarAsync<int>(
            connectionString,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]AN80CarryOverInputsAsOfCategoryRuleLength'"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "AN-80-N2-02")]
    public async Task Статистика_на_колонці_виразу_не_зупиняє_оновлення_вгору_і_вниз()
    {
        // ⛔ Перевірка оновлення RC16 → RC17 (10.10): на живій базі після sp_createstats (DBA чи
        // tools/Ecr.DataGen) ALTER COLUMN падав з Msg 5074 «statistics … dependent on column».
        // Без DropColumnStatisticsSql цей тест червоний.
        var connectionString = await CreateDatabaseBeforeMigrationAsync();
        await InsertRuleAsync(connectionString, versionId: 1, new string('a', 100));
        await ExecuteAsync(connectionString, "CREATE STATISTICS [Expression] ON calc.CategoryRule (Expression);");

        await MigrateAsync(connectionString);
        Assert.Equal(4000, await ExpressionMaxLengthAsync(connectionString));

        // Down повертає nvarchar(max) — і теж мусить пройти, якщо статистика з'явилась знову.
        await ExecuteAsync(connectionString, "CREATE STATISTICS [Expression] ON calc.CategoryRule (Expression);");
        await MigrateAsync(connectionString, BeforeMigration);
        Assert.Equal(-1, await ExpressionMaxLengthAsync(connectionString));

        // Ідемпотентний скрипт (шлях deploy-ecr.ps1 / setup-dev-db.ps1) — так само.
        await ExecuteAsync(connectionString, "CREATE STATISTICS [Expression] ON calc.CategoryRule (Expression);");
        await RunIdempotentScriptAsync(connectionString);
        Assert.Equal(4000, await ExpressionMaxLengthAsync(connectionString));
    }

    /// <summary>Скільки є колонки (опційно - з типом і прапорцем nullable).</summary>
    private static Task<int> ColumnCountAsync(
        string connectionString, string table, string column, string? dataType = null, string? nullable = null)
        => ScalarAsync<int>(
            connectionString,
            $"""
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = N'calc' AND TABLE_NAME = N'{table}' AND COLUMN_NAME = N'{column}'
              {(dataType is null ? string.Empty : $"AND DATA_TYPE = N'{dataType}'")}
              {(nullable is null ? string.Empty : $"AND IS_NULLABLE = N'{nullable}'")}
            """);

    /// <summary>Максимальна довжина <c>calc.CategoryRule.Expression</c>: -1 - <c>nvarchar(max)</c>.</summary>
    private static Task<int> ExpressionMaxLengthAsync(string connectionString)
        => ScalarAsync<int>(
            connectionString,
            "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS "
            + "WHERE TABLE_SCHEMA = N'calc' AND TABLE_NAME = N'CategoryRule' AND COLUMN_NAME = N'Expression'");

    /// <summary>
    /// Рядок <c>calc.CategoryRule</c>. Обмеження вимкнено: версій методології в цій базі немає, а перевіряється
    /// саме довжина виразу.
    /// </summary>
    /// <returns>Ідентифікатор вставленого рядка.</returns>
    private static async Task<int> InsertRuleAsync(string connectionString, int versionId, string expression)
    {
        await ExecuteAsync(connectionString, "ALTER TABLE calc.CategoryRule NOCHECK CONSTRAINT ALL;");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SET NOCOUNT ON;
            INSERT INTO calc.CategoryRule (MethodologyVersionId, Expression, CreatedAt, UpdatedAt)
            VALUES (@version, @expression, '2026-01-01', '2026-01-01');
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """;
        command.Parameters.AddWithValue("@version", versionId);
        command.Parameters.Add(new SqlParameter("@expression", System.Data.SqlDbType.NVarChar, -1) { Value = expression });

        return (int)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Прогін, що існував до міграції (обмеження вимкнено: проєктів у цій базі немає).</summary>
    private static async Task<long> InsertRunAsync(string connectionString)
    {
        await ExecuteAsync(connectionString, "ALTER TABLE calc.CalculationRun NOCHECK CONSTRAINT ALL;");

        return await ScalarAsync<long>(
            connectionString,
            """
            SET NOCOUNT ON;
            INSERT INTO calc.CalculationRun (ProjectId, PeriodKey, Status, StartedAt, FinishedAt)
            VALUES (1, 202601, N'Current', '2026-01-01', '2026-01-01');
            SELECT CAST(SCOPE_IDENTITY() AS bigint);
            """);
    }

    /// <summary>Своя база, зупинена на міграції перед цією.</summary>
    private async Task<string> CreateDatabaseBeforeMigrationAsync()
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var name = builder.InitialCatalog + "_AN80" + Guid.NewGuid().ToString("N")[..6];

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
        await MigrateAsync(builder.ConnectionString, BeforeMigration);

        return builder.ConnectionString;
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
