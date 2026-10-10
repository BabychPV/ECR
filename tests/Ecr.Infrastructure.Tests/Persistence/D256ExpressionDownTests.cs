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
/// Відкат міграції <c>D256SmtpSettingsAndChannelRoles</c> на живих формулах (аудит L10-15, 2026-10-09).
/// </summary>
/// <remarks>
/// ⛔ Up розширює <c>calc.MethodologyFormula.Expression</c> до <c>nvarchar(4000)</c> (реальні формули — 2409 символів),
/// а Down звужував назад до 2000 і падав голим «String or binary data would be truncated» без жодного рядка. Тепер
/// Down починається з <c>THROW 50256</c> з переліком і до зміни схеми. Тест будує власну базу, піднімає її до D256
/// (як <see cref="CalculationRunDocumentScopeDownTests"/>), кладе довгу й коротку формулу й відкочує.
/// Мутація (CI): прибрати <c>migrationBuilder.Sql(DownPrecheckSql)</c> з Down → відкат падає 8152/4922, а не 50256: червоне.
/// </remarks>
[Collection("SqlServer")]
public sealed class D256ExpressionDownTests(SqlServerFixture sql)
{
    private const string Scope = "20261001163355_D256SmtpSettingsAndChannelRoles";

    /// <summary>Остання міграція перед <see cref="Scope"/>.</summary>
    private const string BeforeScope = "20261001120751_D234ColumnWidthPx";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Audit", "L10-15")]
    public async Task Down_на_формулі_довшій_за_2000_символів_зупиняється_THROW_з_переліком_і_не_чіпає_схему()
    {
        var connectionString = await CreateDatabaseAtScopeAsync();

        var longId = await InsertFormulaAsync(connectionString, "LONGF", length: 2409);
        var shortId = await InsertFormulaAsync(connectionString, "SHORTF", length: 100);

        var error = await Record.ExceptionAsync(() => MigrateAsync(connectionString, BeforeScope));

        var sqlError = FindSqlException(error);
        Assert.True(
            sqlError is { Number: D256SmtpSettingsAndChannelRoles.PrecheckErrorNumber },
            $"очікувалась передперевірка {D256SmtpSettingsAndChannelRoles.PrecheckErrorNumber}, а прийшло: "
            + (sqlError is null ? error?.ToString() ?? "жодної помилки" : $"SQL {sqlError.Number}: {sqlError.Message}"));

        // Перелік називає довгу формулу з її довжиною, а коротку — ні.
        var message = sqlError!.Message;
        Assert.Contains($"Id {longId.ToString(CultureInfo.InvariantCulture)} LONGF", message, StringComparison.Ordinal);
        Assert.Contains("2409 символів", message, StringComparison.Ordinal);
        Assert.DoesNotContain($"Id {shortId.ToString(CultureInfo.InvariantCulture)} ", message, StringComparison.Ordinal);

        // Схему й дані не змінено: колонка лишилась nvarchar(4000), текст цілий.
        Assert.Equal(4000, await ScalarAsync<int>(
            connectionString,
            "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS "
            + "WHERE TABLE_SCHEMA = N'calc' AND TABLE_NAME = N'MethodologyFormula' AND COLUMN_NAME = N'Expression'"));
        Assert.Equal(2409, await ScalarAsync<int>(
            connectionString,
            $"SELECT LEN(Expression) FROM calc.MethodologyFormula WHERE Id = {longId.ToString(CultureInfo.InvariantCulture)}"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Audit", "L10-15")]
    public async Task Down_без_довгих_формул_проходить_і_звужує_колонку()
    {
        var connectionString = await CreateDatabaseAtScopeAsync();
        await InsertFormulaAsync(connectionString, "FITS", length: 2000);

        await MigrateAsync(connectionString, BeforeScope);

        Assert.Equal(2000, await ScalarAsync<int>(
            connectionString,
            "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS "
            + "WHERE TABLE_SCHEMA = N'calc' AND TABLE_NAME = N'MethodologyFormula' AND COLUMN_NAME = N'Expression'"));
    }

    /// <summary>Формула заданої довжини; ключі таблиці вимкнено (версія методології не потрібна).</summary>
    /// <returns>Ідентифікатор формули.</returns>
    private static Task<int> InsertFormulaAsync(string connectionString, string code, int length)
        => ScalarAsync<int>(
            connectionString,
            $"""
            SET NOCOUNT ON;
            ALTER TABLE calc.MethodologyFormula NOCHECK CONSTRAINT ALL;
            INSERT INTO calc.MethodologyFormula (MethodologyVersionId, Code, Expression, ResultType)
            VALUES (1, N'{code}', REPLICATE(N'x', {length.ToString(CultureInfo.InvariantCulture)}), 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """);

    /// <summary>Своя база, піднята рівно до <see cref="Scope"/>.</summary>
    private async Task<string> CreateDatabaseAtScopeAsync()
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var name = builder.InitialCatalog + "_L1015D";

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
        await MigrateAsync(builder.ConnectionString, Scope);

        return builder.ConnectionString;
    }

    private static async Task MigrateAsync(string connectionString, string target)
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);
        await db.GetService<IMigrator>().MigrateAsync(target);
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
}
