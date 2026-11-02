using System.Data;
using System.Globalization;
using System.Text;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Журнал рядків <c>aud.RowChange</c> (<c>11-audit-tables.sql</c>) прямим ADO.</summary>
/// <remarks>
/// ⚠ Як і решта <c>aud.*</c>, таблиця поза моделлю EF (append-only, тригер незмінності
/// <c>TR_RowChange_Immutable</c>). Запис — на з'єднанні контексту в ПОТОЧНІЙ транзакції (як <see cref="AuditWriter"/>);
/// читання — окремим з'єднанням (як <see cref="AuditReader"/>).
/// </remarks>
public sealed class RowChangeAudit(EcrDbContext db) : IRowChangeAudit
{
    /// <summary><c>aud.RowChange.RowKey</c> — <c>nvarchar(100)</c>.</summary>
    private const int RowKeyLength = 100;

    /// <summary><c>aud.RowChange.Origin</c> — <c>nvarchar(32)</c>.</summary>
    private const int OriginLength = 32;

    /// <summary><c>aud.RowChange.CorrelationId</c> — <c>nvarchar(64)</c>.</summary>
    private const int CorrelationIdLength = 64;

    /// <summary>Рядків за один <c>INSERT</c>: 12 параметрів на рядок, ліміт SQL Server — 2100.</summary>
    private const int MaxRowsPerBatch = 150;

    /// <inheritdoc />
    public async Task WriteRowChangesAsync(IReadOnlyList<RowChangeRecord> changes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);

        // ⛔ Межі довжин — ДО відправки: параметр із заданим Size обрізав би значення на клієнті, і журнал
        // показував би на рядок, якого немає (той самий урок, що `AuditWriter.EnsureFits`).
        foreach (var change in changes)
        {
            ArgumentNullException.ThrowIfNull(change);
            EnsureFits(nameof(RowChangeRecord.RowKey), change.RowKey, RowKeyLength);
            EnsureFits(nameof(RowChangeRecord.Origin), change.Origin, OriginLength);
            EnsureFits(nameof(RowChangeRecord.CorrelationId), change.CorrelationId, CorrelationIdLength);
        }

        for (var offset = 0; offset < changes.Count; offset += MaxRowsPerBatch)
        {
            var count = Math.Min(MaxRowsPerBatch, changes.Count - offset);
            await using var command = CreateCommand();
            var values = new string[count];
            for (var i = 0; i < count; i++)
            {
                var c = changes[offset + i];
                var at = command.Parameters.Add($"@t{i}", SqlDbType.DateTime2);
                at.Scale = 3;
                at.Value = c.ChangedAt;
                command.Parameters.Add($"@d{i}", SqlDbType.BigInt).Value = c.DocumentId;
                command.Parameters.Add($"@p{i}", SqlDbType.Int).Value = c.PeriodKey;
                command.Parameters.Add($"@ti{i}", SqlDbType.BigInt).Value = c.TableInstanceId;
                command.Parameters.Add($"@td{i}", SqlDbType.Int).Value = c.TableDefId;
                command.Parameters.Add($"@r{i}", SqlDbType.BigInt).Value = c.TableRowId;
                command.Parameters.Add($"@k{i}", SqlDbType.NVarChar, RowKeyLength).Value = c.RowKey;
                command.Parameters.Add($"@a{i}", SqlDbType.TinyInt).Value = (byte)c.Action;
                command.Parameters.Add($"@u{i}", SqlDbType.Int).Value = c.ChangedByUserId;
                command.Parameters.Add($"@o{i}", SqlDbType.NVarChar, OriginLength).Value = c.Origin;
                command.Parameters.Add($"@x{i}", SqlDbType.NVarChar, CorrelationIdLength).Value =
                    (object?)c.CorrelationId ?? DBNull.Value;
                values[i] = $"(@t{i}, @d{i}, @p{i}, @ti{i}, @td{i}, @r{i}, @k{i}, @a{i}, @u{i}, @o{i}, @x{i})";
            }

            command.CommandText = $"""
                INSERT INTO aud.RowChange
                    (ChangedAt, DocumentId, PeriodKey, TableInstanceId, TableDefId, TableRowId, RowKey, Action,
                     ChangedByUserId, Origin, CorrelationId)
                VALUES {string.Join(",\n", values)};
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Межі читання (<see cref="RowChangeFilter.VisibleTableIds"/>), автор і «лише людина» — у WHERE ДО <c>TOP</c>:
    /// сторінка й курсор не знають про рядки прихованих таблиць. Вікно за <c>ChangedAt</c> — першим (партиції);
    /// порядок — зворотний обхід <c>IX_RowChange_Document</c> <c>(DocumentId, PeriodKey, ChangedAt DESC, Id DESC)</c>.
    /// Імена — після вибору сторінки (LEFT JOIN: видалений автор чи таблиця не ховають факту).
    /// </remarks>
    public async Task<IReadOnlyList<RowChangeView>> ReadRowChangesAsync(
        RowChangeFilter filter, int take, (DateTime At, long Id)? after, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);

        if (filter.VisibleTableIds.Count == 0)
        {
            return [];
        }

        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var where = new StringBuilder("ChangedAt >= @from AND ChangedAt < @to AND DocumentId = @documentId AND PeriodKey = @periodKey");
        AddTimestamp(command, "@from", filter.From);
        AddTimestamp(command, "@to", filter.To);
        command.Parameters.Add("@documentId", SqlDbType.BigInt).Value = filter.DocumentId;
        command.Parameters.Add("@periodKey", SqlDbType.Int).Value = filter.PeriodKey;

        where.Append("\n   AND TableDefId IN (SELECT CAST([value] AS int) FROM OPENJSON(@visibleTables))");
        command.Parameters.Add("@visibleTables", SqlDbType.NVarChar, -1).Value =
            "[" + string.Join(",", filter.VisibleTableIds.Select(v => v.ToString(CultureInfo.InvariantCulture))) + "]";

        if (filter.ChangedByUserId is { } by)
        {
            where.Append("\n   AND ChangedByUserId = @changedBy");
            command.Parameters.Add("@changedBy", SqlDbType.Int).Value = by;
        }

        if (filter.HumanOnly)
        {
            where.Append("\n   AND Origin IN (" + CellChangeOrigins.HumanOriginsSql + ")");
        }

        if (after is { } a)
        {
            where.Append("\n   AND (ChangedAt < @afterAt OR (ChangedAt = @afterAt AND Id < @afterId))");
            AddTimestamp(command, "@afterAt", a.At);
            command.Parameters.Add("@afterId", SqlDbType.BigInt).Value = a.Id;
        }

        command.Parameters.Add("@take", SqlDbType.Int).Value = take;
        command.CommandText = $"""
            SELECT r.Id, r.ChangedAt, r.PeriodKey, r.RowKey, r.Action, r.ChangedByUserId, u.DisplayName, r.Origin,
                   td.Code, td.NameL10n
              FROM (SELECT TOP (@take) Id, ChangedAt, PeriodKey, RowKey, Action, ChangedByUserId, Origin, TableDefId
                      FROM aud.RowChange
                     WHERE {where}
                     ORDER BY ChangedAt DESC, Id DESC) AS r
              LEFT JOIN sec.[User] AS u ON u.Id = r.ChangedByUserId
              LEFT JOIN cfg.TableDef AS td ON td.Id = r.TableDefId
             ORDER BY r.ChangedAt DESC, r.Id DESC;
            """;

        var rows = new List<RowChangeView>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new RowChangeView(
                reader.GetInt64(0),
                DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
                reader.GetInt32(2),
                reader.GetString(3),
                (RowChangeAction)reader.GetByte(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                LocalizedOrNull(reader, 9)));
        }

        return rows;
    }

    private static Ecr.Domain.ValueObjects.LocalizedText? LocalizedOrNull(SqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var text = Ecr.Domain.ValueObjects.LocalizedText.FromJson(reader.GetString(ordinal));
        return text.Values.Count == 0 ? null : text;
    }

    private static void AddTimestamp(SqlCommand command, string name, DateTime value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.DateTime2);
        parameter.Scale = 3;
        parameter.Value = value;
    }

    private static void EnsureFits(string name, string? value, int max)
    {
        if (value is not null && value.Length > max)
        {
            throw new ArgumentException(
                $"Значення {name} довше за стовпець: {value.Length} символів проти {max}.", nameof(value));
        }
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
}
