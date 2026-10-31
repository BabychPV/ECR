// src/Ecr.Infrastructure/Persistence/CellSourceStore.cs
using System.Data;
using Ecr.Application.Integration.RawData;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="ICellSourceStore"/> над <see cref="EcrDbContext"/> (<c>D-463</c>).</summary>
/// <remarks>
/// ⚠ Кожен запит — seek: мапінги за <c>IX_EntityFieldMap_TargetCell</c>, період за
/// <c>(ProjectId, PeriodKeyValue)</c>, події за <c>IX_CollectionCoverage_PeriodEvents</c> (обидва індекси —
/// міграція <c>D463RawDataReadIndexes</c>). Комірковий вхід відкривають усі користувачі документів, тож
/// скан журналу покриття (мільйони інтервалів за рік) на кожне відкриття інспектора неприпустимий.
/// </remarks>
public sealed class CellSourceStore(EcrDbContext db, Ecr.Application.Ports.IUnitCatalog units) : ICellSourceStore
{
    /// <summary>Події журналу покриття сутності за період, новіші першими.</summary>
    /// <remarks>⚠ <c>PeriodKey IS NOT NULL</c> — дослівно фільтр <c>IX_CollectionCoverage_PeriodEvents</c>: з параметром
    /// оптимізатор сам не виводить його з <c>PeriodKey = @period</c> для фільтрованого індексу.</remarks>
    public const string PeriodEventsSql = """
        SELECT TOP (@take) c.Id, c.[Status], c.Details, c.CoveredFrom
        FROM itg.CollectionCoverage AS c
        WHERE c.SourceEntityId = @entity AND c.PeriodKey = @period
          AND c.[Status] IS NOT NULL AND c.PeriodKey IS NOT NULL
        ORDER BY c.Id DESC;
        """;

    /// <summary>Останній запис журналу змін комірки в екземплярах таблиці документа за названі періоди.</summary>
    /// <remarks>
    /// ⚠ Seek-и: <c>UQ_TableInstance (PeriodKey, DocumentId, TableDefId)</c>, рядок за
    /// <c>(PeriodKey, TableInstanceId, RowKey)</c>, журнал за <c>IX_CellChange_Cell (DocumentId, TableRowId,
    /// ColumnDefId, ChangedAt DESC)</c>. Періодів — один або два (Q-17: фізична адреса — у періоді-власнику).
    /// </remarks>
    public const string LastWriteSql = """
        SELECT TOP (1) c.Origin, c.NewValue, c.ChangedAt
        FROM doc.TableInstance AS i
        JOIN doc.TableRow AS r ON r.PeriodKey = i.PeriodKey AND r.TableInstanceId = i.Id AND r.RowKey = @rowKey
        CROSS APPLY (SELECT TOP (1) cc.Origin, cc.NewValue, cc.ChangedAt, cc.Id
                     FROM aud.CellChange AS cc
                     WHERE cc.DocumentId = @document AND cc.TableRowId = r.Id AND cc.ColumnDefId = @column
                     ORDER BY cc.ChangedAt DESC, cc.Id DESC) AS c
        WHERE i.DocumentId = @document AND i.TableDefId = @table AND i.PeriodKey IN (@period, @owner)
        ORDER BY c.ChangedAt DESC, c.Id DESC;
        """;

    /// <inheritdoc />
    public async Task<CellFold> FoldAsync(CellSourceMap map, RawSeriesKey window, int maxPoints, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(window);

        if (map.Kind is not { } kind)
        {
            return new CellFold(CellFoldState.NoAggregation, null, null);
        }

        var range = new Domain.Entities.Documents.Period.UtcRange(window.FromUtc, window.ToUtc);
        var needsTime = Jobs.CollectedFold.IsTimeFold(kind);

        // ⛔ Стеля в пам'яті — `maxPoints` (+1 — ознака «довше»): понад неї числа НЕ показуємо, бо часткова сума —
        // хибне число, а не «приблизне» (той самий принцип, що в стелі матеріалізації, D16-03).
        var inside = await Jobs.CollectedFold
            .LoadInsideAsync(db, map.SourceEntityId, map.SourcePath, range, needsTime, maxPoints + 1, ct)
            .ConfigureAwait(false);
        if (inside.Count > maxPoints)
        {
            return new CellFold(CellFoldState.TooManyPoints, null, null);
        }

        var bounds = needsTime
            ? await Jobs.CollectedFold.LoadBoundsAsync(db, map.SourceEntityId, map.SourcePath, range, ct).ConfigureAwait(false)
            : default;

        Ecr.Application.Ports.UnitCatalogSnapshot? catalog = null;
        if ((needsTime && Jobs.CollectedFold.HasForeignEdge(bounds, map.SourceUnitId))
            || Jobs.CollectedFold.NeedsCatalog(kind, map.SourceUnitId, map.TargetUnitId))
        {
            catalog = await units.GetAsync(ct).ConfigureAwait(false);
        }

        // ✎ Аудит 31.10 (G4): «очищено» — лише коли точки вікна справді видаляли (те саме правило, що в матеріалізації).
        var removed = inside.Count == 0
            && await Jobs.CollectedFold.RemovedInWindowAsync(db, map.SourceEntityId, map.SourcePath, range, ct).ConfigureAwait(false);

        var outcome = Jobs.CollectedFold.FoldMap(kind, map.IsStep, map.SourceUnitId, inside, bounds, range, catalog, removed);
        switch (outcome.Kind)
        {
            case Jobs.FoldOutcomeKind.MixedUnits:
                return new CellFold(CellFoldState.MixedUnits, null, null);
            case Jobs.FoldOutcomeKind.Clear:
                return new CellFold(CellFoldState.Cleared, null, null);
        }

        if (outcome.Folded is not { } folded)
        {
            return new CellFold(CellFoldState.NoData, null, null);
        }

        var (storable, failure) = Jobs.CollectedFold.ToStorable(kind, folded, map.SourceUnitId, map.TargetUnitId, catalog);
        return failure is null
            ? new CellFold(CellFoldState.Value, storable, outcome.PercentGood)
            : new CellFold(CellFoldState.UnitFailure, null, outcome.PercentGood);
    }

    /// <inheritdoc />
    public async Task<CellLastWrite?> LastWriteAsync(
        long documentId, int tableDefId, string rowKey, int columnDefId, IReadOnlyList<int> periodKeys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(periodKeys);

        await using var command = await CommandAsync(LastWriteSql, ct).ConfigureAwait(false);
        command.Parameters.Add("@document", SqlDbType.BigInt).Value = documentId;
        command.Parameters.Add("@table", SqlDbType.Int).Value = tableDefId;
        command.Parameters.Add("@rowKey", SqlDbType.NVarChar, 100).Value = rowKey;
        command.Parameters.Add("@column", SqlDbType.Int).Value = columnDefId;
        command.Parameters.Add("@period", SqlDbType.Int).Value = periodKeys[0];
        command.Parameters.Add("@owner", SqlDbType.Int).Value = periodKeys[^1];

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new CellLastWrite(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc))
            : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CellSourceMap>> MapsAsync(
        int columnDefId, string rowKey, int max, CancellationToken ct)
    {
        var rows = await (
                from m in db.EntityFieldMaps.AsNoTracking()
                where m.TargetColumnDefId == columnDefId && m.TargetRowKey == rowKey
                join e in db.SourceEntities.AsNoTracking() on m.SourceEntityId equals e.Id
                join su in db.Units.AsNoTracking() on m.SourceUnitId equals (int?)su.Id into sourceUnits
                from su in sourceUnits.DefaultIfEmpty()
                join tu in db.Units.AsNoTracking() on m.TargetUnitId equals (int?)tu.Id into targetUnits
                from tu in targetUnits.DefaultIfEmpty()
                orderby m.Id
                select new
                {
                    m.Id,
                    m.SourceEntityId,
                    e.Code,
                    e.DisplayName,
                    m.SourceField,
                    m.TransformCode,
                    SourceUnit = su == null ? null : su.Code,
                    TargetUnit = tu == null ? null : tu.Code,
                    m.IsActive,
                    m.IsStep,
                    m.SourceUnitId,
                    m.TargetUnitId,
                })
            .Take(max)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ConvertAll(r => new CellSourceMap(
            r.Id, r.SourceEntityId, r.Code, r.DisplayName, r.SourceField,
            RawDataReader.Aggregation(r.TransformCode), r.SourceUnit, r.TargetUnit, r.IsActive,
            Enum.TryParse<Ecr.Domain.Entities.External.AggregationKind>(r.TransformCode, out var kind) ? kind : null,
            r.IsStep, r.SourceUnitId, r.TargetUnitId));
    }

    /// <inheritdoc />
    public async Task<CellPeriodWindow?> PeriodAsync(long documentId, int periodKey, CancellationToken ct)
        => await (
                from d in db.Documents.AsNoTracking()
                where d.Id == documentId
                join p in db.Periods.AsNoTracking() on d.ProjectId equals p.ProjectId
                where p.PeriodKeyValue == periodKey
                join project in db.Projects.AsNoTracking() on d.ProjectId equals project.Id
                select new CellPeriodWindow(p.PeriodStart, p.PeriodEnd, project.TimeZoneId))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<CellSourceEvent>> PeriodEventsAsync(
        int sourceEntityId, int periodKey, int take, CancellationToken ct)
    {
        await using var command = await CommandAsync(PeriodEventsSql, ct).ConfigureAwait(false);
        command.Parameters.Add("@take", SqlDbType.Int).Value = take;
        command.Parameters.Add("@entity", SqlDbType.Int).Value = sourceEntityId;
        command.Parameters.Add("@period", SqlDbType.Int).Value = periodKey;

        var result = new List<CellSourceEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new CellSourceEvent(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)));
        }

        return result;
    }

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
}
