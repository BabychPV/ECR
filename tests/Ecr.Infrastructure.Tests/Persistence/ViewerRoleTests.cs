// tests/Ecr.Infrastructure.Tests/Persistence/ViewerRoleTests.cs
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// AN-10 (D-258): роль бази <c>ecr_viewer</c> («бачить усе», лише читання) з
/// <c>05-rpt-views.sql</c>. Порожня за замовчуванням; член читає КОЖНУ схему
/// додатку й не може ні змінювати схему, ні писати.
/// Мутаційний доказ: прибрати з 05-rpt-views.sql <c>ALTER ROLE db_datareader ADD MEMBER
/// ecr_viewer</c> — тест червоний (member не читає схеми).
/// </summary>
[Collection("SqlServer")]
public sealed class ViewerRoleTests(SqlServerFixture sql)
{
    private const string Role = "ecr_viewer";

    private static readonly string[] ReadOnlyForbidden = ["INSERT", "UPDATE", "DELETE", "ALTER", "EXECUTE"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Роль_ecr_viewer_існує_і_порожня_за_замовчуванням()
    {
        await using var connection = await OpenAsync();

        Assert.Equal(1, await ScalarAsync<int>(connection,
            $"SELECT COUNT(*) FROM sys.database_principals WHERE name = N'{Role}' AND type = 'R';"));
        Assert.Equal(0, await ScalarAsync<int>(connection,
            $"SELECT COUNT(*) FROM sys.database_role_members WHERE role_principal_id = DATABASE_PRINCIPAL_ID(N'{Role}') AND member_principal_id <> ISNULL(DATABASE_PRINCIPAL_ID(N'viewer_probe'), 0);"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Член_ecr_viewer_читає_кожну_схему_додатку_і_не_може_змінювати_чи_писати()
    {
        await using var connection = await OpenAsync();
        const string user = "viewer_probe";

        await ExecAsync(connection, $"IF DATABASE_PRINCIPAL_ID(N'{user}') IS NOT NULL DROP USER [{user}];");
        await ExecAsync(connection, $"CREATE USER [{user}] WITHOUT LOGIN; ALTER ROLE {Role} ADD MEMBER [{user}];");
        try
        {
            // Схеми додатку = схеми, у яких є таблиці чи вʼюхи (без системних).
            var schemas = new List<(string Schema, string Object)>();
            await using (var cmd = new SqlCommand(
                """
                SELECT s.name, MIN(o.name)
                FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
                WHERE o.type IN ('U','V') AND o.is_ms_shipped = 0
                  AND s.name NOT IN ('sys','INFORMATION_SCHEMA')
                GROUP BY s.name;
                """, connection))
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    schemas.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            Assert.True(schemas.Count >= 8, "очікувано ≥ 8 схем додатку: " + string.Join(", ", schemas.Select(s => s.Schema)));
            Assert.Contains(schemas, s => s.Schema == "rpt");
            Assert.Contains(schemas, s => s.Schema == "doc");

            await ExecAsync(connection, $"EXECUTE AS USER = N'{user}';");
            try
            {
                var problems = new List<string>();
                foreach (var (schema, obj) in schemas)
                {
                    if (await ScalarAsync<int?>(connection, $"SELECT HAS_PERMS_BY_NAME(N'{schema}', 'SCHEMA', 'SELECT');") != 1)
                    {
                        problems.Add($"немає SELECT на схему {schema}");
                    }

                    foreach (var perm in ReadOnlyForbidden)
                    {
                        if (await ScalarAsync<int?>(connection, $"SELECT HAS_PERMS_BY_NAME(N'{schema}', 'SCHEMA', '{perm}');") == 1)
                        {
                            problems.Add($"є зайве право {perm} на схему {schema}");
                        }
                    }

                    try
                    {
                        await ExecAsync(connection, $"SELECT TOP 1 * FROM [{schema}].[{obj}];");
                    }
                    catch (SqlException e)
                    {
                        problems.Add($"SELECT TOP 1 з {schema}.{obj}: {e.Message}");
                    }
                }

                Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

                Assert.Equal(1, await ScalarAsync<int?>(connection, "SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION');"));
                foreach (var perm in new[] { "ALTER", "CREATE TABLE", "CREATE VIEW", "CREATE SCHEMA", "CONTROL" })
                {
                    Assert.NotEqual(1, await ScalarAsync<int?>(connection, $"SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', '{perm}');"));
                }

                // Реальна відмова, а не лише відсутність права у метаданих.
                var create = await Assert.ThrowsAsync<SqlException>(
                    () => ExecAsync(connection, "CREATE TABLE dbo.viewer_must_fail (Id int);"));
                Assert.Equal(262, create.Number);
                var (writeSchema, writeObject) = schemas.First(s => s.Schema == "doc");
                var write = await Assert.ThrowsAsync<SqlException>(
                    () => ExecAsync(connection, $"DELETE FROM [{writeSchema}].[{writeObject}];"));
                Assert.Equal(229, write.Number);
            }
            finally
            {
                await ExecAsync(connection, "REVERT;");
            }
        }
        finally
        {
            await ExecAsync(connection, $"IF DATABASE_PRINCIPAL_ID(N'{user}') IS NOT NULL DROP USER [{user}];");
        }
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecAsync(SqlConnection connection, string text)
    {
        await using var cmd = new SqlCommand(text, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T?> ScalarAsync<T>(SqlConnection connection, string text)
    {
        await using var cmd = new SqlCommand(text, connection);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }
}
