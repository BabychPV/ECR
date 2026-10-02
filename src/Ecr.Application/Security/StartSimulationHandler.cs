// src/Ecr.Application/Security/StartSimulationHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;

namespace Ecr.Application.Security;

/// <summary>
/// Починає сеанс «очима користувача» (ФВ-6.16a, D-96). Право
/// <c>Security.Simulate</c>.
/// </summary>
/// <remarks>
/// ⚠ Запис у <c>aud.SimulationSession</c> робиться **до** видачі профілю.
/// Інакше збій між видачею і записом лишив би сеанс перегляду чужих даних без
/// сліду — а слід тут і є суттю вимоги.
///
/// ⛔ D-210: ціль не може бути bootstrap-адміністратором і не може мати жодного
/// небезпечного права (<c>sec.Permission.IsDangerous</c>) — інакше сеанс показав
/// би актору права, яких у нього немає (стеля «View as»).
/// </remarks>
public sealed class StartSimulationHandler(
    ISimulationService simulation,
    IAccessDecisionService access,
    IUserStore users,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право, без якого симуляція неможлива.</summary>
    public const string Permission = "Security.Simulate";

    /// <summary>Подія-спроба в <c>aud.SecurityEvent</c>: ціль заборонена (D-210).</summary>
    public const string DeniedEvent = "SimulationDenied";

    /// <summary>Відкриває сеанс і повертає його ідентифікатор.</summary>
    /// <param name="subjectUserId">Чиїми очима дивитися.</param>
    /// <param name="reason">Причина; обов'язкова.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException">
    /// Симуляція себе або без причини — <c>ECR-SIM-0422</c>; ціль — bootstrap або
    /// власник небезпечного права — <c>ECR-SIM-4031</c> (403, D-210).
    /// </exception>
    public async Task<long> HandleAsync(int subjectUserId, string reason, CancellationToken ct)
    {
        var actorUserId = currentUser.UserId
                          ?? throw new AccessDeniedException(
                              "ECR-AUTH-0401", "Анонімний запит не може відкривати симуляцію.",
                              new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        var actorProfile = await access.BuildProfileAsync(actorUserId, ct).ConfigureAwait(false);
        if (!PermissionCheck.IsGranted(actorProfile, Permission))
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {Permission}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.permission",
                    ["permission"] = Permission,
                });
        }

        // Симуляція себе безглузда і водночас небезпечна: вона дала б сеанс із
        // прапорцем «лише читання» і власними правами, тобто зручний спосіб
        // «випадково» опинитися в режимі, який не відповідає за дії.
        if (subjectUserId == actorUserId)
        {
            throw new BusinessRuleException(
                "ECR-SIM-0422", "Симуляція самого себе не має сенсу.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-SIM-0422.selfSimulation" });
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new BusinessRuleException(
                "ECR-SIM-0422", "Причина симуляції обов'язкова: без неї журнал не відповідає ні на що.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-SIM-0422.reasonRequired" });
        }

        await EnsureTargetAllowedAsync(actorUserId, subjectUserId, ct).ConfigureAwait(false);

        // ⚠ Спершу ЗАПИС сеансу, потім профіль. Збій між видачею профілю і
        // записом лишив би перегляд чужих даних без сліду — а слід тут і є
        // суттю вимоги.
        StartedAt = clock.UtcNow;

        var sessionId = await simulation
            .StartAsync(actorUserId, subjectUserId, reason, ct).ConfigureAwait(false);

        // ⚠ Профіль тут НЕ видається і ніде не тримається: профіль сеансу
        // будує на КОЖЕН запит SimulationAwareAccessDecisionService (V-06).
        // Виклик лишається заради побічної перевірки — вимкнений чи відсутній
        // суб'єкт дає ECR-AUTH-0401 одразу, а не на наступному запиті.
        _ = await simulation.BuildProfileAsync(sessionId, ct).ConfigureAwait(false);

        return sessionId;
    }

    /// <summary>Стеля «View as» (D-210): ціль без bootstrap і без небезпечних прав.</summary>
    /// <remarks>
    /// ⛔ Права цілі — зі сховища, а не з <see cref="IAccessDecisionService.BuildProfileAsync"/>:
    /// профіль викидає глобальні права ролі з областю (ФВ-6.14), а область
    /// небезпечності не знімає. І ще — профіль цілі тут не будується, щоб не гріти
    /// кеш профілів чужим записом.
    ///
    /// ⚠ Подія-СПРОБА (C4) пишеться незалежно від транзакції; у деталях — лише
    /// причина, без переліку прав цілі: журнал не має ставати довідником про те,
    /// хто в системі має небезпечні права.
    /// </remarks>
    private async Task EnsureTargetAllowedAsync(int actorUserId, int subjectUserId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var target = await users
            .GetSimulationTargetPrivilegesAsync(subjectUserId, now, ct)
            .ConfigureAwait(false);

        var reason = target.DenyReason;
        if (reason is null)
        {
            return;
        }

        var (message, messageKey) = reason == "bootstrapTarget"
            ? ("Дивитися очима запису первинного налаштування заборонено (D-210).",
               "err.ECR-SIM-4031.bootstrapTarget")
            : ("Дивитися очима власника небезпечних прав заборонено (D-210).",
               "err.ECR-SIM-4031.dangerousTarget");

        await audit.WriteIndependentSecurityEventAsync(
            new SecurityEventRecord(
                now,
                DeniedEvent,
                TargetUserId: subjectUserId,
                TargetRoleId: null,
                DetailsJson: $$"""{"reason":"{{reason}}"}""",
                ChangedByUserId: actorUserId,
                CorrelationId: currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        throw new BusinessRuleException(
            ErrorCodes.SimulationTargetForbidden,
            message,
            new Dictionary<string, object?> { ["messageKey"] = messageKey });
    }

    /// <summary>Момент відкриття останнього сеансу.</summary>
    /// <remarks>Клієнт зобов'язаний показувати банер увесь сеанс.</remarks>
    public DateTime StartedAt { get; private set; }
}
