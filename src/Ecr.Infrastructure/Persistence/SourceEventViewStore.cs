// src/Ecr.Infrastructure/Persistence/SourceEventViewStore.cs
using System.Data;
using System.Text.Json;
using Ecr.Application.Integration.RawData;
using Ecr.Application.Integration.SourceEvents;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="ISourceEventViewStore"/> над <see cref="EcrDbContext"/> (<c>D-465</c>).</summary>
/// <remarks>
/// ⚠ Кожен текст — константа (бойовий; його ж міряють seek-тести) з точними типами параметрів і тайм-аутом
/// <see cref="RawDataReader.CommandTimeoutSeconds"/>; скасування — з запиту.
/// </remarks>
public sealed class SourceEventViewStore(
    EcrDbContext db,
    IUnitCatalog units,
    Application.Permits.Groups.ILookupTargetResolver? groups = null) : ISourceEventViewStore
{
    /// <summary>
    /// Сторінка зв'язків мапінгу: seek <c>IX_SEL_MapStart (SourceEventMapId, StartUtc, Id)</c> у діапазоні
    /// <c>[@from, min(@to, курсор)]</c>, зворотним проходом (новіші першими); фільтри — з INCLUDE.
    /// </summary>
    public const string LinksSql = """
        SELECT TOP (@take) l.Id, l.SourceEventMapId, l.SourceEventId, l.EventName, l.StartUtc, l.EndUtc,
               l.SourceModifiedUtc, l.PrimaryElement, l.[Status], l.PeriodKey, l.TableInstanceId, l.RowKey,
               l.KeptManualJson, l.UnmappedJson, l.FirstSeenAt, l.LastSeenAt, l.LastSyncAt
          FROM ext.SourceEventLink AS l
         WHERE l.SourceEventMapId = @map
           AND l.StartUtc >= @from AND l.StartUtc < @to AND l.StartUtc <= @upper
           AND (l.StartUtc < @upper OR l.Id < @afterId)
           AND (@statuses IS NULL OR l.[Status] IN (SELECT s.value FROM OPENJSON(@statuses) AS s))
           AND (@element IS NULL OR l.PrimaryElement LIKE @element ESCAPE N'\')
           AND (@name IS NULL OR l.EventName LIKE @name ESCAPE N'\')
         ORDER BY l.StartUtc DESC, l.Id DESC;
        """;

    /// <summary>Підрахунок зв'язків мапінгу під фільтром — лише індекс <c>IX_SEL_MapStart</c> (з INCLUDE).</summary>
    public const string CountSql = """
        SELECT COUNT_BIG(*)
          FROM ext.SourceEventLink AS l
         WHERE l.SourceEventMapId = @map
           AND l.StartUtc >= @from AND l.StartUtc < @to
           AND (@statuses IS NULL OR l.[Status] IN (SELECT s.value FROM OPENJSON(@statuses) AS s))
           AND (@element IS NULL OR l.PrimaryElement LIKE @element ESCAPE N'\')
           AND (@name IS NULL OR l.EventName LIKE @name ESCAPE N'\');
        """;

    /// <summary>Значення атрибутів зв'язків — seek за префіксом <c>PK_SourceEventAttributeValue (LinkId, …)</c>.</summary>
    public const string ValuesSql = """
        SELECT v.LinkId, v.Scope, v.Attribute, v.ValueNumeric, v.ValueText, v.IsTruncated, v.SourceUnit,
               v.SourceModifiedUtc, v.ChangedAt
          FROM ext.SourceEventAttributeValue AS v
         WHERE v.LinkId IN (SELECT l.id FROM OPENJSON(@links) WITH (id bigint '$') AS l);
        """;

    /// <summary>
    /// Комірки рядків подій у названих колонках і останнє походження за журналом змін: рядок — ключ
    /// <c>(PeriodKey, TableInstanceId, RowKey)</c>, комірка — <c>PK_CellValue</c>, журнал — <c>IX_CellChange_Cell</c>.
    /// </summary>
    public const string CellsSql = """
        SELECT k.p, k.t, k.r, col.c,
               cv.IsEmpty, cv.ValueNumeric, cv.ValueString, cv.ValueDate, cv.ValueRegistryEntryId,
               lc.Origin, lc.ChangedAt
          FROM OPENJSON(@rows) WITH (p int '$.p', t bigint '$.t', r nvarchar(100) '$.r', d bigint '$.d') AS k
          JOIN doc.TableRow AS tr
            ON tr.PeriodKey = k.p AND tr.TableInstanceId = k.t AND tr.RowKey = k.r AND tr.IsDeleted = 0
         CROSS JOIN OPENJSON(@cols) WITH (c int '$') AS col
          LEFT JOIN doc.CellValue AS cv
            ON cv.PeriodKey = tr.PeriodKey AND cv.TableRowId = tr.Id AND cv.ColumnDefId = col.c
         OUTER APPLY (SELECT TOP (1) cc.Origin, cc.ChangedAt
                        FROM aud.CellChange AS cc
                       WHERE cc.DocumentId = k.d AND cc.TableRowId = tr.Id AND cc.ColumnDefId = col.c
                       ORDER BY cc.ChangedAt DESC, cc.Id DESC) AS lc;
        """;

    /// <summary>
    /// Журнал видалень рядків подій сутності за вікном: seek кластерного ключа <c>(ChangedAt, Id)</c> журналу
    /// структурних змін (одиниці записів на день), сутність — з <c>OldJson</c>.
    /// </summary>
    public const string RemovalsSql = """
        SELECT TOP (@take) s.ChangedAt, s.OldJson, s.CorrelationId
          FROM aud.StructureChange AS s WITH (FORCESEEK)
         WHERE s.ChangedAt >= @from AND s.ChangedAt < @to
           AND s.EntityType = N'ext.SourceEventLink' AND s.Operation = N'SourceEventRowRemoved'
           AND JSON_VALUE(s.OldJson, '$.sourceEntityId') = @entity
         ORDER BY s.ChangedAt DESC, s.Id DESC;
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public Task<IReadOnlyList<PiEventMap>> MapsAsync(int sourceEntityId, CancellationToken ct)
        => LoadMapsAsync(db.SourceEventMaps.AsNoTracking().Where(m => m.SourceEntityId == sourceEntityId), ct);

    /// <inheritdoc />
    public async Task<PiEventMap?> MapAsync(int mapId, CancellationToken ct)
    {
        var maps = await LoadMapsAsync(db.SourceEventMaps.AsNoTracking().Where(m => m.Id == mapId), ct).ConfigureAwait(false);
        return maps.Count == 0 ? null : maps[0];
    }

    /// <inheritdoc />
    public async Task<PiEventMapContext> ContextAsync(PiEventMap map, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(map);

        var entity = await db.SourceEventMaps
            .AsNoTracking()
            .Include(m => m.Fields)
            .ThenInclude(f => f.Values)
            .FirstAsync(m => m.Id == map.MapId, ct)
            .ConfigureAwait(false);

        var set = await SourceEventFieldPlans.LoadAsync(db, entity, groups, ct).ConfigureAwait(false);
        var catalog = set.Plans.Any(p => p.SourceUnitId is not null)
            ? await units.GetAsync(ct).ConfigureAwait(false)
            : null;
        var periods = await PeriodsAsync(map, ct).ConfigureAwait(false);

        // Подані/затверджені аркуші таблиці мапінгу — одним запитом за всі періоди (як у синку, D-464).
        var locked = await (
                from t in db.TableDefs.AsNoTracking()
                where t.Id == map.TableDefId
                join a in db.ApprovalStates.AsNoTracking() on t.SheetDefId equals a.SheetDefId
                where a.DocumentId == map.DocumentId
                      && (a.Status == DocumentStatus.Submitted || a.Status == DocumentStatus.Approved)
                select a.PeriodKey)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PiEventMapContext(
            set.Plans,
            set.Columns.Values.ToDictionary(c => c.Id, c => c.Scale),
            catalog,
            periods,
            locked.ToHashSet());
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SourceEventPeriod>> PeriodsAsync(PiEventMap map, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(map);

        var tz = SiteTimeZone.Create(map.TimeZoneId).ToTimeZoneInfo();
        var rows = await (
                from d in db.Documents.AsNoTracking()
                where d.Id == map.DocumentId
                join p in db.Periods.AsNoTracking() on d.ProjectId equals p.ProjectId
                select new { p.PeriodKeyValue, p.State, p.PeriodStart, p.PeriodEnd })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. rows
            .Select(p => new SourceEventPeriod(
                p.PeriodKeyValue, p.State, Domain.Entities.Documents.Period.UtcBounds(p.PeriodStart, p.PeriodEnd, tz)))
            .OrderBy(p => p.Bounds.StartUtc)];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PiEventLink>> LinksAsync(
        int mapId, PiEventFilter filter, PiEventCursor? before, int take, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await using var command = await CommandAsync(LinksSql, ct).ConfigureAwait(false);
        command.Parameters.Add("@take", SqlDbType.Int).Value = take;
        BindFilter(command, mapId, filter);
        command.Parameters.Add(new SqlParameter("@upper", SqlDbType.DateTime2)
        {
            Scale = 7,
            Value = before is null || before.StartUtc > filter.ToUtc ? filter.ToUtc : before.StartUtc,
        });
        command.Parameters.Add("@afterId", SqlDbType.BigInt).Value = before?.LinkId ?? long.MaxValue;

        var result = new List<PiEventLink>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadLink(reader));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<int> CountLinksAsync(int mapId, PiEventFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await using var command = await CommandAsync(CountSql, ct).ConfigureAwait(false);
        BindFilter(command, mapId, filter);
        return (int)Math.Min(int.MaxValue, (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PiEventLink>> LinksOfRowAsync(
        long tableInstanceId, string rowKey, int take, CancellationToken ct)
        => await db.SourceEventLinks
            .AsNoTracking()
            .Where(l => l.TableInstanceId == tableInstanceId && l.RowKey == rowKey)
            .OrderBy(l => l.Id)
            .Take(take)
            .Select(l => new PiEventLink(
                l.Id, l.SourceEventMapId, l.SourceEventId, l.EventName, l.StartUtc, l.EndUtc, l.SourceModifiedUtc,
                l.PrimaryElement, l.Status, l.PeriodKey, l.TableInstanceId, l.RowKey, l.KeptManualJson, l.UnmappedJson,
                l.FirstSeenAt, l.LastSeenAt, l.LastSyncAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PiObservedValue>> ValuesAsync(IReadOnlyCollection<long> linkIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(linkIds);
        if (linkIds.Count == 0)
        {
            return [];
        }

        await using var command = await CommandAsync(ValuesSql, ct).ConfigureAwait(false);
        command.Parameters.Add("@links", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(linkIds, Json);

        var result = new List<PiObservedValue>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new PiObservedValue(
                reader.GetInt64(0),
                (SourceEventAttributeScope)reader.GetByte(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetBoolean(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc),
                DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc)));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PiCellValue>> CellsAsync(
        IReadOnlyList<PiRowAddress> rows, IReadOnlyCollection<int> columnDefIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(columnDefIds);
        if (rows.Count == 0 || columnDefIds.Count == 0)
        {
            return [];
        }

        await using var command = await CommandAsync(CellsSql, ct).ConfigureAwait(false);
        command.Parameters.Add("@rows", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(
            rows.Select(r => new { p = r.PeriodKey, t = r.TableInstanceId, r = r.RowKey, d = r.DocumentId }), Json);
        command.Parameters.Add("@cols", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(columnDefIds.Distinct(), Json);

        var result = new List<PiCellValue>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var exists = !reader.IsDBNull(4) && !reader.GetBoolean(4);
            result.Add(new PiCellValue(
                reader.GetInt32(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetInt32(3),
                exists,
                reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : DateTime.SpecifyKind(reader.GetDateTime(10), DateTimeKind.Utc)));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PiEventRemoval>> RemovalsAsync(
        int sourceEntityId, DateTime fromUtc, DateTime toUtc, int take, CancellationToken ct)
    {
        await using var command = await CommandAsync(RemovalsSql, ct).ConfigureAwait(false);
        command.Parameters.Add("@take", SqlDbType.Int).Value = take;
        command.Parameters.Add(new SqlParameter("@from", SqlDbType.DateTime2) { Scale = 3, Value = fromUtc });
        command.Parameters.Add(new SqlParameter("@to", SqlDbType.DateTime2) { Scale = 3, Value = toUtc });
        command.Parameters.Add("@entity", SqlDbType.NVarChar, 32).Value =
            sourceEntityId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var result = new List<PiEventRemoval>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var at = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc);
            var json = reader.IsDBNull(1) ? null : reader.GetString(1);
            var run = reader.IsDBNull(2) ? null : reader.GetString(2);
            result.Add(ParseRemoval(at, json, run));
        }

        return result;
    }

    /// <summary>Склад видаленого з <c>OldJson</c>; зіпсований JSON — запис лише з моментом (а не 500 на екрані).</summary>
    public static PiEventRemoval ParseRemoval(DateTime at, string? json, string? runId)
    {
        RemovalJson? parsed = null;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                parsed = JsonSerializer.Deserialize<RemovalJson>(json, Json);
            }
            catch (JsonException)
            {
                parsed = null;
            }
        }

        return new PiEventRemoval(
            at,
            parsed?.EventId,
            parsed?.EventName,
            parsed?.EventStartUtc is { } start ? DateTime.SpecifyKind(start, DateTimeKind.Utc) : null,
            parsed?.PeriodKey,
            parsed?.MapId,
            parsed?.DocumentId,
            parsed?.TableInstanceId,
            parsed?.RowKey,
            parsed?.RunId ?? runId);
    }

    /// <summary>Екранує шаблон <c>LIKE</c> (<c>\</c> — символ екранування).</summary>
    public static string EscapeLike(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal)
            .Replace("[", @"\[", StringComparison.Ordinal);
    }

    private async Task<IReadOnlyList<PiEventMap>> LoadMapsAsync(
        IQueryable<Domain.Entities.External.SourceEventMap> source, CancellationToken ct)
    {
        var maps = await source
            .Include(m => m.Fields)
            .OrderBy(m => m.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (maps.Count == 0)
        {
            return [];
        }

        var documentIds = maps.Select(m => m.DocumentId).Distinct().ToList();
        var documents = await (
                from d in db.Documents.AsNoTracking()
                where documentIds.Contains(d.Id)
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                select new { d.Id, d.BusinessKey, p.TimeZoneId })
            .ToDictionaryAsync(x => x.Id, ct)
            .ConfigureAwait(false);

        var tableIds = maps.Select(m => m.TableDefId).Distinct().ToList();
        var tables = await db.TableDefs.AsNoTracking()
            .Where(t => tableIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Code })
            .ToDictionaryAsync(t => t.Id, t => t.Code, ct)
            .ConfigureAwait(false);

        var columnIds = maps.SelectMany(m => m.Fields).Select(f => f.TargetColumnDefId).Distinct().ToList();
        var columns = await db.ColumnDefs.AsNoTracking()
            .Where(c => columnIds.Contains(c.Id) && !c.IsDeleted)
            .Select(c => new { c.Id, c.Code, c.IsMonthColumn })
            .ToDictionaryAsync(c => c.Id, ct)
            .ConfigureAwait(false);

        var entityIds = maps.Select(m => m.SourceEntityId).Distinct().ToList();
        var entities = await db.SourceEntities.AsNoTracking()
            .Where(e => entityIds.Contains(e.Id))
            .Select(e => new { e.Id, e.Code, e.DisplayName })
            .ToDictionaryAsync(e => e.Id, ct)
            .ConfigureAwait(false);

        return [.. maps
            .Where(m => documents.ContainsKey(m.DocumentId) && entities.ContainsKey(m.SourceEntityId))
            .Select(m => new PiEventMap(
                m.Id,
                m.SourceEntityId,
                entities[m.SourceEntityId].Code,
                entities[m.SourceEntityId].DisplayName,
                m.DocumentId,
                documents[m.DocumentId].BusinessKey,
                m.TableDefId,
                tables.GetValueOrDefault(m.TableDefId) ?? string.Empty,
                documents[m.DocumentId].TimeZoneId,
                m.IsActive,
                [.. m.Fields
                    .OrderBy(f => f.Id)
                    .Select(f => new PiEventMapField(
                        f.TargetColumnDefId,
                        columns.GetValueOrDefault(f.TargetColumnDefId)?.Code,
                        f.SourceAttribute,
                        f.AttributeScope,
                        columns.GetValueOrDefault(f.TargetColumnDefId)?.IsMonthColumn == true))]))];
    }

    private static void BindFilter(SqlCommand command, int mapId, PiEventFilter filter)
    {
        command.Parameters.Add("@map", SqlDbType.Int).Value = mapId;
        command.Parameters.Add(new SqlParameter("@from", SqlDbType.DateTime2) { Scale = 7, Value = filter.FromUtc });
        command.Parameters.Add(new SqlParameter("@to", SqlDbType.DateTime2) { Scale = 7, Value = filter.ToUtc });
        command.Parameters.Add("@statuses", SqlDbType.NVarChar, -1).Value = filter.Statuses.Count == 0
            ? DBNull.Value
            : JsonSerializer.Serialize(filter.Statuses.Select(s => s.ToString()), Json);
        command.Parameters.Add("@element", SqlDbType.NVarChar, 210).Value = filter.ElementPrefix is { } element
            ? EscapeLike(element) + "%"
            : DBNull.Value;
        command.Parameters.Add("@name", SqlDbType.NVarChar, 420).Value = filter.NameContains is { } name
            ? "%" + EscapeLike(name) + "%"
            : DBNull.Value;
    }

    private static PiEventLink ReadLink(SqlDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
            reader.IsDBNull(5) ? null : DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc),
            reader.IsDBNull(6) ? null : DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            Enum.Parse<SourceEventLinkStatus>(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetInt32(9),
            reader.IsDBNull(10) ? null : reader.GetInt64(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            DateTime.SpecifyKind(reader.GetDateTime(14), DateTimeKind.Utc),
            DateTime.SpecifyKind(reader.GetDateTime(15), DateTimeKind.Utc),
            DateTime.SpecifyKind(reader.GetDateTime(16), DateTimeKind.Utc));

    private async Task<SqlCommand> CommandAsync(string sql, CancellationToken ct)
    {
        var connection = (SqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = RawDataReader.CommandTimeoutSeconds;
        if (db.Database.CurrentTransaction?.GetDbTransaction() is SqlTransaction transaction)
        {
            command.Transaction = transaction;
        }

        return command;
    }

    private sealed record RemovalJson(
        string? EventId,
        string? EventName,
        DateTime? EventStartUtc,
        string? RowKey,
        int? PeriodKey,
        long? TableInstanceId,
        long? DocumentId,
        int? MapId,
        string? RunId);
}
