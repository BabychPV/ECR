using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ecr.Api.Health;

/// <summary>
/// Перевірка <c>worker</c>: чи має перерахунок виконавця, коли Api віддав його
/// пулу <c>EcrWorker</c> (<c>Jobs:Recalculation:Executor = Worker</c>, I2-2).
/// </summary>
/// <remarks>
/// ⛔ За <c>Worker</c> Api лейн перерахунку НЕ бере (<see cref="JobLaneMap.ApiLanes"/>):
/// без живої служби задачі стоять <c>Queued</c> вічно, а ззовні це «дуже довго
/// рахує». Режим пише <c>deploy-ecr.ps1</c> лише разом зі службою, але службу
/// можна вимкнути чи зняти пізніше (runbook §10) — тому перевірка дивиться на
/// ФАКТИ, а не на конфіг:
/// <list type="number">
/// <item>служби <c>EcrWorker</c> на цьому сервері немає або її тип запуску
/// <c>Disabled</c> — жовтий одразу, ще до першої задачі;</item>
/// <item>у лейні перерахунку є задачі, що чекають довше за <see cref="StallAfter"/>,
/// і жодна задача цього лейну не тримає живої оренди — жовтий: служба є, але
/// не працює (зупинена, падає, немає рядка підключення).</item>
/// </list>
/// ⚠ Чому не «биття рядків <c>wrk</c>»: дочірній воркер пише в <c>itg.JobProgress</c>
/// лише під час задачі. Простій без жодної задачі й мертва служба в базі
/// однакові, тож така перевірка жовтіла б на кожному тихому сервері — моніторинг,
/// що світиться жовтим завжди, навчають ігнорувати (див. <see cref="JobsHealthCheck"/>).
/// Застій черги на простої не спрацьовує, а коли робота є — спрацьовує завжди.
/// <para>
/// ⛔ Лише <c>Degraded</c>, ніколи <c>Unhealthy</c>: перевірка має тег <c>ready</c>,
/// і 503 вивів би з балансувальника Api, який обслуговує введення даних, через
/// стан фонового перерахунку — той самий принцип, що в <see cref="JobsHealthCheck"/>.
/// </para>
/// </remarks>
public sealed class RecalculationWorkerHealthCheck(
    IConfiguration configuration,
    IRecalculationWorkerProbe probe,
    IUiStringCatalog catalog,
    ICurrentUser currentUser) : IHealthCheck
{
    /// <summary>Скільки задача перерахунку може чекати, перш ніж черга вважається застряглою.</summary>
    /// <remarks>
    /// Воркер бере задачу за секунди; п'ять хвилин — із запасом на перезапуск
    /// служби (<c>DelayedAutoStart</c>) і відступ наглядача після збою дочірнього.
    /// </remarks>
    public static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        var mode = DbBackgroundJobScheduler.ReadMode(configuration);
        var executor = JobLaneMap.ReadExecutor(configuration);
        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["queueMode"] = mode.ToString(),
            ["executor"] = executor.ToString(),
        };

        // У режимі Quartz Executor нічого не змінює: перерахунок іде в Api.
        if (mode != JobQueueMode.Database || executor != RecalculationExecutor.Worker)
        {
            var inProcess = await Text(
                "health.worker.inProcess", "Recalculation runs in the application process.", cancellationToken)
                .ConfigureAwait(false);
            return HealthCheckResult.Healthy(inProcess, data);
        }

        var service = probe.ReadServiceState();
        data["workerService"] = service.ToString();

        if (service == WorkerServiceState.Missing)
        {
            var missing = await Text(
                "health.worker.serviceMissing",
                "Recalculation is assigned to the EcrWorker service (Jobs:Recalculation:Executor = Worker), "
                + "but the service is not installed on this server: recalculation jobs will wait with no one to run them. "
                + "Install it (WORKER_ENABLED=1) or switch the executor to InProcess.",
                cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Degraded(missing, data: data);
        }

        if (service == WorkerServiceState.Disabled)
        {
            var disabled = await Text(
                "health.worker.serviceDisabled",
                "Recalculation is assigned to the EcrWorker service (Jobs:Recalculation:Executor = Worker), "
                + "but the service is disabled: recalculation jobs will wait with no one to run them. "
                + "Enable the service or switch the executor to InProcess.",
                cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Degraded(disabled, data: data);
        }

        RecalculationQueueState queue;
        try
        {
            queue = await probe.ReadQueueAsync(StallAfter, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Збій читання — жовтий із причиною в журналі, а не виняток: інакше 503 (див. ⛔ вище).
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            var unavailable = await Text(
                "health.worker.queueUnavailable", "The recalculation queue could not be read.", cancellationToken)
                .ConfigureAwait(false);
            return HealthCheckResult.Degraded(unavailable, ex, data);
        }

        data["stalledJobs"] = queue.Stalled;
        data["liveLeases"] = queue.LiveLeases;

        if (queue.Stalled > 0 && queue.LiveLeases == 0)
        {
            var stalled = await Text(
                    "health.worker.stalled",
                    "No recalculation worker is taking jobs: {count} job(s) have been waiting longer than {minutes} min. "
                    + "Check the EcrWorker service.",
                    cancellationToken,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["count"] = queue.Stalled.ToString(CultureInfo.InvariantCulture),
                        ["minutes"] = ((int)StallAfter.TotalMinutes).ToString(CultureInfo.InvariantCulture),
                    })
                .ConfigureAwait(false);
            return HealthCheckResult.Degraded(stalled, data: data);
        }

        var running = await Text(
            "health.worker.running", "Recalculation runs in the EcrWorker pool.", cancellationToken)
            .ConfigureAwait(false);
        return HealthCheckResult.Healthy(running, data);
    }

    private Task<string> Text(
        string key, string fallback, CancellationToken ct, IReadOnlyDictionary<string, string>? parameters = null)
        => HealthCatalogText.ResolveAsync(catalog, currentUser, key, fallback, parameters, ct);
}
