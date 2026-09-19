using System.Data;
using System.Globalization;
using System.Text;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Читання <c>aud.CellChange</c> прямим ADO.</summary>
/// <remarks>
/// ⚠ Таблиці <c>aud.*</c> живуть поза моделлю EF: журнал append-only, і
/// обліковий запис застосунку має на ньому лише <c>INSERT</c> і <c>SELECT</c>.
/// Тримати їх у <c>DbSet</c> означало б дати трекеру змін можливість, якої в
/// нього немає в базі, — і виявилося б це на першому <c>SaveChanges</c>.
/// </remarks>
public sealed class AuditReader(EcrDbContext db) : IAuditReader
{
    /// <summary>Довжина <c>aud.CellChange.RowKey</c> зі схеми (<c>11-audit-tables.sql</c>).</summary>
    private const int RowKeySize = 100;

    /// <summary>Довжина <c>aud.CellChange.Origin</c> зі схеми (<c>11-audit-tables.sql</c>).</summary>
    private const int OriginSize = 32;

    /// <inheritdoc />
    public async Task<PagedResult<CellChangeView>> ReadCellChangesAsync(
        CellChangeFilter filter, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);

        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        // ⚠ Вікно за ChangedAt стоїть ПЕРШИМ у WHERE не заради стилю: саме воно
        // відсікає партиції. Курсор за Id додається до нього, а не замість —
        // інакше сторінка 20 читала б журнал цілком.
        var where = new StringBuilder("ChangedAt >= @from AND ChangedAt < @to\n                   AND Id > @after");

        // ⛔ Умова додається ЛИШЕ за наявності значення, і кожна — іменованим
        // параметром. Правило «жодного значення в текст запиту» винятків не має
        // (див. коментар у ReadStructureChangesAsync нижче).
        //
        // ⚠ Параметри рядків ТИПІЗОВАНІ разом із довжиною, а не через
        // AddWithValue: нетипізований рядок їде як nvarchar(4000) і дає окремий
        // план на кожну довжину значення — той самий урок, що `WR-01` у №14.
        // Довжини взято зі схеми (`11-audit-tables.sql`: RowKey nvarchar(100),
        // Origin nvarchar(32)), а не з пам'яті.
        void And(string clause, string name, object value, SqlDbType type, int size = 0)
        {
            where.Append("\n                   AND ").Append(clause);

            var parameter = command.Parameters.Add(name, type);
            if (size > 0)
            {
                parameter.Size = size;
            }

            parameter.Value = value;
        }

        if (filter.DocumentId is { } documentId)
        {
            And("DocumentId = @documentId", "@documentId", documentId, SqlDbType.BigInt);
        }

        if (filter.RowKey is { } rowKey)
        {
            And("RowKey = @rowKey", "@rowKey", rowKey, SqlDbType.NVarChar, RowKeySize);
        }

        if (filter.ColumnDefId is { } columnDefId)
        {
            And("ColumnDefId = @columnDefId", "@columnDefId", columnDefId, SqlDbType.Int);
        }

        if (filter.ChangedByUserId is { } changedBy)
        {
            And("ChangedByUserId = @changedBy", "@changedBy", changedBy, SqlDbType.Int);
        }

        if (filter.Origin is { } origin)
        {
            And("Origin = @origin", "@origin", origin, SqlDbType.NVarChar, OriginSize);
        }

        if (filter.LateOnly)
        {
            // ⚠ Константа, а не параметр: значення приходить не ззовні, а з
            // форми самого фільтра — параметризувати літерал `1` нічого не
            // захищає і лише ховає умову від читача плану.
            where.Append("\n                   AND IsLateEdit = 1");
        }

        command.CommandText = $"""
            SELECT TOP (@take)
                   Id, ChangedAt, PeriodKey, DocumentId, RowKey, ColumnDefId,
                   OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit
              FROM aud.CellChange
             WHERE {where}
             ORDER BY Id;
            """;

        command.Parameters.AddWithValue("@take", page.Limit + 1);
        command.Parameters.AddWithValue("@from", filter.From);
        command.Parameters.AddWithValue("@to", filter.To);
        command.Parameters.AddWithValue("@after", Cursor.Decode(page.Cursor));

        var rows = new List<(long Id, CellChangeView View)>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add((
                    reader.GetInt64(0),
                    new CellChangeView(
                        DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
                        reader.GetInt32(2),
                        reader.GetInt64(3),
                        reader.GetString(4),
                        reader.GetInt32(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7),

                        // Автор — UserId, не SID: у локального користувача SID
                        // не існує взагалі (R-A2, D-86).
                        reader.GetInt32(8),
                        reader.GetString(9),
                        reader.GetBoolean(10))));
            }
        }

        var hasMore = rows.Count > page.Limit;
        var items = rows.Take(page.Limit).Select(r => r.View).ToList();

        return new PagedResult<CellChangeView>(
            items,
            hasMore ? Cursor.Encode(rows[page.Limit - 1].Id) : null,

            // Підрахунок по вікну аудиту дорогий і нікому не потрібен: аудитор
            // гортає, а не рахує (конвенція API — TotalCount може бути null).
            TotalCount: null);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StructureChangeView>> ReadStructureChangesAsync(
        IReadOnlyList<string> entityTypes, int entityId, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entityTypes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        if (entityTypes.Count == 0)
        {
            return [];
        }

        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        // ⛔ Типи підставляються ІМЕНОВАНИМИ параметрами, а не склеюванням
        // рядків: перелік приходить із коду, але правило «жодного значення в
        // текст запиту» не має винятків — виняток «тут же наше» і є тим, як
        // склеювання потрапляє в місце, де значення вже чуже.
        var names = entityTypes.Select((_, i) => $"@t{i.ToString(CultureInfo.InvariantCulture)}").ToList();
        for (var i = 0; i < entityTypes.Count; i++)
        {
            command.Parameters.AddWithValue(names[i], entityTypes[i]);
        }

        command.CommandText = $"""
            SELECT TOP (@take)
                   ChangedAt, EntityType, EntityId, Operation,
                   OldJson, NewJson, ChangeReason, ChangedByUserId
              FROM aud.StructureChange
             WHERE EntityType IN ({string.Join(", ", names)})
                   AND EntityId = @entityId
             ORDER BY ChangedAt DESC, Id DESC;
            """;

        command.Parameters.AddWithValue("@take", limit);
        command.Parameters.AddWithValue("@entityId", entityId);

        var rows = new List<StructureChangeView>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new StructureChangeView(
                DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt32(7)));
        }

        return rows;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<(long TableRowId, int ColumnDefId), LastCellChange>> ReadLastChangesAsync(
        long documentId,
        IReadOnlyCollection<(long TableRowId, int ColumnDefId)> cells,
        DateTime since,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cells);

        var result = new Dictionary<(long TableRowId, int ColumnDefId), LastCellChange>();

        // ⚠ Дублікати прибираються ТУТ, а не покладаються на словник результату:
        // повторена адреса в `VALUES` — це зайва засічка в `CROSS APPLY`, тобто
        // робота бази, а не лише зайвий рядок у відповіді.
        var distinct = cells.Distinct().ToList();
        if (distinct.Count == 0)
        {
            return result;
        }

        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        // ⛔ Адреси їдуть ІМЕНОВАНИМИ ТИПІЗОВАНИМИ параметрами, а не склеюванням
        // чисел у текст: правило «жодного значення в текст запиту» винятків не
        // має (той самий коментар, що в `ReadCellChangesAsync` вище), і
        // нетипізований параметр дав би окремий план на кожну довжину батчу
        // понад те, що вже дає сама кількість рядків `VALUES` (`WR-01`).
        for (var i = 0; i < distinct.Count; i++)
        {
            var index = i.ToString(CultureInfo.InvariantCulture);
            command.Parameters.Add($"@r{index}", SqlDbType.BigInt).Value = distinct[i].TableRowId;
            command.Parameters.Add($"@c{index}", SqlDbType.Int).Value = distinct[i].ColumnDefId;
        }

        command.CommandText = LastChangesSql(distinct.Count);

        command.Parameters.Add("@documentId", SqlDbType.BigInt).Value = documentId;

        // ⚠ `datetime2(3)` — рівно ширина колонки зі схеми (`11-audit-tables.sql:44`).
        // Параметр іншої точності змусив би СУБД перетворювати КОЛОНКУ, а не
        // значення, і предикат перестав би бути SARGable — тобто вікно, заради
        // якого тут усе й написано, перестало б відсікати партиції.
        var sinceParameter = command.Parameters.Add("@since", SqlDbType.DateTime2);
        sinceParameter.Scale = 3;
        sinceParameter.Value = since;

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result[(reader.GetInt64(0), reader.GetInt32(1))] = new LastCellChange(
                DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),

                // Автор — UserId, не SID: у локального користувача SID не існує
                // взагалі (R-A2, D-86).
                reader.GetInt32(3),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(4));
        }

        return result;
    }

    /// <summary>
    /// Текст запиту «остання зміна кожної з <paramref name="cellCount"/> комірок».
    /// </summary>
    /// <param name="cellCount">Скільки адрес їде в <c>VALUES</c>; параметри — <c>@rN</c>/<c>@cN</c>.</param>
    /// <remarks>
    /// ⛔ Винесено окремою фабрикою НЕ заради читабельності, а щоб замір
    /// відсікання партицій міряв БОЙОВИЙ текст, а не його копію в тесті
    /// (<c>AuditLastChangePartitionScanTests</c>). Копія розійшлася б із
    /// оригіналом мовчки — і замір лишився б зеленим, доводячи властивість
    /// запиту, якого більше немає. Той самий прийом, що <c>RowStore.RowsQuery</c>
    /// для сторожа <c>WR-05</c>.
    ///
    /// ⛔ `CROSS APPLY` з `TOP (1)` на КОЖНУ адресу, а не один `GROUP BY` по
    /// всьому документу: <c>IX_CellChange_Cell (DocumentId, TableRowId,
    /// ColumnDefId, ChangedAt DESC)</c> дає останню зміну комірки ПЕРШИМ же
    /// рядком засічки — без сортування й без читання її історії цілком.
    ///
    /// ⛔ <c>a.ChangedAt &gt;= @since</c> стоїть усередині <c>APPLY</c>, і саме
    /// воно відсікає партиції. Індекс вирівняний по <c>ps_AuditByMonth</c>, тому
    /// без цього предиката засічка йде по КОЖНІЙ партиції — адреса комірки
    /// партицію не звужує.
    ///
    /// ⚠ <c>LEFT JOIN sec.[User]</c>, а не <c>INNER</c>: видалений (або ще не
    /// заведений) автор не має права ховати сам факт зміни — журнал append-only,
    /// а таблиця користувачів живе своїм життям. Ім'я тоді приїде <c>null</c>, і
    /// назвати його порожнім рядком означало б сказати «змінив ніхто».
    ///
    /// ⚠ <c>u.DisplayName</c>, не <c>u.UserName</c>: у діалозі конфлікту стоїть
    /// ІМ'Я людини, а логін і SID показувати заборонено (R-A2, D-86).
    /// </remarks>
    public static string LastChangesSql(int cellCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellCount);

        var tuples = Enumerable
            .Range(0, cellCount)
            .Select(i => $"(@r{i.ToString(CultureInfo.InvariantCulture)}, @c{i.ToString(CultureInfo.InvariantCulture)})");

        return $"""
            SELECT c.TableRowId, c.ColumnDefId, l.ChangedAt, l.ChangedByUserId, l.Origin, u.DisplayName
              FROM (VALUES {string.Join(", ", tuples)}) AS c(TableRowId, ColumnDefId)
             CROSS APPLY (
                   SELECT TOP (1) a.ChangedAt, a.ChangedByUserId, a.Origin
                     FROM aud.CellChange AS a
                    WHERE a.DocumentId = @documentId
                      AND a.TableRowId = c.TableRowId
                      AND a.ColumnDefId = c.ColumnDefId
                      AND a.ChangedAt >= @since
                    ORDER BY a.ChangedAt DESC
                   ) AS l
              LEFT JOIN sec.[User] AS u ON u.Id = l.ChangedByUserId;
            """;
    }

    /// <summary>Формат дати для повідомлень; не для запитів.</summary>
    internal static string Format(DateTime moment)
        => moment.ToString("O", CultureInfo.InvariantCulture);
}
