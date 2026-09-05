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
public sealed class JobsHealthCheck(ISchedulerFactory? factory) : IHealthCheck
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
            return HealthCheckResult.Unhealthy("Планувальник не зареєстрований у контейнері.");
        }

        try
        {
            var scheduler = await factory.GetScheduler(cancellationToken).ConfigureAwait(false);

            if (!scheduler.IsStarted || scheduler.IsShutdown)
            {
                return HealthCheckResult.Unhealthy(
                    "Планувальник зупинений: жодна фонова задача не виконається.",
                    data: Data(0, 0));
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
                return HealthCheckResult.Degraded(
                    "Планувальник живий, але жодного розкладу не зареєстровано.",
                    data: Data(jobs.Count, 0));
            }

            return HealthCheckResult.Healthy(
                "Планувальник працює.",
                data: Data(jobs.Count, triggers.Count));
        }
        catch (SchedulerException failure)
        {
            // ⚠ Виняток йде в журнал, а не в дані відповіді: його текст пише
            // писар тільки в лог, а клієнтові віддається сам факт (ФВ-6.11).
            return HealthCheckResult.Unhealthy("Планувальник недоступний.", failure);
        }
    }

    private static Dictionary<string, object> Data(int jobs, int triggers)
        => new(StringComparer.Ordinal) { ["jobs"] = jobs, ["triggers"] = triggers };
}
