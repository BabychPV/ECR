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

    /// <summary>Масштаб усіх стовпців <c>ChangedAt</c> — <c>datetime2(3)</c>.</summary>
    /// <remarks>
    /// ⛔ <c>WR-01</c>. <c>AddWithValue</c> із <c>DateTime</c> дає
    /// <c>SqlDbType.DateTime</c> — СТАРИЙ тип із роздільністю 1/300 секунди,
    /// тоді як стовпці <c>aud.*</c> оголошені <c>datetime2(3)</c>
    /// (<c>11-audit-tables.sql:44</c>). Тобто момент зміни їхав на сервер
    /// округленим до ~3.3 мс і лише там переводився в <c>datetime2</c>. Для
    /// таблиці, розділеної ПО ЦЬОМУ СТОВПЦЮ, це ще й зайва неявна конверсія в
    /// кожному запиті.
    /// </remarks>
    private const byte TimestampScale = 3;

    /// <summary>Довжини рядкових стовпців <c>aud.*</c> (<c>11-audit-tables.sql</c>).</summary>
    /// <remarks>
    /// ⛔ <c>WR-01</c>, головна причина рядка плану на стороні аудиту.
    /// <c>AddWithValue</c> для <c>string</c> оголошує параметр завдовжки як
    /// САМЕ ЗНАЧЕННЯ: <c>@k0 nvarchar(4)</c> для <c>"R123"</c> і
    /// <c>@k0 nvarchar(5)</c> для <c>"R1234"</c>. На батчі зі 150 рядків це
    /// добуток довжин трьох стовпців — тобто практично унікальна сигнатура на
    /// кожен <c>PATCH</c>, і окремий план на кожну.
    ///
    /// ⚠ Числа — зі скрипта, який створює таблиці, а не з голови: <c>aud.*</c>
    /// не мають доменних сутностей і міграція EF їх не створює
    /// (<c>Q-049</c>), тож «подивитися в конфігурацію EF» тут неможливо в
    /// принципі.
    /// </remarks>
    private const int RowKeyLength = 100;

    /// <summary><c>aud.CellChange.Origin</c> — <c>nvarchar(32)</c>.</summary>
    private const int OriginLength = 32;

    /// <summary><c>CorrelationId</c> в усіх таблицях <c>aud.*</c> — <c>nvarchar(64)</c>.</summary>
    private const int CorrelationIdLength = 64;

    /// <summary><c>EntityType</c>/<c>EventType</c> — <c>nvarchar(64)</c>.</summary>
    private const int EntityTypeLength = 64;

    /// <summary><c>Operation</c> — <c>nvarchar(32)</c>.</summary>
    private const int OperationLength = 32;

    /// <summary>
    /// Рядкові значення без власної стелі — <c>nvarchar(max)</c> у параметрі.
    /// </summary>
    /// <remarks>
    /// ⛔ Сюди йдуть <c>OldValue</c>/<c>NewValue</c> (стовпці
    /// <c>nvarchar(1000)</c>), <c>ChangeReason</c>, <c>*Json</c>. Стелю
    /// стовпця в параметрі тут ставити НЕ МОЖНА: <c>SqlParameter</c> із
    /// заданим <c>Size</c> ріже довше значення на клієнті, і журнал аудиту
    /// тихо зберігав би огризок замість того, що насправді записали. Це
    /// дослівно <c>DAT-03</c> (<c>NormalizedCellStore</c>, коментар до
    /// <c>AddNullable(string)</c>), і ціна помилки в журналі вища: комірку
    /// можна перечитати, а неправдивий рядок аудиту нічим не спростуєш.
    ///
    /// ⚠ Для <c>WR-01</c> цього досить: <c>-1</c> — сигнатура СТАЛА
    /// (<c>nvarchar(max)</c>) на будь-якому значенні, а саме сталості план і
    /// потребує.
    /// </remarks>
    private const int UnboundedLength = -1;

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

                AddTimestamp(command, $"@t{i}", c.ChangedAt);
                AddInt32(command, $"@p{i}", c.Address.PeriodKey.Value);
                AddInt64(command, $"@d{i}", c.DocumentId);
                AddInt64(command, $"@r{i}", c.Address.TableRowId);
                AddText(command, $"@k{i}", c.RowKey, RowKeyLength);
                AddInt32(command, $"@c{i}", c.Address.ColumnDefId);
                AddText(command, $"@o{i}", c.OldValue, UnboundedLength);
                AddText(command, $"@n{i}", c.NewValue, UnboundedLength);
                AddInt32(command, $"@u{i}", c.ChangedByUserId);
                AddText(command, $"@g{i}", c.Origin, OriginLength);
                AddBit(command, $"@l{i}", c.IsLateEdit);
                AddText(command, $"@x{i}", c.CorrelationId, CorrelationIdLength);
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
        AddTimestamp(command, "@t", change.ChangedAt);
        AddInt32(command, "@v", change.TemplateVersionId);
        AddText(command, "@et", change.EntityType, EntityTypeLength);
        AddInt32(command, "@ei", change.EntityId);
        command.Parameters.Add("@cc", SqlDbType.TinyInt).Value = (byte)change.ChangeClass;
        AddText(command, "@op", change.Operation, OperationLength);
        AddText(command, "@oj", change.OldJson, UnboundedLength);
        AddText(command, "@nj", change.NewJson, UnboundedLength);
        AddText(command, "@cr", change.ChangeReason, UnboundedLength);
        AddInt32(command, "@u", change.ChangedByUserId);
        AddText(command, "@x", change.CorrelationId, CorrelationIdLength);

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
        AddTimestamp(command, "@t", evt.ChangedAt);
        AddText(command, "@e", evt.EventType, EntityTypeLength);
        AddNullableInt32(command, "@tu", evt.TargetUserId);
        AddNullableInt32(command, "@tr", evt.TargetRoleId);
        AddText(command, "@d", evt.DetailsJson, UnboundedLength);
        AddInt32(command, "@u", evt.ChangedByUserId);
        AddText(command, "@x", evt.CorrelationId, CorrelationIdLength);

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
        AddTimestamp(command, "@t", evt.ChangedAt);
        AddText(command, "@et", evt.EntityType, EntityTypeLength);
        AddInt32(command, "@ei", evt.EntityId);
        AddText(command, "@rd", evt.ResultDiffJson, UnboundedLength);
        AddText(command, "@cr", evt.ChangeReason, UnboundedLength);
        AddInt32(command, "@u", evt.ChangedByUserId);

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

    /// <summary>Рядковий параметр сталої сигнатури.</summary>
    /// <param name="command">Команда.</param>
    /// <param name="name">Ім'я параметра.</param>
    /// <param name="value">Значення; <c>null</c> → <c>DBNull</c>.</param>
    /// <param name="size">
    /// Довжина стовпця або <see cref="UnboundedLength"/> для значень без стелі.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Значення довше за оголошений стовпець.
    /// </exception>
    /// <remarks>
    /// ⛔ Перевірка довжини тут ОБОВ'ЯЗКОВА, і без неї заміна
    /// <c>AddWithValue</c> на типізований параметр була б не оптимізацією, а
    /// новим дефектом: <c>SqlParameter</c> із заданим <c>Size</c> ріже довше
    /// значення НА КЛІЄНТІ, тобто <c>RowKey</c> на 101 символ ліг би в журнал
    /// огризком на 100 — і рядок аудиту показував би на комірку, якої немає.
    /// <c>AddWithValue</c> цього не робив (довжина параметра дорівнювала
    /// довжині значення, і задовге падало на сервері помилкою 2628), тож без
    /// цієї перевірки зміна ПОГІРШИЛА б поведінку. Той самий урок, що
    /// <c>DAT-03</c>, і саме тому він тут повторений, а не мається на увазі.
    ///
    /// ⚠ Сьогодні жоден викликач до межі не доходить (<c>RowKey</c> береться з
    /// <c>doc.TableRow.RowKey</c>, теж <c>nvarchar(100)</c>; <c>Origin</c> —
    /// із замкненого переліку). Перевірка на це й не розрахована: вона
    /// стереже ЗАВТРАШНЬОГО викликача, який про цю відповідність не знатиме.
    /// </remarks>
    private static void AddText(SqlCommand command, string name, string? value, int size)
    {
        if (size != UnboundedLength && value is not null && value.Length > size)
        {
            throw new ArgumentException(
                $"Значення параметра {name} довше за стовпець: {value.Length} символів проти {size}.",
                nameof(value));
        }

        command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = (object?)value ?? DBNull.Value;
    }

    /// <summary><c>datetime2(3)</c> — тип і масштаб стовпців <c>ChangedAt</c>.</summary>
    /// <param name="command">Команда.</param>
    /// <param name="name">Ім'я параметра.</param>
    /// <param name="value">Момент зміни.</param>
    private static void AddTimestamp(SqlCommand command, string name, DateTime value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.DateTime2);
        parameter.Scale = TimestampScale;
        parameter.Value = value;
    }

    /// <summary><c>int</c> зі сталою сигнатурою.</summary>
    /// <param name="command">Команда.</param>
    /// <param name="name">Ім'я параметра.</param>
    /// <param name="value">Значення.</param>
    private static void AddInt32(SqlCommand command, string name, int value)
        => command.Parameters.Add(name, SqlDbType.Int).Value = value;

    /// <summary><c>int NULL</c> зі сталою сигнатурою.</summary>
    /// <param name="command">Команда.</param>
    /// <param name="name">Ім'я параметра.</param>
    /// <param name="value">Значення або <c>null</c>.</param>
    private static void AddNullableInt32(SqlCommand command, string name, int? value)
        => command.Parameters.Add(name, SqlDbType.Int).Value = value.HasValue ? value.Value : DBNull.Value;

    /// <summary><c>bigint</c> зі сталою сигнатурою.</summary>
    /// <param name="command">Команда.</param>
    /// <param name="name">Ім'я параметра.</param>
    /// <param name="value">Значення.</param>
    private static void AddInt64(SqlCommand command, string name, long value)
        => command.Parameters.Add(name, SqlDbType.BigInt).Value = value;

    /// <summary><c>bit</c> зі сталою сигнатурою.</summary>
    /// <param name="command">Команда.</param>
    /// <param name="name">Ім'я параметра.</param>
    /// <param name="value">Значення.</param>
    private static void AddBit(SqlCommand command, string name, bool value)
        => command.Parameters.Add(name, SqlDbType.Bit).Value = value;
}
