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
public sealed class CellSourceStore(EcrDbContext db) : ICellSourceStore
{
    /// <summary>Події журналу покриття сутності за період, новіші першими.</summary>
    public const string PeriodEventsSql = """
        SELECT TOP (@take) c.Id, c.[Status], c.Details, c.CoveredFrom
        FROM itg.CollectionCoverage AS c
        WHERE c.SourceEntityId = @entity AND c.PeriodKey = @period AND c.[Status] IS NOT NULL
        ORDER BY c.Id DESC;
        """;

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
                })
            .Take(max)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ConvertAll(r => new CellSourceMap(
            r.Id, r.SourceEntityId, r.Code, r.DisplayName, r.SourceField,
            RawDataReader.Aggregation(r.TransformCode), r.SourceUnit, r.TargetUnit, r.IsActive));
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
        var connection = (SqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = PeriodEventsSql;
        command.CommandTimeout = RawDataReader.CommandTimeoutSeconds;
        if (db.Database.CurrentTransaction?.GetDbTransaction() is SqlTransaction transaction)
        {
            command.Transaction = transaction;
        }

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
}
