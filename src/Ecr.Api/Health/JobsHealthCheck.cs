using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ecr.Api.Health;

/// <summary>
/// Стан фонових задач: чи живий планувальник і чи не накопичилася черга.
/// </summary>
/// <remarks>
/// ⚠ Файл оголошений у дереві `05-skeleton.md` §1, але секції з вмістом у
/// `05h` немає (Q-015).
///
/// Задача, яка мовчки не виконується, і задача, якій нема чого робити, ззовні
/// виглядають однаково — тому health має розрізняти їх за віком найстарішої
/// задачі в черзі, а не за фактом «планувальник запущений».
/// </remarks>
public sealed class JobsHealthCheck : IHealthCheck
{
    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Залежність від <c>IBackgroundJobScheduler</c> прибрана з
    /// конструктора: реалізації порту немає до Етапу 5, а health-перевірка,
    /// яку неможливо створити, валить увесь <c>/health/ready</c> винятком
    /// контейнера — і замість «планувальника ще немає» адміністратор бачить
    /// 500 без пояснень (`Q-051`).
    ///
    /// Повертає <c>Degraded</c>, а не <c>Healthy</c>: сказати «healthy» про
    /// підсистему, якої немає, гірше — саме так з'являються моніторинги, що
    /// мовчать роками.
    /// </remarks>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
        => Task.FromResult(HealthCheckResult.Degraded(
            "Планувальник фонових задач з'явиться на Етапі 5.",
            data: new Dictionary<string, object>(StringComparer.Ordinal) { ["stage"] = 5 }));
}
