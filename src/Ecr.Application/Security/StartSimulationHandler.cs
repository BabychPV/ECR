// src/Ecr.Application/Security/StartSimulationHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Security;

/// <summary>
/// Починає сеанс «очима користувача» (ФВ-6.16a, D-96). Право
/// <c>Security.Simulate</c>.
/// </summary>
/// <remarks>
/// ⚠ Запис у <c>aud.SimulationSession</c> робиться **до** видачі профілю.
/// Інакше збій між видачею і записом лишив би сеанс перегляду чужих даних без
/// сліду — а слід тут і є суттю вимоги.
/// </remarks>
public sealed class StartSimulationHandler(
    ISimulationService simulation,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право, без якого симуляція неможлива.</summary>
    public const string Permission = "Security.Simulate";

    /// <summary>Профіль останнього відкритого сеансу.</summary>
    /// <remarks>
    /// ⛔ Тримається на обробнику, а не кладеться в кеш профілів (ФВ-6.16a
    /// п. 4). Під ключем суб'єкта він дістався б справжньому користувачеві —
    /// разом із чужими правами і прапорцем <c>IsSimulation</c>.
    /// </remarks>
    public AccessProfile? Profile { get; private set; }

    /// <summary>Відкриває сеанс і повертає його ідентифікатор.</summary>
    /// <param name="subjectUserId">Чиїми очима дивитися.</param>
    /// <param name="reason">Причина; обов'язкова.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException">Симуляція себе або без причини — <c>ECR-SIM-0422</c>.</exception>
    public async Task<long> HandleAsync(int subjectUserId, string reason, CancellationToken ct)
    {
        var actorUserId = currentUser.UserId
                          ?? throw new AccessDeniedException(
                              "ECR-AUTH-0401", "Анонімний запит не може відкривати симуляцію.");

        var actorProfile = await access.BuildProfileAsync(actorUserId, ct).ConfigureAwait(false);
        if (!actorProfile.Has(Permission))
        {
            throw new AccessDeniedException("ECR-AUTH-0403", $"Потрібне право {Permission}.");
        }

        // Симуляція себе безглузда і водночас небезпечна: вона дала б сеанс із
        // прапорцем «лише читання» і власними правами, тобто зручний спосіб
        // «випадково» опинитися в режимі, який не відповідає за дії.
        if (subjectUserId == actorUserId)
        {
            throw new BusinessRuleException(
                "ECR-SIM-0422", "Симуляція самого себе не має сенсу.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new BusinessRuleException(
                "ECR-SIM-0422", "Причина симуляції обов'язкова: без неї журнал не відповідає ні на що.");
        }

        // ⚠ Спершу ЗАПИС сеансу, потім профіль. Збій між видачею профілю і
        // записом лишив би перегляд чужих даних без сліду — а слід тут і є
        // суттю вимоги.
        StartedAt = clock.UtcNow;

        var sessionId = await simulation
            .StartAsync(actorUserId, subjectUserId, reason, ct).ConfigureAwait(false);

        Profile = await simulation.BuildProfileAsync(sessionId, ct).ConfigureAwait(false);

        return sessionId;
    }

    /// <summary>Момент відкриття останнього сеансу.</summary>
    /// <remarks>Клієнт зобов'язаний показувати банер увесь сеанс.</remarks>
    public DateTime StartedAt { get; private set; }
}
