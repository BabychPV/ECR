// src/Ecr.Infrastructure/Persistence/RawDataReader.cs
using System.Data;
using Ecr.Application.Integration.RawData;
using Ecr.Domain.Entities.External;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IRawDataReader"/> над <see cref="EcrDbContext"/> (<c>D-463</c>).</summary>
/// <remarks>
/// ⛔ Запити до <c>ext.RawDataPoint</c> — сирим SQL із <c>WITH (FORCESEEK)</c> і точними типами параметрів
/// (<c>nvarchar(400)</c>, <c>datetime2(3)</c>). Причина — точка перелому: для «рік щільного тега» в малій
/// базі оптимізатор вибрав би скан кластерного індексу (уся таблиця всіх тегів), а неявне перетворення
/// параметра вимкнуло б seek узагалі. Підказка без імені індексу навмисно: після R2b
/// (<c>docs/build/perf/R2-rawdatapoint-plan.md</c>) кластером стане сам ключ
/// <c>(SourceEntityId, SourcePath, Timestamp)</c>, а <c>UQ_RawDataPoint</c> зникне — підказка з іменем зламалася б.
/// Форму планів тримає <c>RawDataSeekTests</c>.
/// <para>
/// ⚠ Тайм-аут команди — <see cref="CommandTimeoutSeconds"/>: найдовша дозволена згортка (рік щохвилини,
/// ~525 тис. точок через пошук за ключем) вкладається з запасом, а завислий запит не тримає потік запиту
/// довше. Скасування запиту клієнтом скасовує й команду (<see cref="CancellationToken"/>).
/// </para>
/// </remarks>
public sealed class RawDataReader(EcrDbContext db) : IRawDataReader
{
    /// <summary>Тайм-аут команд читання точок.</summary>
    public const int CommandTimeoutSeconds = 30;

    /// <summary>Сторінка точок ряду, новіші першими.</summary>
    public const string PageSql = """
        SELECT TOP (@take) p.[Timestamp], p.ValueNumeric, p.ValueString, p.UnitId, p.Quality, p.RetrievedAt, p.CollectionRunId
        FROM ext.RawDataPoint AS p WITH (FORCESEEK)
        WHERE p.SourceEntityId = @entity AND p.SourcePath = @path
          AND p.[Timestamp] >= @from AND p.[Timestamp] < @to
        ORDER BY p.[Timestamp] DESC;
        """;

    /// <summary>Сторінка точок ряду за зростанням часу (експорт).</summary>
    public const string AscendingSql = """
        SELECT TOP (@take) p.[Timestamp], p.ValueNumeric, p.ValueString, p.UnitId, p.Quality, p.RetrievedAt, p.CollectionRunId
        FROM ext.RawDataPoint AS p WITH (FORCESEEK)
        WHERE p.SourceEntityId = @entity AND p.SourcePath = @path
          AND p.[Timestamp] > @from AND p.[Timestamp] < @to
        ORDER BY p.[Timestamp] ASC;
        """;

    /// <summary>Кількість точок ряду (лише індекс).</summary>
    public const string CountSql = """
        SELECT COUNT_BIG(*)
        FROM ext.RawDataPoint AS p WITH (FORCESEEK)
        WHERE p.SourceEntityId = @entity AND p.SourcePath = @path
          AND p.[Timestamp] >= @from AND p.[Timestamp] < @to;
        """;

    /// <summary>
    /// Згортка ряду в кошики: номер кошика — мілісекунди від початку вікна, поділені на ширину.
    /// </summary>
    /// <remarks>
    /// ⚠ Мілісекунди, а не <c>DATEDIFF(SECOND)</c>: той рахує перетнуті межі секунд, і точка 00:00:01.200
    /// при початку 00:00:00.500 потрапила б у наступний кошик. <c>datetime2(3)</c> — рівно мілісекунди.
    /// </remarks>
    public const string BucketsSql = """
        SELECT b.Bucket, COUNT_BIG(*) AS Points, COUNT_BIG(p.ValueNumeric) AS NumericPoints,
               MIN(p.ValueNumeric) AS MinValue, MAX(p.ValueNumeric) AS MaxValue, AVG(p.ValueNumeric) AS AvgValue
        FROM ext.RawDataPoint AS p WITH (FORCESEEK)
        CROSS APPLY (SELECT DATEDIFF_BIG(MILLISECOND, @from, p.[Timestamp]) / @bucketMs AS Bucket) AS b
        WHERE p.SourceEntityId = @entity AND p.SourcePath = @path
          AND p.[Timestamp] >= @from AND p.[Timestamp] < @to
        GROUP BY b.Bucket
        ORDER BY b.Bucket;
        """;

    /// <summary>Покриті інтервали сутності, що перетинають вікно (фільтрований індекс <c>IX_CollectionCoverage_SourceEntity_CoveredTo</c>).</summary>
    public const string CoveredSql = """
        SELECT TOP (@take) c.CoveredFrom, c.CoveredTo
        FROM itg.CollectionCoverage AS c
        WHERE c.SourceEntityId = @entity AND c.[Status] IS NULL
          AND c.CoveredTo > @from AND c.CoveredFrom < @to
        ORDER BY c.CoveredTo;
        """;

    /// <inheritdoc />
    public async Task<RawSourceEntity?> FindEntityAsync(int sourceEntityId, CancellationToken ct)
        => await db.SourceEntities
            .AsNoTracking()
            .Where(e => e.Id == sourceEntityId)
            .Select(e => new RawSourceEntity(e.Id, e.Code, e.DisplayName, e.EntityPath, e.IsActive))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RawDataTag>> ListTagsAsync(int sourceEntityId, int max, CancellationToken ct)
    {
        // ⚠ Один запит: мапінги + колонка + одиниця джерела; призупинені теж — їхні точки лишаються.
        var rows = await (
                from m in db.EntityFieldMaps.AsNoTracking()
                where m.SourceEntityId == sourceEntityId
                join c in db.ColumnDefs.AsNoTracking().Where(c => !c.IsDeleted) on m.TargetColumnDefId equals (int?)c.Id into cols
                from c in cols.DefaultIfEmpty()
                join u in db.Units.AsNoTracking() on m.SourceUnitId equals (int?)u.Id into srcUnits
                from u in srcUnits.DefaultIfEmpty()
                orderby m.SourceField
                select new
                {
                    m.Id,
                    m.SourceField,
                    ColumnCode = c == null ? null : c.Code,
                    m.TargetRowKey,
                    m.TransformCode,
                    UnitCode = u == null ? null : u.Code,
                    m.IsActive,
                })
            .Take(max)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ConvertAll(r => new RawDataTag(
            r.SourceField, r.Id, r.ColumnCode, r.TargetRowKey, Aggregation(r.TransformCode), r.UnitCode, r.IsActive));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RawPoint>> PageAsync(RawSeriesKey key, DateTime? before, int take, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        var upper = before is { } b && b < key.ToUtc ? b : key.ToUtc;
        return PointsAsync(PageSql, key, key.FromUtc, upper, take, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RawPoint>> PageAscendingAsync(
        RawSeriesKey key, DateTime? after, int take, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        // ⚠ Перша сторінка — `> from - 1 мс`, тобто включно з `from` у точності datetime2(3).
        var lower = after is { } a && a >= key.FromUtc ? a : key.FromUtc.AddMilliseconds(-1);
        return PointsAsync(AscendingSql, key, lower, key.ToUtc, take, ct);
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(RawSeriesKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        await using var command = await CommandAsync(CountSql, ct).ConfigureAwait(false);
        AddKey(command, key.SourceEntityId, key.SourcePath, key.FromUtc, key.ToUtc);

        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RawBucket>> BucketsAsync(RawSeriesKey key, long bucketSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(bucketSeconds, 1);

        await using var command = await CommandAsync(BucketsSql, ct).ConfigureAwait(false);
        AddKey(command, key.SourceEntityId, key.SourcePath, key.FromUtc, key.ToUtc);
        command.Parameters.Add("@bucketMs", SqlDbType.BigInt).Value = checked(bucketSeconds * 1000);

        var result = new List<RawBucket>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new RawBucket(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : SafeDecimal(reader.GetSqlDecimal(3)),
                reader.IsDBNull(4) ? null : SafeDecimal(reader.GetSqlDecimal(4)),
                reader.IsDBNull(5) ? null : SafeDecimal(reader.GetSqlDecimal(5))));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RawInterval>> CoveredAsync(
        int sourceEntityId, DateTime fromUtc, DateTime toUtc, int take, CancellationToken ct)
    {
        await using var command = await CommandAsync(CoveredSql, ct).ConfigureAwait(false);
        command.Parameters.Add("@take", SqlDbType.Int).Value = take;
        command.Parameters.Add("@entity", SqlDbType.Int).Value = sourceEntityId;
        command.Parameters.Add("@from", SqlDbType.DateTime2, 3).Value = fromUtc;
        command.Parameters.Add("@to", SqlDbType.DateTime2, 3).Value = toUtc;

        var result = new List<RawInterval>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new RawInterval(Utc(reader.GetDateTime(0)), Utc(reader.GetDateTime(1))));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<RawCollectionStatus> LastCollectionAsync(int sourceEntityId, CancellationToken ct)
    {
        // ⚠ Обидва — seek у `IX_CollectionRun_SourceEntityId_Id` (Id спадно, INCLUDE StartedAt/Status/FinishedAt).
        var last = await db.CollectionRuns
            .AsNoTracking()
            .Where(r => r.SourceEntityId == sourceEntityId)
            .OrderByDescending(r => r.Id)
            .Select(r => new { r.Id, r.Status, r.StartedAt, r.FinishedAt })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (last is null)
        {
            return new RawCollectionStatus(null, null, null, null, null);
        }

        var succeeded = last.Status is "Succeeded" or "Degraded"
            ? last.FinishedAt
            : await db.CollectionRuns
                .AsNoTracking()
                .Where(r => r.SourceEntityId == sourceEntityId && (r.Status == "Succeeded" || r.Status == "Degraded"))
                .OrderByDescending(r => r.Id)
                .Select(r => r.FinishedAt)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

        return new RawCollectionStatus(
            last.Id, last.Status, Utc(last.StartedAt), last.FinishedAt is { } f ? Utc(f) : null,
            succeeded is { } s ? Utc(s) : null);
    }

    /// <summary>Згортка мапінгу словом — як у перегляді мапінгу; невідомий код — <c>null</c>.</summary>
    internal static string? Aggregation(string? transformCode)
        => Enum.TryParse<AggregationKind>(transformCode, out var kind) ? kind.ToString() : null;

    private async Task<IReadOnlyList<RawPoint>> PointsAsync(
        string sql, RawSeriesKey key, DateTime from, DateTime to, int take, CancellationToken ct)
    {
        await using var command = await CommandAsync(sql, ct).ConfigureAwait(false);
        command.Parameters.Add("@take", SqlDbType.Int).Value = take;
        AddKey(command, key.SourceEntityId, key.SourcePath, from, to);

        var result = new List<RawPoint>(Math.Min(take, 512));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new RawPoint(
                Utc(reader.GetDateTime(0)),
                reader.IsDBNull(1) ? null : SafeDecimal(reader.GetSqlDecimal(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                Utc(reader.GetDateTime(5)),
                reader.GetInt64(6)));
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
        command.CommandTimeout = CommandTimeoutSeconds;
        if (db.Database.CurrentTransaction?.GetDbTransaction() is SqlTransaction transaction)
        {
            command.Transaction = transaction;
        }

        return command;
    }

    private static void AddKey(SqlCommand command, int entity, string path, DateTime from, DateTime to)
    {
        command.Parameters.Add("@entity", SqlDbType.Int).Value = entity;

        // ⛔ Рівно тип стовпця: `nvarchar(max)` чи інша довжина дали б неявне перетворення і скан.
        command.Parameters.Add("@path", SqlDbType.NVarChar, 400).Value = path;
        command.Parameters.Add("@from", SqlDbType.DateTime2, 3).Value = from;
        command.Parameters.Add("@to", SqlDbType.DateTime2, 3).Value = to;
    }

    /// <summary>
    /// <c>AVG(decimal(34,16))</c> повертає <c>decimal(38,16)</c>, що може не вміститися в <see cref="decimal"/>
    /// .NET (28–29 значущих цифр) — тоді точність знижується, а не падає читання.
    /// </summary>
    private static decimal SafeDecimal(System.Data.SqlTypes.SqlDecimal value)
    {
        try
        {
            return value.Value;
        }
        catch (OverflowException)
        {
            return (decimal)value.ToDouble();
        }
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
