using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ecr.Api.Health;

/// <summary>
/// Розмір <c>ext.RawDataPoint</c> проти порога перегляду R2 (≥ 20 млн рядків ≈ 8 ГБ;
/// <c>docs/build/perf/R2-rawdatapoint-plan.md</c>) для картки <c>db</c>.
/// </summary>
/// <remarks>
/// Кількість — з <c>sys.partitions</c> (наближена, без скану й блокувань), кеш 10 хв
/// на рядок підключення. Конфіг: <c>Health:RawDataPointWarnRows</c>.
/// </remarks>
public static class RawDataPointHealth
{
    /// <summary>Ключ конфігурації порога.</summary>
    public const string ThresholdConfigKey = "Health:RawDataPointWarnRows";

    /// <summary>Типовий поріг перегляду R2.</summary>
    public const long DefaultThreshold = 20_000_000;

    /// <summary>Ключ «наближається» (від 80 % порога).</summary>
    public const string ApproachingKey = "health.collection.rawPointsApproaching";

    /// <summary>Ключ «поріг перевищено» (від порога включно).</summary>
    public const string OverThresholdKey = "health.collection.rawPointsOverThreshold";

    /// <summary>Скільки живе закешоване значення.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<string, (long Rows, DateTime At)> Cache = new(StringComparer.Ordinal);

    /// <summary>Чиста функція статусу: Healthy нижче 80 % порога, інакше Degraded з ключем.</summary>
    public static (HealthStatus Status, string? MessageKey) Evaluate(long rows, long threshold)
    {
        if (threshold <= 0)
        {
            return (HealthStatus.Healthy, null);
        }

        if (rows >= threshold)
        {
            return (HealthStatus.Degraded, OverThresholdKey);
        }

        return rows * 5 >= threshold * 4
            ? (HealthStatus.Degraded, ApproachingKey)
            : (HealthStatus.Healthy, null);
    }

    /// <summary>Параметри тексту: <c>{rows}</c>, <c>{threshold}</c>.</summary>
    public static Dictionary<string, string> Parameters(long rows, long threshold) => new(2)
    {
        ["rows"] = rows.ToString("N0", CultureInfo.InvariantCulture),
        ["threshold"] = threshold.ToString("N0", CultureInfo.InvariantCulture),
    };

    /// <summary>Наближена кількість рядків (≥ 0); кеш на <see cref="CacheTtl"/>.</summary>
    public static async Task<long> ApproximateRowsAsync(
        Ecr.Infrastructure.Persistence.EcrDbContext db, DateTime utcNow, CancellationToken ct)
    {
        var cacheKey = db.Database.GetConnectionString() ?? string.Empty;
        if (Cache.TryGetValue(cacheKey, out var hit) && utcNow - hit.At < CacheTtl && utcNow >= hit.At)
        {
            return hit.Rows;
        }

        var rows = await db.Database
            .SqlQueryRaw<long>(
                """
                SELECT CAST(COALESCE(SUM(p.rows), 0) AS bigint) AS Value
                FROM sys.partitions p
                WHERE p.object_id = OBJECT_ID(N'ext.RawDataPoint') AND p.index_id IN (0, 1)
                """)
            .ToListAsync(ct).ConfigureAwait(false);

        var value = rows.Count > 0 ? Math.Max(0, rows[0]) : 0;
        Cache[cacheKey] = (value, utcNow);
        return value;
    }
}
