using System.Globalization;
using Ecr.Application.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ecr.Api.Health;

/// <summary>
/// Стан БД: редакція, версія, RCSI, файлові групи, запас партицій.
/// </summary>
/// <remarks>
/// **Обов'язково повідомляє поточний режим редакції і що система в ньому
/// втрачає** — наприклад «перебудова індексів потребує вікна обслуговування»
/// (АРХ-7 п. 5). Health, який каже лише «healthy», не допомагає адміністратору
/// зрозуміти, чому нічна операція поводиться інакше, ніж на тесті.
/// </remarks>
public sealed class DatabaseHealthCheck(
    ISqlCapabilities capabilities,
    Ecr.Infrastructure.Persistence.EcrDbContext db,
    Ecr.Domain.Abstractions.IClock clock) : IHealthCheck
{
    /// <summary>Скільки вільних партицій попереду вважається достатнім.</summary>
    /// <remarks>
    /// Менше двох — це Degraded, і дізнатися про це треба на старті, а не
    /// вночі, коли архівація впреться у відсутню межу.
    /// </remarks>
    private const int MinimumPartitionsAhead = 2;

    /// <summary>Файлові групи, без яких фізична модель не працює.</summary>
    private static readonly string[] RequiredFilegroups =
        ["DATA_HOT", "DATA_ARCHIVE", "AUDIT", "INDEXES"];

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["edition"] = capabilities.EditionName,
            ["effectiveMode"] = capabilities.EffectiveMode.ToString(),
            ["majorVersion"] = capabilities.ProductMajorVersion,
            ["rcsi"] = capabilities.IsReadCommittedSnapshotOn,
            ["archiveBatchSize"] = capabilities.ArchiveBatchSize,
        };

        try
        {
            var filegroups = await db.Database
                .SqlQueryRaw<string>("SELECT name AS Value FROM sys.filegroups")
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            data["filegroups"] = filegroups;

            var missing = RequiredFilegroups
                .Where(fg => !filegroups.Contains(fg, StringComparer.OrdinalIgnoreCase))
                .ToList();
            data["missingFilegroups"] = missing;

            var partitionsAhead = await PartitionsAheadAsync(cancellationToken).ConfigureAwait(false);
            data["partitionsAhead"] = partitionsAhead;
            data["limitations"] = Limitations();

            if (!capabilities.IsReadCommittedSnapshotOn)
            {
                // Не Degraded, а Unhealthy: без RCSI пік останнього дня періоду
                // впирається в блокування (D-29), і це не «трохи гірше», а
                // непрацездатність у той єдиний день, коли система потрібна.
                return HealthCheckResult.Unhealthy("RCSI вимкнено.", data: data);
            }

            if (missing.Count > 0)
            {
                return HealthCheckResult.Unhealthy(
                    "Немає файлових груп: " + string.Join(", ", missing), data: data);
            }

            if (partitionsAhead < MinimumPartitionsAhead)
            {
                return HealthCheckResult.Degraded(
                    string.Create(CultureInfo.InvariantCulture,
                        $"Запас партицій {partitionsAhead}: менше за {MinimumPartitionsAhead}."),
                    data: data);
            }

            return HealthCheckResult.Healthy("База доступна.", data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("База недоступна.", ex, data);
        }
    }

    /// <summary>Скільки меж партиціонування лежить попереду поточного періоду.</summary>
    private async Task<int> PartitionsAheadAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var currentKey = (now.Year * 100) + now.Month;

        var ahead = await db.Database
            .SqlQueryRaw<int>(
                """
                SELECT COUNT(*) AS Value
                FROM sys.partition_range_values rv
                JOIN sys.partition_functions pf ON pf.function_id = rv.function_id
                WHERE pf.name = 'pf_ByPeriodKey' AND CAST(rv.value AS int) > {0}
                """,
                currentKey)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return ahead.Count > 0 ? ahead[0] : 0;
    }

    private IReadOnlyList<string> Limitations()
        => capabilities is Ecr.Infrastructure.Startup.SqlCapabilitiesProbe probe
            ? probe.Limitations()
            : [];
}
