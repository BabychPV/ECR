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

    /// <summary>
    /// Ключ у <c>sys_ecr.SystemSetting</c>: строк зберігання <c>sec.LoginAttempt</c> у днях (PSS-24, рішення людини 20.11 п. 7).
    /// </summary>
    public const string LoginAttemptRetentionKey = "Security.LoginAttemptRetentionDays";

    /// <summary>За замовчуванням — 400 днів (слово людини 20.11).</summary>
    public const int DefaultLoginAttemptRetentionDays = 400;

    /// <summary>Підлога: жодне налаштування не стирає спроби молодші за 30 днів.</summary>
    public const int MinLoginAttemptRetentionDays = 30;

    /// <summary>Стеля: 10 років.</summary>
    public const int MaxLoginAttemptRetentionDays = 3650;

    /// <summary>Ключ відмови «строк зберігання спроб входу поза межами».</summary>
    public const string RetentionOutOfRangeKey = "err.ECR-REQ-0422.loginRetentionOutOfRange";

    /// <summary>
    /// Чинний строк зберігання спроб входу: збережене значення в межах 30–3650, інакше (немає рядка, не число) — 400.
    /// </summary>
    /// <remarks>
    /// ⛔ Рядок, змінений руками в обхід екрана (0, −5, 99999), не вимикає прибирання й не стирає свіжі спроби: він
    /// обмежується межами, а не відкидається.
    /// </remarks>
    /// <param name="stored">Значення з <c>SystemSetting</c>; <c>null</c> — рядка немає.</param>
    public static int ResolveLoginAttemptRetentionDays(string? stored)
        => int.TryParse(stored, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var days)
            ? Math.Clamp(days, MinLoginAttemptRetentionDays, MaxLoginAttemptRetentionDays)
            : DefaultLoginAttemptRetentionDays;
}

/// <summary>Налаштування сеансу для екрана «Безпека».</summary>
/// <param name="IdleTimeoutMinutes">Вихід після стількох хвилин без дій.</param>
/// <param name="MinIdleTimeoutMinutes">Найменше допустиме значення.</param>
/// <param name="MaxIdleTimeoutMinutes">Найбільше допустиме значення.</param>
/// <param name="DefaultIdleTimeoutMinutes">Значення за замовчуванням.</param>
/// <param name="LoginAttemptRetentionDays">Скільки днів зберігається оперативний журнал спроб входу <c>sec.LoginAttempt</c> (PSS-24).</param>
/// <param name="MinLoginAttemptRetentionDays">Найменше допустиме значення.</param>
/// <param name="MaxLoginAttemptRetentionDays">Найбільше допустиме значення.</param>
/// <param name="DefaultLoginAttemptRetentionDays">Значення за замовчуванням (400).</param>
public sealed record SessionSettingsView(
    int IdleTimeoutMinutes, int MinIdleTimeoutMinutes, int MaxIdleTimeoutMinutes, int DefaultIdleTimeoutMinutes,
    int LoginAttemptRetentionDays, int MinLoginAttemptRetentionDays, int MaxLoginAttemptRetentionDays,
    int DefaultLoginAttemptRetentionDays);

/// <summary>Запит на зміну налаштувань сеансу.</summary>
/// <param name="IdleTimeoutMinutes">Вихід після стількох хвилин без дій.</param>
/// <param name="LoginAttemptRetentionDays">
/// Строк зберігання <c>sec.LoginAttempt</c> у днях (30–3650); <c>null</c> — не змінювати (старі клієнти шлють лише
/// <paramref name="IdleTimeoutMinutes"/>).
/// </param>
public sealed record SessionSettingsInput(int IdleTimeoutMinutes, int? LoginAttemptRetentionDays = null);

/// <summary>Читання налаштувань сеансу. Право <c>Security.ManageUsers</c>.</summary>
public sealed class GetSessionSettingsHandler(
    IIdleTimeoutPolicy policy, ISessionSettingsStore store, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Поточне значення й межі.</summary>
    /// <param name="ct">Токен скасування.</param>
    public async Task<SessionSettingsView> HandleAsync(CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, "Security.ManageUsers", ct).ConfigureAwait(false);

        return await ViewAsync(policy, store, ct).ConfigureAwait(false);
    }

    internal static async Task<SessionSettingsView> ViewAsync(
        IIdleTimeoutPolicy policy, ISessionSettingsStore store, CancellationToken ct)
        => new(
            await policy.GetMinutesAsync(ct).ConfigureAwait(false), policy.MinimumMinutes,
            SessionSettings.MaxIdleTimeoutMinutes, SessionSettings.DefaultIdleTimeoutMinutes,
            SessionSettings.ResolveLoginAttemptRetentionDays(
                (await store.ReadLoginAttemptRetentionDaysAsync(ct).ConfigureAwait(false))
                ?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            SessionSettings.MinLoginAttemptRetentionDays, SessionSettings.MaxLoginAttemptRetentionDays,
            SessionSettings.DefaultLoginAttemptRetentionDays);
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

        // ⛔ PSS-24: строк зберігання спроб входу — той самий екран і те саме право. Поза 30–3650 — 422, а не мовчазне
        // обмеження: людина має знати, що її число не прийнято (обмеження мовчки лишається лише для рядка, зміненого в обхід).
        if (input.LoginAttemptRetentionDays is { } retention
            && (retention < SessionSettings.MinLoginAttemptRetentionDays
                || retention > SessionSettings.MaxLoginAttemptRetentionDays))
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Строк зберігання спроб входу: від {SessionSettings.MinLoginAttemptRetentionDays} до "
                + $"{SessionSettings.MaxLoginAttemptRetentionDays} днів.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = SessionSettings.RetentionOutOfRangeKey,
                    ["name"] = "loginAttemptRetentionDays",
                    ["min"] = SessionSettings.MinLoginAttemptRetentionDays,
                    ["max"] = SessionSettings.MaxLoginAttemptRetentionDays,
                });
        }

        var previous = await store.ReadIdleTimeoutMinutesAsync(ct).ConfigureAwait(false);
        var previousRetention = SessionSettings.ResolveLoginAttemptRetentionDays(
            (await store.ReadLoginAttemptRetentionDaysAsync(ct).ConfigureAwait(false))
            ?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var now = clock.UtcNow;

        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                await store.WriteIdleTimeoutMinutesAsync(minutes, profile.UserId, now, token).ConfigureAwait(false);
                if (input.LoginAttemptRetentionDays is { } retentionDays)
                {
                    await store.WriteLoginAttemptRetentionDaysAsync(retentionDays, profile.UserId, now, token)
                        .ConfigureAwait(false);
                }

                // ⚠ Подія одна на збереження; поля строку зберігання — лише коли його передано (без них JSON як був).
                var details = new Dictionary<string, object?>
                {
                    ["idleTimeoutMinutes"] = minutes,
                    ["previousIdleTimeoutMinutes"] = previous ?? SessionSettings.DefaultIdleTimeoutMinutes,
                };
                if (input.LoginAttemptRetentionDays is { } newRetention)
                {
                    details["loginAttemptRetentionDays"] = newRetention;
                    details["previousLoginAttemptRetentionDays"] = previousRetention;
                }

                await audit.WriteSecurityEventAsync(
                    new SecurityEventRecord(
                        now, SessionSettings.UpdatedEventType, TargetUserId: null, TargetRoleId: null,
                        JsonSerializer.Serialize(details, Json),
                        profile.UserId, currentUser.CorrelationId),
                    token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        policy.Invalidate();

        return await GetSessionSettingsHandler.ViewAsync(policy, store, ct).ConfigureAwait(false);
    }
}
