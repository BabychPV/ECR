// src/Ecr.Infrastructure/Persistence/SheetChangeStore.cs
using System.Data;
using System.Text.Json;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="ISheetChangeStore"/> сирим SQL (опитування кожні 30 с з кожної відкритої вкладки).</summary>
/// <remarks>
/// ⚠ Лише читання, без нових індексів. Проба: пошук <c>UQ_TableInstance (PeriodKey, DocumentId, TableDefId)</c> →
/// <c>UQ_TableRow_Key (PeriodKey, TableInstanceId, …)</c> → key lookup <c>PK_TableRow</c> за <c>RowVersion</c>. Скану
/// немає; ціна — лінійна від числа рядків аркуша за період (найбільший аркуш Land: 15 таблиць / 810 рядків ≈ 2,5 тис.
/// логічних читань, ≈ 2–3 мс тепло). Індекс <c>(PeriodKey, TableInstanceId, RowVersion)</c> зробив би її
/// index-only (~45 читань) — пропозиція, не застосована (токен міграцій).
/// ⚠ З'єднання — те саме, що в <see cref="EcrDbContext"/> (власних вихідних з'єднань сховище не відкриває).
/// </remarks>
public sealed class SheetChangeStore(EcrDbContext db) : ISheetChangeStore
{
    /// <summary>Межа: <c>MIN_ACTIVE_ROWVERSION()</c> — найменша версія, яка ще може з'явитися (усе нижче зафіксовано).</summary>
    internal const string CurrentMarkerSql = "SELECT CAST(MIN_ACTIVE_ROWVERSION() AS binary(8));";

    /// <summary>
    /// Проба: ДВА оператори — спершу межа, потім знімок рядків (RCSI бере знімок на старті КОЖНОГО оператора, тож
    /// транзакція, зафіксована між ними, видна; не зафіксована — має версію ≥ межі й потрапить у наступне опитування).
    /// ⚠ <c>FORCESEEK</c> саме по <c>UQ_TableRow_Key (PeriodKey, TableInstanceId)</c>: інакше оптимізатор обирає пошук
    /// лише за <c>PeriodKey</c> у кластерному ключі, тобто скан усієї партиції періоду (рядки ВСІХ документів) — ціна
    /// росла б від чужих даних, а не від свого аркуша (виміряно: шум ×4 → читань ×4).
    /// </summary>
    internal const string ProbeSql = """
        SET NOCOUNT ON;
        DECLARE @upto binary(8) = MIN_ACTIVE_ROWVERSION();
        SELECT @upto;
        SELECT x.TableInstanceId, x.TableDefId, x.Id, x.Cnt
          FROM (SELECT r.TableInstanceId, i.TableDefId, r.Id,
                       ROW_NUMBER() OVER (PARTITION BY r.TableInstanceId ORDER BY r.RowVersion DESC) AS Rn,
                       COUNT(*) OVER (PARTITION BY r.TableInstanceId) AS Cnt
                  FROM doc.TableInstance i
                  JOIN doc.TableRow r WITH (FORCESEEK (UQ_TableRow_Key (PeriodKey, TableInstanceId)))
                    ON r.PeriodKey = i.PeriodKey AND r.TableInstanceId = i.Id
                 WHERE i.PeriodKey = @period AND i.DocumentId = @doc
                   AND i.TableDefId IN (SELECT t.value FROM OPENJSON(@tables) WITH (value int '$') t)
                   AND r.RowVersion >= @since AND r.RowVersion < @upto) x
         WHERE x.Rn <= @sample;
        """;

    /// <summary>
    /// Авторство: ОСТАННЯ не-перерахункова зміна кожного зміненого рядка у вікні журналу (саме вона й підняла версію
    /// рядка), плюс ознака «видима колонка перерахована». Рядок без жодного запису — відсутній у відповіді.
    /// </summary>
    /// <remarks>
    /// ⚠ Саме ОСТАННЯ, а не «будь-яка чужа у вікні»: інакше власна правка рядка, який хтось правив за хвилину ДО
    /// маркера (вже показане), будила б банер — вікно має запас у 15 хв назад на довгі транзакції. Серед змін одного
    /// моменту (один PATCH) перевага — видимій колонці: схована зміна мовчить, видима тієї ж правки — ні.
    /// </remarks>
    internal const string AttributeSql = """
        SET NOCOUNT ON;
        WITH c AS (
            SELECT c.TableRowId, c.ChangedByUserId, c.ChangedAt, c.Id,
                   CASE WHEN c.Origin = @recalc THEN 1 ELSE 0 END AS IsRecalc,
                   CASE WHEN h.value IS NULL THEN 0 ELSE 1 END AS IsHidden
              FROM aud.CellChange c
              LEFT JOIN OPENJSON(@hidden) WITH (value int '$') h ON h.value = c.ColumnDefId
             WHERE c.DocumentId = @doc AND c.ChangedAt >= @from
               AND c.TableRowId IN (SELECT r.value FROM OPENJSON(@rows) WITH (value bigint '$') r)),
        ranked AS (
            SELECT c.*,
                   ROW_NUMBER() OVER (PARTITION BY c.TableRowId ORDER BY c.IsRecalc, c.ChangedAt DESC, c.IsHidden, c.Id DESC) AS Rn,
                   MAX(CASE WHEN c.IsRecalc = 1 AND c.IsHidden = 0 THEN 1 ELSE 0 END) OVER (PARTITION BY c.TableRowId) AS AnyRecalc
              FROM c)
        SELECT r.TableRowId, r.ChangedByUserId, r.ChangedAt, r.IsRecalc, r.IsHidden, r.AnyRecalc, u.DisplayName
          FROM ranked r
          LEFT JOIN sec.[User] u ON u.Id = r.ChangedByUserId
         WHERE r.Rn = 1;
        """;

    /// <inheritdoc />
    public Task<ulong> CurrentMarkerAsync(CancellationToken ct)
        => RunAsync(
            CurrentMarkerSql,
            static _ => { },
            static async (reader, token) =>
            {
                await reader.ReadAsync(token).ConfigureAwait(false);
                return ToVersion((byte[])reader.GetValue(0));
            },
            ct);

    /// <inheritdoc />
    public Task<SheetChangeProbe> ProbeAsync(
        long documentId, int periodKey, IReadOnlyCollection<int> tableDefIds, ulong since, int cap, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tableDefIds);

        return RunAsync(
            ProbeSql,
            parameters =>
            {
                parameters.Add("@period", SqlDbType.Int).Value = periodKey;
                parameters.Add("@doc", SqlDbType.BigInt).Value = documentId;
                parameters.Add("@tables", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(tableDefIds);
                parameters.Add("@since", SqlDbType.Binary, 8).Value = FromVersion(since);
                parameters.Add("@sample", SqlDbType.Int).Value = cap;
            },
            async (reader, token) =>
            {
                await reader.ReadAsync(token).ConfigureAwait(false);
                var marker = ToVersion((byte[])reader.GetValue(0));

                await reader.NextResultAsync(token).ConfigureAwait(false);
                var rows = new List<SheetChangedRow>();
                var hasMore = false;
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    rows.Add(new SheetChangedRow(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt64(2)));
                    hasMore |= reader.GetInt32(3) > cap;
                }

                return new SheetChangeProbe(marker, rows, hasMore);
            },
            ct);
    }

    /// <inheritdoc />
    public Task<SheetChangeAttribution> AttributeAsync(
        long documentId, IReadOnlyCollection<long> rowIds, IReadOnlyCollection<int> hiddenColumnIds, DateTime fromUtc,
        int viewerUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rowIds);
        ArgumentNullException.ThrowIfNull(hiddenColumnIds);

        return RunAsync(
            AttributeSql,
            parameters =>
            {
                parameters.Add("@doc", SqlDbType.BigInt).Value = documentId;
                parameters.Add("@from", SqlDbType.DateTime2).Value = fromUtc;
                parameters.Add("@rows", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(rowIds);
                parameters.Add("@hidden", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(hiddenColumnIds);
                parameters.Add("@me", SqlDbType.Int).Value = viewerUserId;
                parameters.Add("@recalc", SqlDbType.NVarChar, 32).Value = CellChangeOrigins.Recalculation;
            },
            async (reader, token) =>
            {
                var foreign = new HashSet<long>();
                var recalculated = new HashSet<long>();
                var attributed = new HashSet<long>();
                int? userId = null;
                string? name = null;
                DateTime? at = null;

                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var rowId = reader.GetInt64(0);
                    attributed.Add(rowId);

                    if (reader.GetInt32(5) == 1)
                    {
                        recalculated.Add(rowId);
                    }

                    // Остання не-перерахункова зміна рядка: чужа — лише видима й не від читача.
                    var author = reader.GetInt32(1);
                    if (reader.GetInt32(3) == 1 || reader.GetInt32(4) == 1 || author == viewerUserId)
                    {
                        continue;
                    }

                    foreign.Add(rowId);
                    var changedAt = DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc);
                    if (at is null || changedAt > at)
                    {
                        (userId, name, at) = (author, reader.IsDBNull(6) ? null : reader.GetString(6), changedAt);
                    }
                }

                return new SheetChangeAttribution(foreign, recalculated, attributed, userId, name, at);
            },
            ct);
    }

    /// <summary><c>binary(8)</c> rowversion → число (big-endian).</summary>
    internal static ulong ToVersion(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(value);
    }

    /// <summary>Число → <c>binary(8)</c> rowversion (big-endian).</summary>
    internal static byte[] FromVersion(ulong value)
    {
        var bytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    /// <summary>Одна команда на з'єднанні контексту: відкрити (якщо закрите), виконати, повернути як було.</summary>
    private async Task<T> RunAsync<T>(
        string sql, Action<SqlParameterCollection> bind, Func<SqlDataReader, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction() as SqlTransaction;
            bind(command.Parameters);

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await read(reader, ct).ConfigureAwait(false);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
