using Ecr.Application.Ports;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ecr.Api.Health;

/// <summary>
/// Стан зовнішніх джерел: коли востаннє був успішний збір і чи немає прогалин
/// у покритті.
/// </summary>
/// <remarks>
/// ⚠ Файл оголошений у дереві `05-skeleton.md` §1, але секції з вмістом у
/// `05h` немає (Q-015). Він потрібен для компіляції: <c>Program.cs</c>
/// реєструє його як <c>AddCheck&lt;SourcesHealthCheck&gt;("sources")</c>.
///
/// Ознака здоров'я інтеграції — <b>журнал покриття</b>, а не тиша (ІНТ-3.3):
/// система, яка «нічого не повідомляє», і система, яка «нічого не зібрала»,
/// ззовні однакові.
/// </remarks>
public sealed class SourcesHealthCheck(ICollectionStore collections) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: для кожного активного джерела — час останнього успішного прогону і " +
            "непокриті інтервали за lookback. Прогалини є, але catch-up їх планує → Degraded, " +
            "не Unhealthy: простій джерела має бути затримкою, а не втратою (ФВ-11.3). " +
            "Джерело недоступне довше за поріг → Unhealthy із кодом ECR-INT-0503.");
}
