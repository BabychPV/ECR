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
    ISchedulerFactory? factory, IUiStringCatalog catalog, ICurrentUser currentUser) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken)
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
                return HealthCheckResult.Unhealthy(stopped, data: Data(0, 0));
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

    private Task<string> Text(string key, string fallback, CancellationToken ct)
        => HealthCatalogText.ResolveAsync(catalog, currentUser, key, fallback, null, ct);

    private static Dictionary<string, object> Data(int jobs, int triggers)
        => new(StringComparer.Ordinal) { ["jobs"] = jobs, ["triggers"] = triggers };
}
