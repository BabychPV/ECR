using Ecr.Application.Errors;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>LockWaitGuard</c> перетворює на 409 лише вичерпаний <c>LOCK_TIMEOUT</c> (1222); повільний
/// запит без блокувань, що впирається в клієнтський <c>CommandTimeout</c> (-2), лишається
/// <c>SqlException</c> (N-3, P2-a).
/// </summary>
[Collection("SqlServer")]
public sealed class LockWaitGuardSlowQueryTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повільний_запит_без_блокувань_не_стає_409()
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync();

        var error = await Assert.ThrowsAsync<SqlException>(() => LockWaitGuard.RunAsync(
            connection,
            tx,
            async () =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = tx;
                command.CommandTimeout = 2;
                command.CommandText = "WAITFOR DELAY '00:00:06';";
                await command.ExecuteNonQueryAsync();
            },
            CancellationToken.None));

        Assert.Equal(-2, error.Number);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Вичерпаний_lock_timeout_стає_409()
    {
        var table = $"##lockwait_{Guid.NewGuid():N}";

        await using var holder = new SqlConnection(sql.ConnectionString);
        await holder.OpenAsync();
        await using (var create = holder.CreateCommand())
        {
            create.CommandText = $"CREATE TABLE {table} (Id int); INSERT {table} VALUES (1);";
            await create.ExecuteNonQueryAsync();
        }

        await using var holderTx = (SqlTransaction)await holder.BeginTransactionAsync();
        try
        {
            await using (var lockIt = holder.CreateCommand())
            {
                lockIt.Transaction = holderTx;
                lockIt.CommandText = $"UPDATE {table} SET Id = 2;";
                await lockIt.ExecuteNonQueryAsync();
            }

            await using var waiter = new SqlConnection(sql.ConnectionString);
            await waiter.OpenAsync();
            await using var waiterTx = (SqlTransaction)await waiter.BeginTransactionAsync();

            // Скорочений ліміт всередині тіла: після guard'ового SET ставимо 300 мс, щоб тест не чекав 15 с.
            var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => LockWaitGuard.RunAsync(
                waiter,
                waiterTx,
                async () =>
                {
                    await using var shorten = waiter.CreateCommand();
                    shorten.Transaction = waiterTx;
                    shorten.CommandText = "SET LOCK_TIMEOUT 300;";
                    await shorten.ExecuteNonQueryAsync();

                    await using var read = waiter.CreateCommand();
                    read.Transaction = waiterTx;
                    read.CommandText = $"UPDATE {table} SET Id = 3;";
                    await read.ExecuteNonQueryAsync();
                },
                CancellationToken.None));

            Assert.Equal("ECR-DOC-4091", error.ErrorCode);
            Assert.Equal(LockWaitGuard.MessageKey, error.Details!["messageKey"]);
        }
        finally
        {
            await holderTx.RollbackAsync();
        }
    }
}
