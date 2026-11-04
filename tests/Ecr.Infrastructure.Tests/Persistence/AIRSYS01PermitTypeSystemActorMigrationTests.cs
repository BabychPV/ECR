// tests/Ecr.Infrastructure.Tests/Persistence/AIRSYS01PermitTypeSystemActorMigrationTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ AIRSYS01 (coordinator 02.11): a published permit type may have NO publisher — the system (deploy-time step). Upgrade of a base
/// with existing published types: the rows stay as they are, the idempotent <c>migration.sql</c> runs twice cleanly, and afterwards
/// «published without a moment» is still refused while «published by the system (NULL)» is accepted.
/// </summary>
[Collection("SqlServer")]
public sealed class AIRSYS01PermitTypeSystemActorMigrationTests(SqlServerFixture sql)
{
    /// <summary>The migration right before AIRSYS01 (the lane base's last one).</summary>
    private const string Before = "20261102000813_METH01_MethodologyCategory";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Upgrade_keeps_published_types_and_lets_the_system_publish()
    {
        var connectionString = await CreateDatabaseBeforeAsync();
        try
        {
            // An existing published type, published by a real user — as on any customer base before the upgrade.
            int typeId, userId;
            await using (var db = CreateContext(connectionString))
            {
                var user = new User("airsys01", "airsys01", AuthProvider.Local);
                user.SetPassword(new Ecr.Infrastructure.Security.PasswordHasher().Hash("Airsys01-Test-2026!"));
                db.Users.Add(user);
                var header = new RegistryDef(EcrCode.Create("PRM_AIRSYS01"), Name("Header"), isTemporal: true);
                db.RegistryDefs.Add(header);
                await db.SaveChangesAsync();
                var type = new PermitTypeDef(EcrCode.Create("AIRSYS01"), Name("Type"), PermitCategory.Air, header.Id);
                type.SaveDraft("{}", user.Id, DateTime.UtcNow);
                type.Publish(user.Id, DateTime.UtcNow);
                db.PermitTypeDefs.Add(type);
                await db.SaveChangesAsync();
                (typeId, userId) = (type.Id, user.Id);
            }

            // Before the migration «published by nobody» is refused (the old check).
            Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => ExecAsync(
                connectionString, $"UPDATE cfg.PermitTypeDef SET PublishedByUserId = NULL WHERE Id = {typeId}"))).Number);

            // The deploy path: the idempotent script, twice (the second run changes nothing).
            await RunIdempotentScriptAsync(connectionString);
            await RunIdempotentScriptAsync(connectionString);

            Assert.Equal(1, await ScalarAsync<int>(connectionString,
                "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]AIRSYS01[_]PermitTypeSystemActor'"));
            Assert.Equal(userId, await ScalarAsync<int>(connectionString, $"SELECT PublishedByUserId FROM cfg.PermitTypeDef WHERE Id = {typeId}"));

            // After: the system may publish (NULL), a publication without a moment is still impossible.
            await ExecAsync(connectionString, $"UPDATE cfg.PermitTypeDef SET PublishedByUserId = NULL, UpdatedByUserId = NULL WHERE Id = {typeId}");
            Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => ExecAsync(
                connectionString, $"UPDATE cfg.PermitTypeDef SET PublishedAt = NULL WHERE Id = {typeId}"))).Number);
        }
        finally
        {
            await DropAsync(connectionString);
        }
    }

    private static LocalizedText Name(string en) => new(new Dictionary<string, string> { ["en"] = en });

    private async Task<string> CreateDatabaseBeforeAsync()
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var name = builder.InitialCatalog + "_AIRSYS01";
        await ExecAsync(
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
        await using var db = CreateContext(builder.ConnectionString);
        await db.GetService<IMigrator>().MigrateAsync(Before);
        return builder.ConnectionString;
    }

    private static async Task DropAsync(string connectionString)
    {
        var name = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        SqlConnection.ClearAllPools();
        await ExecAsync(
            new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString,
            $"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END");
    }

    /// <summary>The same <c>migration.sql</c> deploy-ecr.ps1 runs (<c>--idempotent</c>), by <c>GO</c> batches.</summary>
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

    private static EcrDbContext CreateContext(string connectionString)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo")).Options);

    private static async Task ExecAsync(string connectionString, string text)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string text)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
