// tests/Ecr.Infrastructure.Tests/Persistence/HFIDX01HeaderFieldDefLookupIndexMigrationTests.cs
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Міграція <c>HFIDX01_HeaderFieldDefLookupIndex</c> (PM-GRP-IDX): індекс
/// <c>IX_HeaderFieldDef_LookupRegistryDefId</c> для запиту «які поля шапки посилаються на реєстр R»
/// (група пермітів, перевірка видалення, PermitGroupRegistryGuard). Тест будує ВЛАСНУ базу,
/// зупинену на міграції перед цією, і перевіряє: індексу ще немає → Up будує його одноколонковим
/// не унікальним індексом, Down знімає рівно його, UQ_HeaderFieldDef не зачеплено.
/// </summary>
/// <remarks>
/// Мутація (власний worktree): прибрати <c>HasIndex(x =&gt; x.LookupRegistryDefId)</c> з
/// <c>HeaderFieldDefConfiguration</c> і перегенерувати міграцію — індексу немає, тест червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class HFIDX01HeaderFieldDefLookupIndexMigrationTests(SqlServerFixture sql)
{
    private const string BeforeMigration = "20261022064702_PRMG01_PermitGroups";
    private const string IndexName = "IX_HeaderFieldDef_LookupRegistryDefId";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "PM-GRP-IDX")]
    public async Task Індекс_будується_Up_одноколонковий_не_унікальний_Down_знімає_рівно_його()
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var name = builder.InitialCatalog + "_HFI" + Guid.NewGuid().ToString("N")[..6];
        await ExecuteAsync(
            new SqlConnectionStringBuilder(sql.ConnectionString) { InitialCatalog = "master" }.ConnectionString,
            $"CREATE DATABASE [{name}] COLLATE Latin1_General_100_CI_AS_SC;");
        builder.InitialCatalog = name;
        var cs = builder.ConnectionString;
        try
        {
            await MigrateAsync(cs, BeforeMigration);
            Assert.Equal(0, await ScalarAsync(cs, CountSql(IndexName)));
            Assert.Equal(1, await ScalarAsync(cs, CountSql("UQ_HeaderFieldDef")));

            await MigrateAsync(cs, null);
            Assert.Equal(1, await ScalarAsync(cs, CountSql(IndexName)));
            Assert.Equal(1, await ScalarAsync(cs, CountSql("UQ_HeaderFieldDef")));
            Assert.Equal(0, await ScalarAsync(cs, $"SELECT is_unique FROM sys.indexes WHERE name = N'{IndexName}'"));
            Assert.Equal(1, await ScalarAsync(cs,
                "SELECT COUNT(*) FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id = ic.object_id "
                + $"AND i.index_id = ic.index_id JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id "
                + $"WHERE i.name = N'{IndexName}' AND c.name = N'LookupRegistryDefId' AND ic.is_included_column = 0"));
            Assert.Equal(1, await ScalarAsync(cs,
                $"SELECT COUNT(*) FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id WHERE i.name = N'{IndexName}' AND ic.is_included_column = 0"));

            await MigrateAsync(cs, BeforeMigration);
            Assert.Equal(0, await ScalarAsync(cs, CountSql(IndexName)));
            Assert.Equal(1, await ScalarAsync(cs, CountSql("UQ_HeaderFieldDef")));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await ExecuteAsync(
                new SqlConnectionStringBuilder(sql.ConnectionString) { InitialCatalog = "master" }.ConnectionString,
                $"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END");
        }
    }

    private static string CountSql(string index)
        => $"SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'cfg.HeaderFieldDef') AND name = N'{index}'";

    private static async Task MigrateAsync(string connectionString, string? target)
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private static async Task ExecuteAsync(string connectionString, string text)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(string connectionString, string query)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        command.CommandTimeout = 300;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
