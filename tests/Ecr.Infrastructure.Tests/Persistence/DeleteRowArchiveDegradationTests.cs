using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// D3-A (P1 «Аудиту»): службовий акаунт без <c>SELECT</c> на <c>arc.AuditCellChange</c> (D-264: DBA дає лише
/// <c>EXECUTE</c> на <c>arc.usp_*</c>) НЕ дає 500 на кожне видалення рядка — перевірка машинного походження
/// деградує до гарячого журналу (SqlException 229/230 → повтор без архіву), як <c>AuditReader</c>.
/// </summary>
/// <remarks>
/// ⚠ Справжня відмова в праві: користувач бази без логіна, `DENY SELECT` на архів, `EXECUTE AS USER`.
/// Напрям рішення НЕ змінено: за деградації блок міряється лише за гарячим журналом — архівна машинна
/// зміна без права читання архіву не блокує (прийнято; див. коментар `RowStore.HasMachineOriginAsync`).
/// </remarks>
[Collection("SqlServer")]
public sealed class DeleteRowArchiveDegradationTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D3-A D-264")]
    public async Task Без_права_на_архів_журналу_перевірка_деградує_до_гарячого_а_не_падає()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(rowCount: 3, rowMode: Ecr.Domain.Enums.TableRowMode.Mixed, ct: CancellationToken.None);
        var user = $"arc_deny_{Guid.NewGuid():N}"[..24];
        var key = doc.PeriodKey.Value;

        // Рядок 0: гарячий Integration; рядок 1: ЛИШЕ архівний Integration; рядок 2: нічого.
        await ExecuteAsync(
            "INSERT INTO aud.CellChange (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue, "
            + "ChangedByUserId, Origin, IsLateEdit, IsOutOfWindow) VALUES "
            + $"('2026-01-10 09:00:00', {key}, {doc.DocumentId}, {doc.RowIds[0]}, N'r0', {doc.ColumnDefIds[1]}, NULL, N'1', 1, N'Integration', 0, 0);\n"
            + "INSERT INTO arc.AuditCellChange (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue, "
            + "ChangedByUserId, Origin, IsLateEdit, IsOutOfWindow) VALUES "
            + $"('2026-01-10 09:00:00', {key}, {doc.DocumentId}, {doc.RowIds[1]}, N'r1', {doc.ColumnDefIds[1]}, NULL, N'1', 1, N'Integration', 0, 0);");

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        await db.Database.OpenConnectionAsync();
        var store = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new FixedClock());

        try
        {
            await ExecOnAsync(db,
                $"CREATE USER [{user}] WITHOUT LOGIN; GRANT SELECT ON aud.CellChange TO [{user}]; "
                + $"DENY SELECT ON arc.AuditCellChange TO [{user}];");

            // Контроль: без імперсонації (повні права) архівний Integration БАЧИТЬСЯ.
            Assert.True(await store.HasMachineOriginAsync(doc.DocumentId, doc.PeriodKey, doc.RowIds[1], default));

            await ExecOnAsync(db, $"EXECUTE AS USER = '{user}';");

            Assert.True(await store.HasMachineOriginAsync(doc.DocumentId, doc.PeriodKey, doc.RowIds[0], default));
            // Деградація: архів недоступний → лише гарячий; архівний-тільки не блокує (прийнято).
            Assert.False(await store.HasMachineOriginAsync(doc.DocumentId, doc.PeriodKey, doc.RowIds[1], default));
            Assert.False(await store.HasMachineOriginAsync(doc.DocumentId, doc.PeriodKey, doc.RowIds[2], default));
        }
        finally
        {
            await ExecOnAsync(db, "IF USER_NAME() <> N'dbo' REVERT;");
            await ExecOnAsync(db, $"DROP USER IF EXISTS [{user}];");
        }
    }

    /// <summary>Команда на ТОМУ САМОМУ з'єднанні контексту (потрібно для `EXECUTE AS`).</summary>
    private static async Task ExecOnAsync(Microsoft.EntityFrameworkCore.DbContext db, string text)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await command.ExecuteNonQueryAsync();
    }

    private async Task ExecuteAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixedClock : Ecr.Domain.Abstractions.IClock
    {
        public DateTime UtcNow { get; } = new(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc);
    }
}