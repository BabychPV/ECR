using Ecr.Application.Ports;
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
public sealed class DatabaseHealthCheck(ISqlCapabilities capabilities, Ecr.Infrastructure.Persistence.EcrDbContext db)
    : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перевірити з'єднання; зібрати data:\n" +
            "  edition, engineEdition, majorVersion, effectiveMode, rcsi,\n" +
            "  filegroups (наявність DATA_HOT/DATA_ARCHIVE/AUDIT/INDEXES),\n" +
            "  partitionsAhead (скільки вільних партицій попереду),\n" +
            "  limitations — перелік того, що недоступне в поточному режимі.\n" +
            "RCSI вимкнено → Unhealthy; partitionsAhead < 2 → Degraded.");
}
