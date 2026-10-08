using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>SET LOCK_TIMEOUT</c> з <c>LockWaitGuard</c> не витікає у з'єднання пулу (N-3, питання «Аудиту»).
/// </summary>
/// <remarks>
/// ⚠ <c>Max Pool Size=1</c> і окремий <c>Application Name</c> (власний пул): наступне відкриття гарантовано
/// бере ТЕ САМЕ фізичне з'єднання — це перевіряється збігом <c>@@SPID</c>, а не припускається.
/// </remarks>
[Collection("SqlServer")]
public sealed class LockWaitGuardPoolTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ліміт_очікування_діє_до_кінця_транзакції_і_не_витікає_в_пул()
    {
        var ct = CancellationToken.None;
        var pooled = new SqlConnectionStringBuilder(sql.ConnectionString)
        {
            Pooling = true,
            MaxPoolSize = 1,
            ApplicationName = $"lockwait-pool-{Guid.NewGuid():N}",
        }.ConnectionString;

        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(rowCount: 2, ct: ct);
        var builder = new TestDocumentBuilder(pooled);

        int spidInside;
        int insideTransaction;
        await using (var db = builder.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var address = new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]);
            await new NormalizedCellStore(db).ApplyAsync(
                new CellChangeSet(
                    doc.TableInstanceId,
                    [new CellRecord(address, doc.TableDefId, new CellValueData { ValueNumeric = 1m })],
                    [],
                    [doc.RowIds[0]],
                    ChangedByUserId: 1,
                    IsLateEdit: false),
                ct);

            (spidInside, insideTransaction) = await ReadAsync(db.Database.GetDbConnection(), tx.GetDbTransaction());
            await tx.RollbackAsync(ct);
        }

        // Те саме фізичне з'єднання з пулу.
        await using var again = new SqlConnection(pooled);
        await again.OpenAsync(ct);
        var (spidAfter, afterPool) = await ReadAsync(again, null);

        Assert.Equal(spidInside, spidAfter);

        // (2) Ambient-транзакція: ліміт діє на решту її операторів.
        Assert.Equal(8000, insideTransaction);

        // (1) Після повернення в пул — звичайне -1.
        Assert.Equal(-1, afterPool);
    }

    private static async Task<(int Spid, int LockTimeout)> ReadAsync(
        System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? tx)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT CONVERT(int, @@SPID), CONVERT(int, @@LOCK_TIMEOUT);";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt32(0), reader.GetInt32(1));
    }
}
