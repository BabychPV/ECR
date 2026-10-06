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

    /// <summary>Довжина <c>aud.StructureChange.EntityType</c> зі схеми (<c>11-audit-tables.sql</c>).</summary>
    private const int EntityTypeSize = 64;

    /// <inheritdoc />
    public async Task<PagedResult<CellChangeView>> ReadCellChangesAsync(
        CellChangeFilter filter, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);

        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        var where = BuildCellChangeWhere(filter, command, Cursor.Decode(page.Cursor));

        // ⚠ `R-18`: імена — ПІСЛЯ вибору сторінки, у зовнішньому запиті. Вікно й
        // курсор лишаються дослівно тими самими над самою `aud.CellChange`
        // (відсікання партицій), а три LEFT JOIN торкаються лише `@take` рядків.
        // LEFT, а не INNER: видалений автор, документ чи колонка не мають права
        // ховати сам факт зміни — журнал append-only.
        command.CommandText = $"""
            SELECT a.Id, a.ChangedAt, a.PeriodKey, a.DocumentId, a.RowKey, a.ColumnDefId,
                   a.OldValue, a.NewValue, a.ChangedByUserId, a.Origin, a.IsLateEdit,
                   u.DisplayName, d.BusinessKey, d.NameL10n, c.Code, c.HeaderL10n, c.DataType,
                   a.IsOutOfWindow, sd.Code, sd.NameL10n, td.Code, td.NameL10n, rd.LabelL10n
              FROM (
                    SELECT TOP (@take)
                           Id, ChangedAt, PeriodKey, DocumentId, RowKey, ColumnDefId,
                           OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit, IsOutOfWindow
                      FROM aud.CellChange
                     WHERE {where}
                     ORDER BY Id
                   ) AS a
              LEFT JOIN sec.[User] AS u ON u.Id = a.ChangedByUserId
              LEFT JOIN doc.Document AS d ON d.Id = a.DocumentId
              LEFT JOIN cfg.ColumnDef AS c ON c.Id = a.ColumnDefId
              LEFT JOIN cfg.TableDef AS td ON td.Id = c.TableDefId
              LEFT JOIN cfg.SheetDef AS sd ON sd.Id = td.SheetDefId
              OUTER APPLY (SELECT TOP (1) r.LabelL10n
                             FROM cfg.RowDef AS r
                            WHERE r.TableDefId = c.TableDefId AND r.RowKey = a.RowKey AND r.IsDeleted = 0) AS rd
             ORDER BY a.Id;
            """;

        command.Parameters.AddWithValue("@take", page.Limit + 1);

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
                        reader.GetBoolean(10),
                        StringOrNull(reader, 11),
                        StringOrNull(reader, 12),
                        LocalizedOrNull(reader, 13),
                        StringOrNull(reader, 14),
                        LocalizedOrNull(reader, 15),
                        reader.IsDBNull(16)
                            ? null
                            : ((Ecr.Domain.Enums.CellDataType)reader.GetByte(16)).ToString(),
                        reader.GetBoolean(17),
                        StringOrNull(reader, 18),
                        LocalizedOrNull(reader, 19),
                        StringOrNull(reader, 20),
                        LocalizedOrNull(reader, 21),
                        LocalizedOrNull(reader, 22))));
            }
        }

        var hasMore = rows.Count > page.Limit;
        var items = rows.Take(page.Limit).Select(r => r.View).ToList();

        // ⚠ TotalCount тут null: підрахунок видимого читачу робить ОБРОБНИК
        // (`CountCellChangesByColumnAsync` + межі читання S6) — читач порту не знає, що саме читач бачить.
        return new PagedResult<CellChangeView>(
            items,
            hasMore ? Cursor.Encode(rows[page.Limit - 1].Id) : null,
            TotalCount: null);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CellChangeColumnCount>> CountCellChangesByColumnAsync(
        CellChangeFilter filter, DateTime todayStartUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        // ⚠ Той самий WHERE, що й у читанні сторінки (одне джерело — BuildCellChangeWhere): вікно за
        // ChangedAt відсікає партиції, і підрахунок читає лише їх. Без курсору.
        var where = BuildCellChangeWhere(filter, command, afterId: null);

        // ⚠ Розріз за колонкою, а не одне число: відсів за межами читання (S6) — прерогатива виклику,
        // і сума з прихованих колонок не повинна навіть існувати поруч із відповіддю. Один прохід, без join.
        command.CommandText = $"""
            SELECT ColumnDefId,
                   COUNT_BIG(*),
                   COUNT_BIG(CASE WHEN ChangedAt >= @today THEN 1 END),
                   COUNT_BIG(CASE WHEN Origin = N'Import' THEN 1 END),
                   COUNT_BIG(CASE WHEN Origin = N'Recalculation' THEN 1 END)
              FROM aud.CellChange
             WHERE {where}
             GROUP BY ColumnDefId;
            """;
        command.Parameters.Add("@today", SqlDbType.DateTime2).Value = todayStartUtc;

        var counts = new List<CellChangeColumnCount>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            counts.Add(new CellChangeColumnCount(
                reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)));
        }

        return counts;
    }

    /// <summary>
    /// WHERE над <c>aud.CellChange</c> за фільтром: вікно, необов'язковий курсор і звуження.
    /// Параметри додаються в <paramref name="command"/>.
    /// </summary>
    private static StringBuilder BuildCellChangeWhere(CellChangeFilter filter, SqlCommand command, long? afterId)
    {
        // ⚠ Вікно за ChangedAt стоїть ПЕРШИМ у WHERE не заради стилю: саме воно
        // відсікає партиції. Курсор за Id додається до нього, а не замість —
        // інакше сторінка 20 читала б журнал цілком.
        var where = new StringBuilder("ChangedAt >= @from AND ChangedAt < @to");
        command.Parameters.AddWithValue("@from", filter.From);
        command.Parameters.AddWithValue("@to", filter.To);

        if (afterId is { } after)
        {
            where.Append("\n                   AND Id > @after");
            command.Parameters.AddWithValue("@after", after);
        }

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

        return where;
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
                   s.ChangedAt, s.EntityType, s.EntityId, s.Operation,
                   s.OldJson, s.NewJson, s.ChangeReason, s.ChangedByUserId, u.DisplayName
              FROM aud.StructureChange AS s
              LEFT JOIN sec.[User] AS u ON u.Id = s.ChangedByUserId
             WHERE s.EntityType IN ({string.Join(", ", names)})
                   AND s.EntityId = @entityId
             ORDER BY s.ChangedAt DESC, s.Id DESC;
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
                reader.GetInt32(7),
                StringOrNull(reader, 8)));
        }

        return rows;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StructureChangeView>> ReadRegistrySetSwitchesAsync(
        string registryCode, int limit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryCode);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        // ⚠ `ISJSON` — у CASE, а не окремою умовою WHERE: порядок умов SQL Server не гарантує,
        // і `OPENJSON` над записом старішого формату впав би на всьому читанні.
        command.CommandText = """
            SELECT TOP (@take)
                   s.ChangedAt, s.EntityType, s.EntityId, s.Operation,
                   s.OldJson, s.NewJson, s.ChangeReason, s.ChangedByUserId, u.DisplayName
              FROM aud.StructureChange AS s
              LEFT JOIN sec.[User] AS u ON u.Id = s.ChangedByUserId
             WHERE s.EntityType = N'cfg.RegistryDef'
                   AND s.EntityId = 0
                   AND EXISTS (
                       SELECT 1
                         FROM OPENJSON(CASE WHEN ISJSON(s.NewJson) = 1 THEN s.NewJson END, '$.registryCodes') AS j
                        WHERE j.type = 1 AND j.value = @code)
             ORDER BY s.ChangedAt DESC, s.Id DESC;
            """;

        command.Parameters.AddWithValue("@take", limit);
        command.Parameters.Add("@code", SqlDbType.NVarChar, 4000).Value = registryCode;

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
                reader.GetInt32(7),
                StringOrNull(reader, 8)));
        }

        return rows;
    }

    /// <inheritdoc />
    public async Task<PagedResult<StructureChangeView>> ReadStructureJournalAsync(
        StructureChangeFilter filter, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);

        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        var where = StructureJournalWhere(command, filter);

        // ⚠ `R-18`: ім'я автора — у зовнішньому запиті, після вибору сторінки
        // (та сама причина, що в `ReadCellChangesAsync`).
        command.CommandText = $"""
            SELECT s.Id, s.ChangedAt, s.EntityType, s.EntityId, s.Operation,
                   s.OldJson, s.NewJson, s.ChangeReason, s.ChangedByUserId, u.DisplayName
              FROM (
                    SELECT TOP (@take)
                           Id, ChangedAt, EntityType, EntityId, Operation,
                           OldJson, NewJson, ChangeReason, ChangedByUserId
                      FROM aud.StructureChange
                     WHERE {where}
                       AND Id > @after
                     ORDER BY Id
                   ) AS s
              LEFT JOIN sec.[User] AS u ON u.Id = s.ChangedByUserId
             ORDER BY s.Id;
            """;

        command.Parameters.Add("@take", SqlDbType.Int).Value = page.Limit + 1;
        command.Parameters.Add("@after", SqlDbType.BigInt).Value = Cursor.Decode(page.Cursor);

        var rows = new List<(long Id, StructureChangeView View)>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add((
                    reader.GetInt64(0),
                    new StructureChangeView(
                        DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
                        reader.GetString(2),
                        reader.GetInt32(3),
                        reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7),
                        reader.GetInt32(8),
                        StringOrNull(reader, 9))));
            }
        }

        var hasMore = rows.Count > page.Limit;

        return new PagedResult<StructureChangeView>(
            rows.Take(page.Limit).Select(r => r.View).ToList(),
            hasMore ? Cursor.Encode(rows[page.Limit - 1].Id) : null,
            TotalCount: null);
    }

    /// <inheritdoc />
    public async Task<int> CountStructureJournalAsync(StructureChangeFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM aud.StructureChange WHERE {StructureJournalWhere(command, filter)};";

        return (int)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <summary>Спільна умова переліку й підрахунку: стеля експорту рахує рівно те, що віддасть перелік.</summary>
    /// <remarks>
    /// ⚠ Вікно ПЕРШИМ (воно відсікає партиції і є префіксом кластерного ключа
    /// <c>(ChangedAt, Id)</c>), кожне звуження — лише за наявності значення і лише
    /// іменованим ТИПІЗОВАНИМ параметром.
    /// </remarks>
    private static StringBuilder StructureJournalWhere(SqlCommand command, StructureChangeFilter filter)
    {
        var where = new StringBuilder("ChangedAt >= @from AND ChangedAt < @to");

        if (filter.EntityType is { } entityType)
        {
            where.Append("\n                   AND EntityType = @entityType");
            command.Parameters.Add("@entityType", SqlDbType.NVarChar, EntityTypeSize).Value = entityType;
        }

        if (filter.ChangedByUserId is { } changedBy)
        {
            where.Append("\n                   AND ChangedByUserId = @changedBy");
            command.Parameters.Add("@changedBy", SqlDbType.Int).Value = changedBy;
        }

        // ⚠ `datetime2(3)` — ширина колонки зі схеми: інша точність змусила б
        // перетворювати КОЛОНКУ, і вікно перестало б відсікати партиції.
        foreach (var (name, value) in new[] { ("@from", filter.From), ("@to", filter.To) })
        {
            var moment = command.Parameters.Add(name, SqlDbType.DateTime2);
            moment.Scale = 3;
            moment.Value = value;
        }

        return where;
    }

    /// <inheritdoc />
    public async Task<PagedResult<SecurityEventView>> ReadSecurityEventsAsync(
        SecurityEventFilter filter, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);

        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        // ⚠ Вікно ПЕРШИМ (відсікає партиції), звуження — лише за наявності значення
        // й іменованим типізованим параметром; ім'я автора — у зовнішньому запиті
        // після вибору сторінки (R-18), як у `ReadStructureJournalAsync`.
        var where = new StringBuilder("ChangedAt >= @from AND ChangedAt < @to");

        if (filter.EventType is { } eventType)
        {
            where.Append("\n                   AND EventType = @eventType");
            command.Parameters.Add("@eventType", SqlDbType.NVarChar, EntityTypeSize).Value = eventType;
        }

        if (filter.ChangedByUserId is { } changedBy)
        {
            where.Append("\n                   AND ChangedByUserId = @changedBy");
            command.Parameters.Add("@changedBy", SqlDbType.Int).Value = changedBy;
        }

        foreach (var (name, value) in new[] { ("@from", filter.From), ("@to", filter.To) })
        {
            var moment = command.Parameters.Add(name, SqlDbType.DateTime2);
            moment.Scale = 3;
            moment.Value = value;
        }

        command.CommandText = $"""
            SELECT s.Id, s.ChangedAt, s.EventType, s.TargetUserId, s.TargetRoleId,
                   s.DetailsJson, s.ChangedByUserId, s.CorrelationId, u.DisplayName
              FROM (
                    SELECT TOP (@take)
                           Id, ChangedAt, EventType, TargetUserId, TargetRoleId,
                           DetailsJson, ChangedByUserId, CorrelationId
                      FROM aud.SecurityEvent
                     WHERE {where}
                       AND Id > @after
                     ORDER BY Id
                   ) AS s
              LEFT JOIN sec.[User] AS u ON u.Id = s.ChangedByUserId
             ORDER BY s.Id;
            """;

        command.Parameters.Add("@take", SqlDbType.Int).Value = page.Limit + 1;
        command.Parameters.Add("@after", SqlDbType.BigInt).Value = Cursor.Decode(page.Cursor);

        var rows = new List<(long Id, SecurityEventView View)>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add((
                    reader.GetInt64(0),
                    new SecurityEventView(
                        DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetInt32(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.GetInt32(6),
                        StringOrNull(reader, 8),
                        reader.IsDBNull(7) ? null : reader.GetString(7))));
            }
        }

        var hasMore = rows.Count > page.Limit;

        return new PagedResult<SecurityEventView>(
            rows.Take(page.Limit).Select(r => r.View).ToList(),
            hasMore ? Cursor.Encode(rows[page.Limit - 1].Id) : null,
            TotalCount: null);
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

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<(long TableRowId, int ColumnDefId)>> ReadOutOfWindowCellsAsync(
        long documentId, int periodKey, CancellationToken ct)
    {
        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = OutOfWindowCellsSql;
        command.Parameters.Add("@documentId", SqlDbType.BigInt).Value = documentId;
        command.Parameters.Add("@periodKey", SqlDbType.Int).Value = periodKey;

        var result = new List<(long TableRowId, int ColumnDefId)>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add((reader.GetInt64(0), reader.GetInt32(1)));
        }

        return result;
    }

    /// <summary>Текст запиту <see cref="ReadOutOfWindowCellsAsync"/>.</summary>
    /// <remarks>
    /// ⚠ Кандидати — лише рядки з <c>IsOutOfWindow = 1</c>: їх несе
    /// фільтрований індекс <c>IX_CellChange_OutOfWindow</c> (ключ
    /// <c>DocumentId, PeriodKey, TableRowId, ColumnDefId</c>, а <c>ChangedAt</c>
    /// і <c>Id</c> додає сам кластерний ключ), тож звичайні правки документа,
    /// яких тисячі, зовнішня частина не читає зовсім. Предикат — літерал
    /// <c>1</c>, а не параметр: інакше оптимізатор не має права взяти
    /// фільтрований індекс.
    ///
    /// ⚠ <c>NOT EXISTS</c> — «пізнішої зміни тієї самої комірки немає»; засічка
    /// по <c>IX_CellChange_Cell (DocumentId, TableRowId, ColumnDefId,
    /// ChangedAt DESC)</c>. Однакові <c>ChangedAt</c> (datetime2(3), пакет)
    /// розводить <c>Id</c>, тож на кожну комірку лишається рівно один рядок —
    /// без <c>DISTINCT</c>.
    ///
    /// ⛔ L6-12: нижня межа <c>@since</c> — раніша з двох: початок періоду чи
    /// розрахункове відкриття (<c>ComputedOpenAt</c>, зсув може бути від'ємним),
    /// мінус доба на пояс майданчика. Правити період до відкриття не дає
    /// <c>EditRules</c> (<c>Scheduled</c> → <c>PeriodNotOpenYet</c>), тож
    /// раніших правок поза вікном не буває. Доти запит на КОЖЕН GET зрізу читав
    /// усі місячні партиції <c>aud.CellChange</c>; тепер — від <c>@since</c>.
    /// Періоду в календарі немає або межі не пораховані — межі немає (як доти).
    /// <c>later.ChangedAt &gt;= o.ChangedAt</c> у <c>NOT EXISTS</c> логічно
    /// зайве — воно дає оптимізатору ту саму відсічку партицій.
    /// </remarks>
    public const string OutOfWindowCellsSql = """
        DECLARE @open datetime2(3) =
            (SELECT CASE WHEN p.ComputedOpenAt < CAST(p.PeriodStart AS datetime2(3))
                         THEN p.ComputedOpenAt ELSE CAST(p.PeriodStart AS datetime2(3)) END
               FROM doc.Period AS p
               JOIN doc.Document AS d ON d.ProjectId = p.ProjectId
              WHERE d.Id = @documentId AND p.PeriodKey = @periodKey);
        DECLARE @since datetime2(3) =
            CASE WHEN @open > '2000-01-01' THEN DATEADD(DAY, -1, @open) ELSE '0001-01-01' END;

        SELECT o.TableRowId, o.ColumnDefId
          FROM aud.CellChange AS o
         WHERE o.DocumentId = @documentId
           AND o.PeriodKey = @periodKey
           AND o.IsOutOfWindow = 1
           AND o.ChangedAt >= @since
           AND NOT EXISTS (
                 SELECT 1
                   FROM aud.CellChange AS later
                  WHERE later.DocumentId = o.DocumentId
                    AND later.TableRowId = o.TableRowId
                    AND later.ColumnDefId = o.ColumnDefId
                    AND later.ChangedAt >= o.ChangedAt
                    AND (later.ChangedAt > o.ChangedAt
                         OR (later.ChangedAt = o.ChangedAt AND later.Id > o.Id)));
        """;

    private static string? StringOrNull(SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>Локалізований текст із JSON-колонки; порожній або відсутній — <c>null</c>.</summary>
    private static Ecr.Domain.ValueObjects.LocalizedText? LocalizedOrNull(SqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var text = Ecr.Domain.ValueObjects.LocalizedText.FromJson(reader.GetString(ordinal));

        return text.Values.Count == 0 ? null : text;
    }

    /// <summary>Формат дати для повідомлень; не для запитів.</summary>
    internal static string Format(DateTime moment)
        => moment.ToString("O", CultureInfo.InvariantCulture);
}
