// tests/Ecr.Infrastructure.Tests/Persistence/CalculationRunDocumentScopeDownTests.cs
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
/// Відкат міграції <c>CalculationRunDocumentScope</c> на живих даних (аудит L10-15).
/// </summary>
/// <remarks>
/// ⛔ Після Up в одного <c>(ProjectId, PeriodKey)</c> законно кілька <c>Current</c>:
/// прогін проєкту й по одному на документ. Down повертає старий унікальний індекс
/// без <c>DocumentId</c> — і без підготовки падав 1505 саме там, де відкат потрібен,
/// тобто на базі з даними. Тест будує власну базу (як <see cref="U1UnitForeignKeysMigrationTests"/>):
/// у спільній після Up уже лежать чужі міграції, і відкат на ній неможливий.
///
/// Мутація: прибрати <c>UPDATE … Superseded</c> з Down — відкат падає 1505: червоне.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationRunDocumentScopeDownTests(SqlServerFixture sql)
{
    private const string Scope = "20260925060946_CalculationRunDocumentScope";

    /// <summary>Остання міграція перед <see cref="Scope"/>.</summary>
    private const string BeforeScope = "20260925024920_B18HotPathIndexes";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Audit", "L10-15")]
    public async Task Down_на_двох_документних_Current_проходить_і_лишає_актуальним_прогін_проєкту()
    {
        var connectionString = await CreateDatabaseAtScopeAsync();

        var projectRun = await InsertRunAsync(connectionString, documentId: null);
        var documentRunA = await InsertRunAsync(connectionString, documentId: 10);
        var documentRunB = await InsertRunAsync(connectionString, documentId: 11);

        await MigrateAsync(connectionString, BeforeScope);

        Assert.Equal("Current", await StatusAsync(connectionString, projectRun));
        Assert.Equal("Superseded", await StatusAsync(connectionString, documentRunA));
        Assert.Equal("Superseded", await StatusAsync(connectionString, documentRunB));

        // Старий індекс на місці й тримає: у (ProjectId, PeriodKey) рівно один Current.
        Assert.Equal(1, await ScalarAsync<int>(
            connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE name = N'UX_CalculationRun_Current' "
            + "AND object_id = OBJECT_ID(N'calc.CalculationRun') AND is_unique = 1"));

        // І вгору знову проходить.
        await MigrateAsync(connectionString, Scope);
        Assert.Equal(1, await ScalarAsync<int>(
            connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'calc.CalculationRun') AND name = N'DocumentId'"));
    }

    /// <summary>Прогін зі статусом <c>Current</c> в одній області; ключі таблиці вимкнено.</summary>
    /// <returns>Ідентифікатор прогону.</returns>
    private static Task<long> InsertRunAsync(string connectionString, long? documentId)
        => ScalarAsync<long>(
            connectionString,
            $"""
            SET NOCOUNT ON;
            ALTER TABLE calc.CalculationRun NOCHECK CONSTRAINT ALL;
            INSERT INTO calc.CalculationRun (ProjectId, PeriodKey, DocumentId, Status, StartedAt)
            VALUES (1, 202601, {(documentId is null ? "NULL" : documentId.Value.ToString(CultureInfo.InvariantCulture))},
                    N'Current', '2026-02-01');
            SELECT CAST(SCOPE_IDENTITY() AS bigint);
            """);

    private static Task<string> StatusAsync(string connectionString, long runId)
        => ScalarAsync<string>(
            connectionString,
            $"SELECT Status FROM calc.CalculationRun WHERE Id = {runId.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>Своя база, піднята рівно до <see cref="Scope"/>.</summary>
    private async Task<string> CreateDatabaseAtScopeAsync()
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var name = builder.InitialCatalog + "_L1015";

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
}
