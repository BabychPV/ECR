// src/Ecr.Application/Security/EndSimulationHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Security;

/// <summary>Завершує сеанс симуляції.</summary>
public sealed class EndSimulationHandler(
    ISimulationService simulation, ICurrentUser currentUser, IClock clock)
{
    /// <summary>Завершує **власний** сеанс.</summary>
    /// <param name="sessionId">Сеанс.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="AccessDeniedException">Чужий сеанс — <c>ECR-AUTH-0403</c>.</exception>
    public async Task HandleAsync(long sessionId, CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Анонімний запит не може завершувати симуляцію.");

        var actor = await simulation.GetActorAsync(sessionId, ct).ConfigureAwait(false);
        if (actor is null)
        {
            throw new NotFoundException("ECR-AUTH-0403", "Активного сеансу симуляції не знайдено.");
        }

        // ⚠ Завершити можна ЛИШЕ власний сеанс. Інакше один адміністратор
        // обриває чужий і псує його аудит: у журналі лишається сеанс, який
        // закрив не той, хто відкривав.
        if (actor != userId)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", "Завершити можна лише власний сеанс симуляції.");
        }

        // Проставляється EndedAt; запис не видаляється (D-25). Повернення до
        // власного профілю окремої дії не потребує: профіль симуляції в кеші й
        // не лежав (ФВ-6.16a п. 4).
        EndedAt = clock.UtcNow;
        await simulation.EndAsync(sessionId, ct).ConfigureAwait(false);
    }

    /// <summary>Момент завершення останнього сеансу.</summary>
    public DateTime EndedAt { get; private set; }
}
