using System.Globalization;
using Ecr.Application.Common;
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
    Ecr.Domain.Abstractions.IClock clock,
    IUiStringCatalog catalog,
    ICurrentUser currentUser,
    DataProtectionKeyProtection keyProtection) : IHealthCheck
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
            data["limitations"] = await LimitationsAsync(cancellationToken).ConfigureAwait(false);

            if (!capabilities.IsReadCommittedSnapshotOn)
            {
                // Не Degraded, а Unhealthy: без RCSI пік останнього дня періоду
                // впирається в блокування (D-29), і це не «трохи гірше», а
                // непрацездатність у той єдиний день, коли система потрібна.
                var message = await Text(
                    "health.db.rcsiDisabled", "RCSI is disabled.", null, cancellationToken)
                    .ConfigureAwait(false);
                return HealthCheckResult.Unhealthy(message, data: data);
            }

            if (missing.Count > 0)
            {
                var message = await Text(
                    "health.db.missingFilegroups", "Missing filegroups: {names}.",
                    Param("names", string.Join(", ", missing)), cancellationToken)
                    .ConfigureAwait(false);
                return HealthCheckResult.Unhealthy(message, data: data);
            }

            if (partitionsAhead < MinimumPartitionsAhead)
            {
                var message = await Text(
                    "health.db.partitionsLow", "Partitions ahead: {count} — below the minimum of {minimum}.",
                    Params(
                        ("count", partitionsAhead.ToString(CultureInfo.InvariantCulture)),
                        ("minimum", MinimumPartitionsAhead.ToString(CultureInfo.InvariantCulture))),
                    cancellationToken)
                    .ConfigureAwait(false);
                return HealthCheckResult.Degraded(message, data: data);
            }

            var available = await Text("health.db.available", "Database is available.", null, cancellationToken)
                .ConfigureAwait(false);
            return HealthCheckResult.Healthy(available, data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = await Text(
                "health.db.unavailable", "Database is unavailable.", null, cancellationToken)
                .ConfigureAwait(false);
            return HealthCheckResult.Unhealthy(message, ex, data);
        }
    }

    /// <summary>Обгортка над <see cref="HealthCatalogText.ResolveAsync"/> із власними каталогом/користувачем.</summary>
    private Task<string> Text(
        string key, string fallback, IReadOnlyDictionary<string, string>? parameters, CancellationToken ct)
        => HealthCatalogText.ResolveAsync(catalog, currentUser, key, fallback, parameters, ct);

    private static Dictionary<string, string> Param(string name, string value) => new(1) { [name] = value };

    private static Dictionary<string, string> Params(params (string Name, string Value)[] pairs)
    {
        var result = new Dictionary<string, string>(pairs.Length);
        foreach (var (name, value) in pairs)
        {
            result[name] = value;
        }

        return result;
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

    /// <summary>
    /// Що недоступне в поточному режимі — для показу в `/health/db`
    /// (`Q-304`).
    /// </summary>
    /// <remarks>
    /// ⛔ Раніше йшло через `probe.Limitations()` — рядковий метод самого
    /// `SqlCapabilitiesProbe` (`Ecr.Infrastructure`), готовий український
    /// текст призначений для ЛОГУ (`StartupSequence.cs`, інший, легітимний
    /// українськомовний споживач — залишений без змін). Той самий готовий
    /// рядок ішов і сюди, у клієнтську відповідь — звідси й був сирий
    /// український текст незалежно від мови інтерфейсу. `capabilities`
    /// (`ISqlCapabilities`) уже несе прапорці напряму
    /// (`SupportsOnlineIndexRebuild`, `SupportsResourceGovernor`,
    /// `IsReadCommittedSnapshotOn`, `ArchiveBatchSize`) — рантайм-перевірка
    /// типу (`is SqlCapabilitiesProbe`) взагалі не потрібна.
    /// </remarks>
    private async Task<IReadOnlyList<string>> LimitationsAsync(CancellationToken ct)
    {
        var list = new List<string>();

        if (!capabilities.SupportsOnlineIndexRebuild)
        {
            list.Add(await Text(
                "health.db.limitation.onlineIndexRebuild",
                "Online index rebuild requires a maintenance window (ONLINE = ON is not available).",
                null, ct).ConfigureAwait(false));
        }

        if (!capabilities.SupportsResourceGovernor)
        {
            list.Add(await Text(
                "health.db.limitation.resourceGovernor",
                "Background jobs are not isolated from the interactive peak (no Resource Governor).",
                null, ct).ConfigureAwait(false));
        }

        if (!capabilities.IsReadCommittedSnapshotOn)
        {
            list.Add(await Text(
                "health.db.limitation.rcsi",
                "RCSI is disabled: reads will block writes during the peak of the last day of the period.",
                null, ct).ConfigureAwait(false));
        }

        // ⛔ `MI-01`/`D14-08`: тимчасове рішення не має права стати невидимим
        // постійним. Ключі, якими підписана сесія, лежать у `sec.DataProtectionKey`
        // відкрито доти, доки немає сертифіката, і єдиний захист від їх читання —
        // права в базі. Про це адміністратор має дізнатися з health, а не з
        // аудиту.
        if (!keyProtection.IsProtected)
        {
            list.Add(await Text(
                "health.db.limitation.dataProtectionKeys",
                "Session keys are stored unencrypted in sec.DataProtectionKey: no certificate is configured "
                + "(Auth:DataProtection:CertificateThumbprint). Restrict the table to the service account with DENY for everyone else.",
                null, ct).ConfigureAwait(false));
        }

        list.Add(await Text(
            "health.db.limitation.archiveBatchSize",
            "Archive batch size: {size}.",
            Param("size", capabilities.ArchiveBatchSize.ToString(CultureInfo.InvariantCulture)),
            ct).ConfigureAwait(false));

        return list;
    }
}
