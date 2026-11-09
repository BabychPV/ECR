// src/Ecr.Application/Security/SessionSettingsHandlers.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;

namespace Ecr.Application.Security;

/// <summary>
/// Вихід після бездіяльності (<c>D-471</c>): скільки хвилин без дій людини живе сеанс.
/// </summary>
/// <remarks>
/// ⛔ Межі 5–480 хв, і це судження, а не смак. Нижче 5 хв — попередження за хвилину до виходу займає
/// п'яту частину вікна, і людина, що читає звіт, вилітає посеред читання; 480 хв (8 год) — робоча зміна:
/// довше вже не «бездіяльність», а забута вкладка, і понад цю межу є лише абсолютні 12 год від входу (S21).
/// Мінімум знижується до 1 хв ЛИШЕ конфігурацією одноразового стенда (<c>Auth:IdleTimeout:MinimumMinutes</c>,
/// <c>tools/e2e-stand.ps1</c>) — щоб e2e не чекав п'ять хвилин; у продукті ключа немає.
/// </remarks>
public static class SessionSettings
{
    /// <summary>Ключ у <c>sys_ecr.SystemSetting</c>.</summary>
    public const string IdleTimeoutKey = "Security.IdleTimeoutMinutes";

    /// <summary>За замовчуванням — 30 хв (слово людини 09.11: «Так, налаштування 30 хв»).</summary>
    public const int DefaultIdleTimeoutMinutes = 30;

    /// <summary>Нижня межа в продукті.</summary>
    public const int MinIdleTimeoutMinutes = 5;

    /// <summary>Верхня межа: робоча зміна.</summary>
    public const int MaxIdleTimeoutMinutes = 480;

    /// <summary>Право на перегляд і зміну: керування обліковими записами.</summary>
    public const string Permission = "Security.ManageUsers";

    /// <summary>Ключ відмови «значення поза межами».</summary>
    public const string OutOfRangeKey = "err.ECR-REQ-0422.idleTimeoutOutOfRange";

    /// <summary>Тип події журналу безпеки про зміну налаштування.</summary>
    public const string UpdatedEventType = "SessionSettingsUpdated";
}

/// <summary>Налаштування сеансу для екрана «Безпека».</summary>
/// <param name="IdleTimeoutMinutes">Вихід після стількох хвилин без дій.</param>
/// <param name="MinIdleTimeoutMinutes">Найменше допустиме значення.</param>
/// <param name="MaxIdleTimeoutMinutes">Найбільше допустиме значення.</param>
/// <param name="DefaultIdleTimeoutMinutes">Значення за замовчуванням.</param>
public sealed record SessionSettingsView(
    int IdleTimeoutMinutes, int MinIdleTimeoutMinutes, int MaxIdleTimeoutMinutes, int DefaultIdleTimeoutMinutes);

/// <summary>Запит на зміну налаштувань сеансу.</summary>
/// <param name="IdleTimeoutMinutes">Вихід після стількох хвилин без дій.</param>
public sealed record SessionSettingsInput(int IdleTimeoutMinutes);

/// <summary>Читання налаштувань сеансу. Право <c>Security.ManageUsers</c>.</summary>
public sealed class GetSessionSettingsHandler(
    IIdleTimeoutPolicy policy, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Поточне значення й межі.</summary>
    /// <param name="ct">Токен скасування.</param>
    public async Task<SessionSettingsView> HandleAsync(CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, "Security.ManageUsers", ct).ConfigureAwait(false);

        return await ViewAsync(policy, ct).ConfigureAwait(false);
    }

    internal static async Task<SessionSettingsView> ViewAsync(IIdleTimeoutPolicy policy, CancellationToken ct)
        => new(
            await policy.GetMinutesAsync(ct).ConfigureAwait(false), policy.MinimumMinutes,
            SessionSettings.MaxIdleTimeoutMinutes, SessionSettings.DefaultIdleTimeoutMinutes);
}

/// <summary>Зміна налаштувань сеансу. Право <c>Security.ManageUsers</c>.</summary>
/// <remarks>
/// ⚠ Діє без перезапуску і для ЧИННИХ сеансів: межу бездіяльності сервер звіряє на кожному запиті з
/// поточним значенням (<see cref="IIdleTimeoutPolicy"/>), а не з тим, що було на вході.
/// </remarks>
public sealed class SaveSessionSettingsHandler(
    IIdleTimeoutPolicy policy, ISessionSettingsStore store, IAccessDecisionService access, IUnitOfWork uow,
    IAuditWriter audit, ICurrentUser currentUser, IClock clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Записує значення, журналює зміну в тій самій транзакції й скидає кеш.</summary>
    /// <param name="input">Нове значення.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<SessionSettingsView> HandleAsync(SessionSettingsInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, "Security.ManageUsers", ct).ConfigureAwait(false);

        var minutes = input.IdleTimeoutMinutes;
        if (minutes < policy.MinimumMinutes || minutes > SessionSettings.MaxIdleTimeoutMinutes)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Вихід після бездіяльності: від {policy.MinimumMinutes} до {SessionSettings.MaxIdleTimeoutMinutes} хв.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = SessionSettings.OutOfRangeKey,
                    ["name"] = "idleTimeoutMinutes",
                    ["min"] = policy.MinimumMinutes,
                    ["max"] = SessionSettings.MaxIdleTimeoutMinutes,
                });
        }

        var previous = await store.ReadIdleTimeoutMinutesAsync(ct).ConfigureAwait(false);
        var now = clock.UtcNow;

        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                await store.WriteIdleTimeoutMinutesAsync(minutes, profile.UserId, now, token).ConfigureAwait(false);
                await audit.WriteSecurityEventAsync(
                    new SecurityEventRecord(
                        now, SessionSettings.UpdatedEventType, TargetUserId: null, TargetRoleId: null,
                        JsonSerializer.Serialize(
                            new
                            {
                                idleTimeoutMinutes = minutes,
                                previousIdleTimeoutMinutes = previous ?? SessionSettings.DefaultIdleTimeoutMinutes,
                            },
                            Json),
                        profile.UserId, currentUser.CorrelationId),
                    token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        policy.Invalidate();

        return await GetSessionSettingsHandler.ViewAsync(policy, ct).ConfigureAwait(false);
    }
}
