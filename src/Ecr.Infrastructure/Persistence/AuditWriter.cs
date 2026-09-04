using System.Data;
using System.Globalization;
using System.Text;
using Ecr.Application.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Запис аудиту в схему <c>aud</c>.
/// </summary>
/// <remarks>
/// ⚠ Пакетно **навмисно**: окремий <c>INSERT</c> на кожну комірку не
/// вкладається в бюджет збереження діапазону (300 мс на 100 комірок). Один
/// багаторядковий <c>INSERT</c> на весь батч — це один похід до сервера
/// замість ста.
///
/// Аудит пишеться в **тій самій транзакції**, що й дані: журнал, який може
/// розійтися з тим, що він описує, доказом не є.
///
/// ⚠ Файла немає в дереві `05-skeleton.md` §1 (`Q-050`); таблиці `aud.*`
/// створює `11-audit-tables.sql` (`Q-049`).
/// </remarks>
public sealed class AuditWriter(EcrDbContext db) : IAuditWriter
{
    /// <summary>
    /// Скільки рядків іде в один <c>INSERT</c>.
    /// </summary>
    /// <remarks>
    /// 13 параметрів на рядок проти ліміту 2100 на запит: 150 × 13 = 1950.
    /// </remarks>
    private const int ChunkSize = 150;

    /// <inheritdoc />
    public async Task WriteCellChangesAsync(IReadOnlyList<CellChangeRecord> changes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return;
        }

        foreach (var chunk in changes.Chunk(ChunkSize))
        {
            await using var command = CreateCommand();
            var values = new StringBuilder();

            for (var i = 0; i < chunk.Length; i++)
            {
                var c = chunk[i];
                if (i > 0)
                {
                    values.Append(',');
                }

                values.Append(CultureInfo.InvariantCulture,
                    $"(@t{i},@p{i},@d{i},@r{i},@k{i},@c{i},@o{i},@n{i},@u{i},@g{i},@l{i},@x{i})");

                command.Parameters.AddWithValue($"@t{i}", c.ChangedAt);
                command.Parameters.AddWithValue($"@p{i}", c.Address.PeriodKey.Value);
                command.Parameters.AddWithValue($"@d{i}", c.DocumentId);
                command.Parameters.AddWithValue($"@r{i}", c.Address.TableRowId);
                command.Parameters.AddWithValue($"@k{i}", c.RowKey);
                command.Parameters.AddWithValue($"@c{i}", c.Address.ColumnDefId);
                AddNullable(command, $"@o{i}", c.OldValue);
                AddNullable(command, $"@n{i}", c.NewValue);
                command.Parameters.AddWithValue($"@u{i}", c.ChangedByUserId);
                command.Parameters.AddWithValue($"@g{i}", c.Origin);
                command.Parameters.AddWithValue($"@l{i}", c.IsLateEdit);
                AddNullable(command, $"@x{i}", c.CorrelationId);
            }

            command.CommandText = $"""
                INSERT INTO aud.CellChange
                    (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId,
                     OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit, CorrelationId)
                VALUES {values};
                """;

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task WriteStructureChangeAsync(StructureChangeRecord change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);

        await using var command = CreateCommand();
        command.CommandText = """
            INSERT INTO aud.StructureChange
                (ChangedAt, TemplateVersionId, EntityType, EntityId, ChangeClass, Operation,
                 OldJson, NewJson, ChangeReason, ChangedByUserId, CorrelationId)
            VALUES (@t, @v, @et, @ei, @cc, @op, @oj, @nj, @cr, @u, @x);
            """;
        command.Parameters.AddWithValue("@t", change.ChangedAt);
        command.Parameters.AddWithValue("@v", change.TemplateVersionId);
        command.Parameters.AddWithValue("@et", change.EntityType);
        command.Parameters.AddWithValue("@ei", change.EntityId);
        command.Parameters.AddWithValue("@cc", (byte)change.ChangeClass);
        command.Parameters.AddWithValue("@op", change.Operation);
        AddNullable(command, "@oj", change.OldJson);
        AddNullable(command, "@nj", change.NewJson);
        AddNullable(command, "@cr", change.ChangeReason);
        command.Parameters.AddWithValue("@u", change.ChangedByUserId);
        AddNullable(command, "@x", change.CorrelationId);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteSecurityEventAsync(SecurityEventRecord evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);

        await using var command = CreateCommand();
        command.CommandText = """
            INSERT INTO aud.SecurityEvent
                (ChangedAt, EventType, TargetUserId, TargetRoleId, DetailsJson, ChangedByUserId, CorrelationId)
            VALUES (@t, @e, @tu, @tr, @d, @u, @x);
            """;
        command.Parameters.AddWithValue("@t", evt.ChangedAt);
        command.Parameters.AddWithValue("@e", evt.EventType);
        AddNullable(command, "@tu", evt.TargetUserId);
        AddNullable(command, "@tr", evt.TargetRoleId);
        AddNullable(command, "@d", evt.DetailsJson);
        command.Parameters.AddWithValue("@u", evt.ChangedByUserId);
        AddNullable(command, "@x", evt.CorrelationId);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WritePublicationEventAsync(PublicationEventRecord evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);

        await using var command = CreateCommand();
        command.CommandText = """
            INSERT INTO aud.PublicationEvent
                (ChangedAt, EntityType, EntityId, ResultDiffJson, ChangeReason, ChangedByUserId)
            VALUES (@t, @et, @ei, @rd, @cr, @u);
            """;
        command.Parameters.AddWithValue("@t", evt.ChangedAt);
        command.Parameters.AddWithValue("@et", evt.EntityType);
        command.Parameters.AddWithValue("@ei", evt.EntityId);
        AddNullable(command, "@rd", evt.ResultDiffJson);
        command.Parameters.AddWithValue("@cr", evt.ChangeReason);
        command.Parameters.AddWithValue("@u", evt.ChangedByUserId);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Команда на з'єднанні контексту, у поточній транзакції.</summary>
    private SqlCommand CreateCommand()
    {
        var connection = (SqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        var command = connection.CreateCommand();
        if (db.Database.CurrentTransaction is { } tx)
        {
            command.Transaction = (SqlTransaction)tx.GetDbTransaction();
        }

        return command;
    }

    private static void AddNullable(SqlCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
