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
    DataProtectionKeyProtection keyProtection,
    IHostEnvironment? environment = null,
    Microsoft.Extensions.Configuration.IConfiguration? configuration = null) : IHealthCheck
{
    /// <summary>Скільки вільних партицій попереду вважається достатнім.</summary>
    /// <remarks>
    /// Менше двох — це Degraded, і дізнатися про це треба на старті, а не
    /// вночі, коли архівація впреться у відсутню межу.
    /// </remarks>
    private const int MinimumPartitionsAhead = 2;

    /// <summary>Запасний текст ключа <c>health.db.limitation.dataProtectionKeys</c>.</summary>
    private const string UnprotectedKeysFallback =
        "Session keys are stored unencrypted in sec.DataProtectionKey: no certificate is configured "
        + "(Auth:DataProtection:CertificateThumbprint). Restrict the table to the service account with DENY for everyone else.";

    /// <summary>Скрипт, що вмикає RCSI (розгортання виконує його ДО старту застосунку).</summary>
    internal const string RcsiScript = "Sql/06-rcsi.sql";

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

            // ⛔ U18: RCSI — ЖИВИМ запитом, а не з проби старту. Проба читає його
            // раз на процес, тож після `06-rcsi.sql` (runbook §5) перевірка
            // лишалася червоною до перезапуску служби, а RCSI, вимкнений на ходу,
            // показувала зеленим. Запит — один рядок `sys.databases`, дешевший за
            // `sys.filegroups` вище.
            var rcsi = await ReadCommittedSnapshotOnAsync(cancellationToken).ConfigureAwait(false);
            data["rcsi"] = rcsi;
            data["limitations"] = await LimitationsAsync(rcsi, cancellationToken).ConfigureAwait(false);

            if (!rcsi)
            {
                // Не Degraded, а Unhealthy: без RCSI пік останнього дня періоду
                // впирається в блокування (D-29), і це не «трохи гірше», а
                // непрацездатність у той єдиний день, коли система потрібна.
                // D-102/ФВ-7.9: адміністратор має одразу бачити, ЩО виконати. Посилання
                // на скрипт — не переклад, тому дописується в коді до тексту з каталогу.
                var message = await Text(
                    "health.db.rcsiDisabled", "RCSI is disabled.", null, cancellationToken)
                    .ConfigureAwait(false);
                data[PublicHealthReason.DataKey] = PublicHealthReason.RcsiDisabled;
                return HealthCheckResult.Unhealthy($"{message} [{RcsiScript}]", data: data);
            }

            if (missing.Count > 0)
            {
                var message = await Text(
                    "health.db.missingFilegroups", "Missing filegroups: {names}.",
                    Param("names", string.Join(", ", missing)), cancellationToken)
                    .ConfigureAwait(false);
                data[PublicHealthReason.DataKey] = PublicHealthReason.FilegroupsMissing;
                return HealthCheckResult.Unhealthy(message, data: data);
            }

            // ⛔ S11: у Production незахищене кільце стартує лише з явною згодою
            // `Auth:DataProtection:AllowUnprotectedKeys` (одноразові стенди, старт
            // пише Critical). Degraded, а не Unhealthy (рішення координатора):
            // стенд лишається робочим, але стан не зелений і причина названа.
            // Після Unhealthy-перевірок — вони важливіші за цей стан. Текст — той
            // самий ключ каталогу, що й обмеження: факт той самий.
            if (!keyProtection.IsProtected && environment?.IsProduction() == true)
            {
                var message = await Text(
                    "health.db.limitation.dataProtectionKeys", UnprotectedKeysFallback, null, cancellationToken)
                    .ConfigureAwait(false);
                data[PublicHealthReason.DataKey] = PublicHealthReason.SessionKeysUnprotected;
                return HealthCheckResult.Degraded(message, data: data);
            }

            // D-267: ключі кільця, зашифровані сертифікатом, якого служба не може
            // використати (не поточний і не з PreviousCertificateThumbprints), —
            // нечитабельні безповоротно, а відмова тиха. Лише відбитки, без секретів.
            if (keyProtection.IsProtected)
            {
                var xml = await db.DataProtectionKeys.AsNoTracking()
                    .Select(k => k.Xml).ToListAsync(cancellationToken).ConfigureAwait(false);
                var readable = new HashSet<string>(
                    keyProtection.ReadableThumbprints ?? [], StringComparer.OrdinalIgnoreCase);
                if (keyProtection.CertificateThumbprint is { } own)
                {
                    readable.Add(own);
                }

                var unreadable = DataProtectionKeyProtection.ThumbprintsInKeyRing(xml.OfType<string>())
                    .Where(t => !readable.Contains(t)).Order(StringComparer.Ordinal).ToList();
                data["unreadableKeyCertificates"] = unreadable;
                if (unreadable.Count > 0)
                {
                    data[PublicHealthReason.DataKey] = PublicHealthReason.KeyCertificateUnavailable;
                    return HealthCheckResult.Degraded(
                        "Data Protection key ring holds keys encrypted with certificate(s) that are not available to the service: "
                        + string.Join(", ", unreadable)
                        + ". Install them in LocalMachine\\My and list the thumbprints in "
                        + "Auth:DataProtection:PreviousCertificateThumbprints, otherwise sessions and channel secrets protected by them cannot be read.",
                        data: data);
                }
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
                data[PublicHealthReason.DataKey] = PublicHealthReason.PartitionsLow;
                return HealthCheckResult.Degraded(message, data: data);
            }

            // R2-rescope: розмір ext.RawDataPoint проти порога перегляду (sys.partitions, кеш 10 хв).
            // Збій самої оцінки не робить БД Unhealthy — це довідковий показник.
            var threshold = configuration?.GetValue<long?>(RawDataPointHealth.ThresholdConfigKey)
                ?? RawDataPointHealth.DefaultThreshold;
            long? rawRows = null;
            try
            {
                rawRows = await RawDataPointHealth.ApproximateRowsAsync(db, clock.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
            }
#pragma warning disable CA1031 // довідковий показник: збій не псує перевірку БД
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                data["rawDataPointRowsError"] = ex.GetType().Name;
            }

            if (rawRows is { } rows)
            {
                data["rawDataPointRows"] = rows;
                data["rawDataPointWarnRows"] = threshold;
                var (rawStatus, rawKey) = RawDataPointHealth.Evaluate(rows, threshold);
                if (rawKey is not null)
                {
                    var message = await Text(
                        rawKey,
                        rawKey == RawDataPointHealth.OverThresholdKey
                            ? "ext.RawDataPoint holds about {rows} rows: the R2 review threshold of {threshold} is exceeded."
                            : "ext.RawDataPoint holds about {rows} rows and is approaching the R2 review threshold of {threshold}.",
                        RawDataPointHealth.Parameters(rows, threshold), cancellationToken)
                        .ConfigureAwait(false);
                    if (rawStatus == HealthStatus.Degraded)
                    {
                        data[PublicHealthReason.DataKey] = PublicHealthReason.RawDataVolume;
                    }

                    return rawStatus == HealthStatus.Degraded
                        ? HealthCheckResult.Degraded(message, data: data)
                        : HealthCheckResult.Healthy(message, data);
                }
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
            data[PublicHealthReason.DataKey] = PublicHealthReason.DatabaseUnavailable;
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

    /// <summary>Чи увімкнено RCSI для поточної бази — зараз, а не на старті процесу.</summary>
    /// <remarks>
    /// ⚠ З <c>sys.databases</c>, як і проба старту (<c>SqlCapabilitiesProbe</c>, <c>Q-052</c>):
    /// <c>DATABASEPROPERTYEX(…, 'IsReadCommittedSnapshotOn')</c> такої властивості не має.
    /// </remarks>
    private async Task<bool> ReadCommittedSnapshotOnAsync(CancellationToken cancellationToken)
    {
        var flags = await db.Database
            .SqlQueryRaw<int>(
                "SELECT CAST(is_read_committed_snapshot_on AS int) AS Value FROM sys.databases WHERE name = DB_NAME()")
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return flags.Count > 0 && flags[0] == 1;
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
    private async Task<IReadOnlyList<string>> LimitationsAsync(bool rcsi, CancellationToken ct)
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

        if (!rcsi)
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
                "health.db.limitation.dataProtectionKeys", UnprotectedKeysFallback, null, ct).ConfigureAwait(false));
        }

        list.Add(await Text(
            "health.db.limitation.archiveBatchSize",
            "Archive batch size: {size}.",
            Param("size", capabilities.ArchiveBatchSize.ToString(CultureInfo.InvariantCulture)),
            ct).ConfigureAwait(false));

        return list;
    }
}
