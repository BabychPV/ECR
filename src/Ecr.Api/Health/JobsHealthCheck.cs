using Ecr.Application.Common;
using Ecr.Application.Ports;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Quartz;
using Quartz.Impl.Matchers;

namespace Ecr.Api.Health;

/// <summary>
/// Стан фонових задач: чи живий планувальник і чи стоять на місці розклади.
/// </summary>
/// <remarks>
/// Задача, яка мовчки не виконується, і задача, якій нема чого робити, ззовні
/// виглядають однаково — тому перевірка дивиться на планувальник і на
/// зареєстровані тригери, а не на факт «збірка містить клас задачі».
///
/// ⛔ До ЕТАПУ 7 ця перевірка повертала <c>Degraded</c> із текстом
/// «планувальник з'явиться на Етапі 5». Етап 5 давно закритий, задач сім,
/// вони працюють — а <c>/health/ready</c> віддавав <c>Degraded</c> **завжди**.
/// Це гірше за відсутність перевірки: моніторинг, який світиться жовтим
/// роками, навчають ігнорувати, і справжню деградацію ніхто не помітить.
/// </remarks>
public sealed class JobsHealthCheck(
    ISchedulerFactory? factory,
    IUiStringCatalog catalog,
    ICurrentUser currentUser,
    IJobProgressStore? progress = null,
    Domain.Abstractions.IClock? clock = null,
    HealthResultCache? cache = null) : IHealthCheck
{
    /// <summary>
    /// Скільки задача може висіти без биття, поки прибирання мало б її закрити,
    /// перш ніж перевірка скаже «прибирання стоїть» (U16).
    /// </summary>
    /// <remarks>
    /// ⚠ Жовтий — одразу, щойно є задача без биття довше за
    /// <see cref="IJobProgressStore.StaleAfter"/>: це штатне вікно до наступного
    /// проходу прибирання (раз на хвилину), і воно має бути видимим. Після
    /// цього запасу — той самий жовтий, але з текстом <c>health.jobs.staleUnswept</c>
    /// і <c>cleanupStalled = true</c>: прибирання не працює. Червоним (503 на
    /// <c>/health/ready</c>) зависле тло не робиться свідомо — див. коментар у
    /// перевірці. Шість проходів — з запасом на збій БД.
    /// </remarks>
    public static readonly TimeSpan UnsweptAfter = IJobProgressStore.StaleAfter + TimeSpan.FromMinutes(6);

    /// <summary>
    /// Вікно лічильника задач, що вичерпали стелю відкладень (Д-2 огляду O1): доба —
    /// жовтий гасне сам наступного дня, якщо нових таких немає.
    /// </summary>
    public static readonly TimeSpan DeferralExhaustedWindow = TimeSpan.FromDays(1);

    /// <summary>Вікно лічильника перерахунків, що вийшли за бюджет ПРД-13: доба, як і вище.</summary>
    public static readonly TimeSpan RecalcOverBudgetWindow = TimeSpan.FromDays(1);

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken)
    {
        // ⛔ `/health/ready` анонімний, а перевірка робить ~254 логічних читання на пробу
        // (JobProgressStore.Count*). Результат живе недовго (HealthResultCache.Ttl), тож
        // шквал проб не б'є по базі; зміна стану не ховається довше за TTL.
        //
        // ⛔ L1-02: ключ — мова, нормалізована реєстром. Сирий `Accept-Language`
        // у ключі давав обхід кешу будь-яким унікальним заголовком.
        if (cache is null)
        {
            return await ComputeAsync(cancellationToken).ConfigureAwait(false);
        }

        var language = await catalog.ResolveLanguageAsync(currentUser.Language, cancellationToken).ConfigureAwait(false);
        return await cache.GetOrAddAsync($"ready:{language}", ComputeAsync, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HealthCheckResult> ComputeAsync(CancellationToken cancellationToken)
    {
        // ⚠ Фабрика лишається необов'язковою: у складаннях без Quartz
        // (наприклад, у тестах контейнера) перевірка має повідомити про це, а
        // не впасти винятком і не завалити весь `/health/ready` (`Q-051`).
        if (factory is null)
        {
            var notRegistered = await Text(
                "health.jobs.notRegistered", "The scheduler is not registered in the container.",
                cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Unhealthy(notRegistered);
        }

        try
        {
            var scheduler = await factory.GetScheduler(cancellationToken).ConfigureAwait(false);

            if (!scheduler.IsStarted || scheduler.IsShutdown)
            {
                var stopped = await Text(
                    "health.jobs.stopped", "The scheduler is stopped: no background job will run.",
                    cancellationToken).ConfigureAwait(false);

                // ⛔ U7: лише Degraded — той самий принцип, що й для завислих
                // задач нижче: тло не виводить інстанс із ротації. Перевірка
                // має тег `ready`, і Unhealthy дав би `/health/ready` 503 —
                // балансувальник зняв би API, який обслуговує запити, через
                // стан планувальника. Тяжкість показують текст
                // (`health.jobs.stopped`) і `schedulerStopped = true`.
                var data = Data(0, 0);
                data["schedulerStopped"] = true;
                return HealthCheckResult.Degraded(stopped, data: data);
            }

            var jobs = await scheduler
                .GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), cancellationToken)
                .ConfigureAwait(false);

            var triggers = await scheduler
                .GetTriggerKeys(GroupMatcher<TriggerKey>.AnyGroup(), cancellationToken)
                .ConfigureAwait(false);

            // ⛔ Планувальник без жодного розкладу — це саме «мовчить, бо
            // нічого не поставлено», а не «нема чого робити»: регламентні
            // задачі (стани періодів, запас партицій, пошук осиротілих рядків)
            // мають бути зареєстровані завжди.
            if (triggers.Count == 0)
            {
                var noSchedules = await Text(
                    "health.jobs.noSchedules", "The scheduler is alive, but no schedule is registered.",
                    cancellationToken).ConfigureAwait(false);
                return HealthCheckResult.Degraded(noSchedules, data: Data(jobs.Count, 0));
            }

            // ⛔ U16: «планувальник живий» ще не означає «задачі не висять».
            // Раніше перевірка була зеленою, поки в `/jobs` місяцями «виконувалися»
            // задачі, покинуті процесом, що зник.
            if (progress is not null && clock is not null)
            {
                var now = clock.UtcNow;
                var stale = await progress.SummarizeStaleAsync(now, cancellationToken).ConfigureAwait(false);

                if (stale.Count > 0)
                {
                    var data = Data(jobs.Count, triggers.Count);
                    data["staleJobs"] = stale.Count;

                    var unswept = stale.OldestHeartbeatAt is not { } oldest || now - oldest > UnsweptAfter;
                    data["cleanupStalled"] = unswept;
                    var text = await Text(
                            unswept ? "health.jobs.staleUnswept" : "health.jobs.stale",
                            unswept
                                ? "Background jobs hang without a heartbeat and the cleanup does not close them: {count}."
                                : "Background jobs without a heartbeat, awaiting cleanup: {count}.",
                            cancellationToken,
                            new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["count"] = stale.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            })
                        .ConfigureAwait(false);

                    // ⛔ Лише Degraded, навіть коли прибирання стоїть. Перевірка
                    // має тег `ready`, а Unhealthy дає `/health/ready` 503:
                    // балансувальник вивів би з ротації здоровий API через
                    // зависле ТЛО — і однаковий стан спільної бази зняв би так
                    // само всі інстанси разом (рішення координатора). Тяжкість
                    // розрізняють текст і `cleanupStalled`, а не код відповіді.
                    return HealthCheckResult.Degraded(text, data: data);
                }

                // ⛔ Д-2 огляду O1: задача, що вичерпала стелю відкладень, закривається Failed
                // тихо — рядок Error у журналі і конверт у /jobs. Без цього жовтого вічно
                // зайнятий лок документа помічали б лише за непорахованими формулами.
                var exhausted = await progress
                    .CountFailedWithMessageKeyAsync(
                        Infrastructure.Jobs.JobDeferral.ExhaustedKey, now - DeferralExhaustedWindow, cancellationToken)
                    .ConfigureAwait(false);

                if (exhausted > 0)
                {
                    var data = Data(jobs.Count, triggers.Count);
                    data["deferralExhausted"] = exhausted;
                    var text = await Text(
                            "health.jobs.deferralExhausted",
                            "Background jobs stopped on the deferral limit in the last 24 hours: {count}. See the job list for the held resource.",
                            cancellationToken,
                            new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["count"] = exhausted.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            })
                        .ConfigureAwait(false);

                    // Лише Degraded — той самий принцип, що для завислих задач вище.
                    return HealthCheckResult.Degraded(text, data: data);
                }

                // ⛔ ПРД-13 (НФ-8.6.4): перерахунок, що завершився, але вийшов за бюджет (за
                // замовчуванням 600 с), — не помилка задачі, тож у `Failed` його немає. Слід —
                // конверт у Message, який лишає `RecalculationBudgetMonitor`. Без цього бюджет
                // видно лише тому, хто дивиться на графік, а не в готовності інстанса.
                var overBudget = await progress
                    .CountSucceededWithMessageKeyAsync(
                        Infrastructure.Jobs.RecalculationBudgetMonitor.OverBudgetKey, now - RecalcOverBudgetWindow,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (overBudget > 0)
                {
                    var data = Data(jobs.Count, triggers.Count);
                    data["recalcOverBudget"] = overBudget;
                    var text = await Text(
                            "health.jobs.recalcOverBudget",
                            "Recalculation jobs that ran over the time budget in the last 24 hours: {count}. See the job list for the duration.",
                            cancellationToken,
                            new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["count"] = overBudget.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            })
                        .ConfigureAwait(false);

                    // Лише Degraded: повільний перерахунок не виводить інстанс із ротації.
                    return HealthCheckResult.Degraded(text, data: data);
                }
            }

            var running = await Text("health.jobs.running", "The scheduler is running.", cancellationToken)
                .ConfigureAwait(false);
            return HealthCheckResult.Healthy(running, data: Data(jobs.Count, triggers.Count));
        }
        catch (SchedulerException failure)
        {
            // ⚠ Виняток йде в журнал, а не в дані відповіді: його текст пише
            // писар тільки в лог, а клієнтові віддається сам факт (ФВ-6.11).
            var unavailable = await Text(
                "health.jobs.unavailable", "The scheduler is unavailable.", cancellationToken)
                .ConfigureAwait(false);
            return HealthCheckResult.Unhealthy(unavailable, failure);
        }
    }

    private Task<string> Text(
        string key, string fallback, CancellationToken ct, IReadOnlyDictionary<string, string>? parameters = null)
        => HealthCatalogText.ResolveAsync(catalog, currentUser, key, fallback, parameters, ct);

    private static Dictionary<string, object> Data(int jobs, int triggers)
        => new(StringComparer.Ordinal) { ["jobs"] = jobs, ["triggers"] = triggers };
}
