// src/Ecr.Application/Ports/ICorrelationIdAccessor.cs

namespace Ecr.Application.Ports;

/// <summary>
/// Ідентифікатор кореляції поточного запиту — той, що пишеться в лог і
/// повертається в <c>X-Correlation-Id</c> (BE-08).
/// </summary>
/// <remarks>
/// ⚠ Окремий порт, а не <c>ICurrentUser.CorrelationId</c>: той кидає поза
/// запитом, а планувальник ставить задачі і з фонових задач (збір →
/// перенесення), де запиту немає. Тут це законний стан — <c>null</c>.
/// </remarks>
public interface ICorrelationIdAccessor
{
    /// <summary>Кореляція запиту; <c>null</c> — виклик поза HTTP-запитом.</summary>
    public string? CorrelationId { get; }
}
