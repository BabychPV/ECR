using Ecr.Application.Ports;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ecr.Api.Health;

/// <summary>
/// Стан фонових задач: чи живий планувальник і чи не накопичилася черга.
/// </summary>
/// <remarks>
/// ⚠ Файл оголошений у дереві `05-skeleton.md` §1, але секції з вмістом у
/// `05h` немає (Q-015). Він потрібен для компіляції: <c>Program.cs</c>
/// реєструє його як <c>AddCheck&lt;JobsHealthCheck&gt;("jobs")</c>.
///
/// Задача, яка мовчки не виконується, і задача, якій нема чого робити, ззовні
/// виглядають однаково — тому health має розрізняти їх за віком найстарішої
/// задачі в черзі, а не за фактом «планувальник запущений».
/// </remarks>
public sealed class JobsHealthCheck(IBackgroundJobScheduler scheduler) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: повідомити провайдера (Quartz), кількість воркерів, довжину черги і вік " +
            "найстарішої задачі; задачі, що впали за останню добу, — окремим списком. " +
            "Планувальник не запущений → Unhealthy; черга не рухається довше за поріг → Degraded.");
}
