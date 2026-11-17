using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// B166: класифікатор повтору фікстури на СПРАВЖНІЙ <see cref="SqlException"/> — живий замок дає 1222 (тайм-аут замка),
/// повтор після звільнення замка проходить; ділення на нуль (8134) летить одразу, без повтору.
/// </summary>
/// <remarks>
/// Замок — на глобальній тимчасовій таблиці в <c>tempdb</c> (RCSI там вимкнено, тож читач справді чекає замка), а не на
/// спільній базі колекції. <c>SET LOCK_TIMEOUT 0</c> — відмова одразу, без очікування.
/// </remarks>
[Collection("SqlServer")]
public sealed class TransientSqlRetryRealSqlTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Тайм_аут_замка_1222_повторюється_і_проходить_після_звільнення_замка()
    {
        var table = "##b166_" + Guid.NewGuid().ToString("N");

        await using var holder = new SqlConnection(sql.ConnectionString);
        await holder.OpenAsync();
        await ExecAsync(holder, null, $"CREATE TABLE {table} (Id int NOT NULL); INSERT {table} VALUES (1);");
        await using var lockTx = holder.BeginTransaction();
        await ExecAsync(holder, lockTx, $"UPDATE {table} SET Id = 2;");

        var attempts = 0;
        var firstErrors = new List<int>();
        await TransientSqlRetry.RunAsync(
            "читання під замком",
            async number =>
            {
                attempts++;
                await using var reader = new SqlConnection(sql.ConnectionString);
                await reader.OpenAsync();
                try
                {
                    await ExecAsync(reader, null, $"SET LOCK_TIMEOUT 0; SELECT Id FROM {table};");
                }
                catch (SqlException ex) when (number == 1)
                {
                    firstErrors.AddRange(TransientSqlRetry.ErrorNumbers(ex));
                    throw;
                }
            },
            TransientSqlRetry.SqlClassifier(TransientSqlRetry.DdlLockErrors),
            TransientSqlRetry.Ddl,
            delay: (_, ct) => lockTx.CommitAsync(ct));

        Assert.Equal(2, attempts);
        Assert.Contains(1222, firstErrors);

        await ExecAsync(holder, null, $"DROP TABLE {table};");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Справжня_неперехідна_помилка_8134_летить_одразу()
    {
        var attempts = 0;

        var thrown = await Assert.ThrowsAsync<SqlException>(() => TransientSqlRetry.RunAsync(
            "ділення на нуль",
            async _ =>
            {
                attempts++;
                await using var connection = new SqlConnection(sql.ConnectionString);
                await connection.OpenAsync();
                await ExecAsync(connection, null, "SELECT 1 / 0;");
            },
            TransientSqlRetry.SqlClassifier(TransientSqlRetry.DdlLockErrors),
            TransientSqlRetry.Ddl,
            delay: (_, _) => Task.CompletedTask));

        Assert.Equal(8134, thrown.Number);
        Assert.Equal(1, attempts);
    }

    private static async Task ExecAsync(SqlConnection connection, SqlTransaction? tx, string text)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = text;
        await command.ExecuteNonQueryAsync();
    }
}
