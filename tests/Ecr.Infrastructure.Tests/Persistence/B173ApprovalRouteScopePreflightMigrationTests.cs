// tests/Ecr.Infrastructure.Tests/Persistence/B173ApprovalRouteScopePreflightMigrationTests.cs
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
/// B173: міграція <c>B155TemplateApprovalRoute</c> додає унікальний <c>UQ_AR_Scope (ProjectId, TemplateId)
/// WHERE TemplateVersionId IS NULL</c>; до неї <c>TemplateId</c> порожній, тож кілька маршрутів без версії на
/// проєкт (чи кілька типових) зупиняють міграцію <c>THROW 50155</c> з переліком ДО зміни схеми. Автоматичного
/// видалення немає: це дані, рішення людини.
/// </summary>
/// <remarks>
/// Мутація (2026-11-18): прибрати <c>Sql(PreflightSql)</c> - тести відмови дають 1505/2601 замість 50155.
/// </remarks>
[Collection("SqlServer")]
public sealed class B173ApprovalRouteScopePreflightMigrationTests(SqlServerFixture sql)
{
    private const string BeforeMigration = "20261117152942_B80DocumentSourceBinding";
    private const int PreflightErrorNumber = 50155;
    private const string IndexName = "UQ_AR_Scope";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B173")]
    public async Task Два_маршрути_без_версії_на_один_проєкт_зупиняють_міграцію_з_переліком_обома_шляхами()
    {
        var cs = await CreateDatabaseBeforeMigrationAsync();
        await InsertAsync(cs, "AR-B", 5, null, true);
        await InsertAsync(cs, "AR-A", 5, null, true);
        await InsertAsync(cs, "AR-V", 5, 77, true);      // версійний - не дубль
        await InsertAsync(cs, "AR-OTHER", 6, null, true); // інший проєкт - не дубль

        foreach (var (path, run) in new (string, Func<Task>)[]
                 { ("migration.sql", () => RunIdempotentScriptAsync(cs)), ("MigrateAsync", () => MigrateAsync(cs)) })
        {
            var error = FindSqlException(await Record.ExceptionAsync(run));
            Assert.True(error is { Number: PreflightErrorNumber }, $"{path}: очікувалось {PreflightErrorNumber}, а прийшло: {error?.Number} {error?.Message}");
            Assert.Contains("проєкт 5", error!.Message, StringComparison.Ordinal);
            Assert.Contains("AR-A (активний), ", error.Message, StringComparison.Ordinal);
            Assert.Contains("AR-B (активний)", error.Message, StringComparison.Ordinal);
            Assert.True(
                error.Message.IndexOf("AR-A", StringComparison.Ordinal) < error.Message.IndexOf("AR-B", StringComparison.Ordinal),
                "маршрути за кодом");
            Assert.DoesNotContain("AR-OTHER", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("AR-V", error.Message, StringComparison.Ordinal);
            Assert.Contains("operations-runbook.md", error.Message, StringComparison.Ordinal);
        }

        // Зупинка ДО зміни схеми: міграція не записана, стовпця й індексу немає, дані на місці.
        Assert.Equal(0, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]B155TemplateApprovalRoute'"));
        Assert.Equal(0, await ScalarAsync<int>(cs, $"SELECT COUNT(*) FROM sys.indexes WHERE name = N'{IndexName}'"));
        Assert.Equal(4, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM wf.ApprovalRoute"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B173")]
    public async Task Два_типові_маршрути_майданчика_без_проєкту_теж_зупиняють_міграцію()
    {
        var cs = await CreateDatabaseBeforeMigrationAsync();
        await InsertAsync(cs, "SITE-1", null, null, true);
        await InsertAsync(cs, "SITE-2", null, null, false);

        var error = FindSqlException(await Record.ExceptionAsync(() => MigrateAsync(cs)));
        Assert.True(error is { Number: PreflightErrorNumber }, $"очікувалось {PreflightErrorNumber}, а прийшло: {error?.Number} {error?.Message}");
        Assert.Contains("типовий маршрут майданчика", error!.Message, StringComparison.Ordinal);
        Assert.Contains("SITE-1 (активний)", error.Message, StringComparison.Ordinal);
        Assert.Contains("SITE-2 (вимкнений)", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]B155TemplateApprovalRoute'"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B173")]
    public async Task Маршрут_проєкту_типовий_і_версійний_того_ж_проєкту_проходять_і_індекс_тримає()
    {
        var cs = await CreateDatabaseBeforeMigrationAsync();
        await InsertAsync(cs, "P5", 5, null, true);
        await InsertAsync(cs, "SITE", null, null, true);
        await InsertAsync(cs, "P5-V1", 5, 77, true);
        await InsertAsync(cs, "P5-V2", 5, 78, true);

        await RunIdempotentScriptAsync(cs);
        Assert.Equal(1, await ScalarAsync<int>(cs, $"SELECT COUNT(*) FROM sys.indexes WHERE name = N'{IndexName}' AND is_unique = 1 AND has_filter = 1"));

        // Повторний прогін - 0 змін.
        await RunIdempotentScriptAsync(cs);

        var rejected = FindSqlException(await Record.ExceptionAsync(
            () => ExecuteAsync(cs, "INSERT INTO wf.ApprovalRoute (Code, NameL10n, ProjectId, TemplateVersionId, IsActive) VALUES (N'DUP', N'n', 5, NULL, 1);")));
        Assert.True(rejected is { Number: 2601 or 2627 }, $"очікувалось 2601/2627, а прийшло: {rejected?.Number}");
    }

    /// <summary>Рядок <c>wf.ApprovalRoute</c>; зовнішні ключі вимкнено - перевіряється лише унікальність.</summary>
    private static async Task InsertAsync(string cs, string code, int? projectId, int? versionId, bool active)
    {
        var p = projectId?.ToString(CultureInfo.InvariantCulture) ?? "NULL";
        var v = versionId?.ToString(CultureInfo.InvariantCulture) ?? "NULL";
        await ExecuteAsync(
            cs,
            $"""
            ALTER TABLE wf.ApprovalRoute NOCHECK CONSTRAINT ALL;
            INSERT INTO wf.ApprovalRoute (Code, NameL10n, ProjectId, TemplateVersionId, IsActive)
            VALUES (N'{code}', N'n', {p}, {v}, {(active ? 1 : 0)});
            """);
    }
    /// <summary>Своя база, зупинена на міграції перед цією.</summary>
    private async Task<string> CreateDatabaseBeforeMigrationAsync()
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var name = builder.InitialCatalog + "_B173" + Guid.NewGuid().ToString("N")[..6];

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
                CREATE DATABASE [{name}]{TestDatabaseFiles.Clause(name)} COLLATE Latin1_General_100_CI_AS_SC;
                """;
            await command.ExecuteNonQueryAsync();
        }

        builder.InitialCatalog = name;
        await MigrateAsync(builder.ConnectionString, BeforeMigration);

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
