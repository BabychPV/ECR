// src/Ecr.Application/Security/SessionSettingsHandlers.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;
using Microsoft.Extensions.Logging;

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
public static partial class SessionSettings
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
    public const string TechnicalLogRetentionKey = "Retention.TechnicalLogDays";

    /// <summary>За замовчуванням — 400 днів (слово людини 20.11).</summary>
    public const int DefaultTechnicalLogRetentionDays = 400;

    /// <summary>Підлога: менше 30 днів — опечатка, а не політика.</summary>
    public const int MinTechnicalLogRetentionDays = 30;

    /// <summary>Стеля: 100 років (36 500 днів) — далі «не видаляти» по суті.</summary>
    public const int MaxTechnicalLogRetentionDays = 36500;

    /// <summary>Ключ у <c>sys_ecr.SystemSetting</c>: мінімальна глибина аудиту в <c>aud.*</c>, місяців (рішення людини 20.11 п. 7).</summary>
    /// <remarks>
    /// ⛔ Той самий ключ і ті самі межі, що в <c>Ecr.Infrastructure.Jobs.RetentionSettings</c> (PSS-13/PSS-27, лан L6): один
    /// рядок налаштувань, два читачі. Підлога 120 міс. — рішення людини; саму архівацію (<c>arc.usp_ArchiveAudit</c>) веде L6.
    /// </remarks>
    public const string AuditRetentionKey = "Retention.AuditMonths";

    /// <summary>Типово — рівно підлога.</summary>
    public const int DefaultAuditRetentionMonths = 120;

    /// <summary>Підлога глибини аудиту, місяців (рішення людини 20.11 п. 7): менше не приймається й не діє.</summary>
    public const int MinAuditRetentionMonths = 120;

    /// <summary>Верхня межа, місяців (100 років).</summary>
    public const int MaxAuditRetentionMonths = 1200;

    /// <summary>Ключ відмови «глибина аудиту поза межами».</summary>
    public const string AuditRetentionOutOfRangeKey = "err.ECR-REQ-0422.auditRetentionOutOfRange";

    /// <summary>Чинна глибина аудиту: нижче 120 чи не число — 120 (типове) з попередженням; вище 1200 — 1200 з попередженням.</summary>
    /// <param name="stored">Сире значення з <c>SystemSetting</c>; <c>null</c> — рядка немає (мовчки типове).</param>
    /// <param name="logger">Куди йде попередження про відкинуте значення; <c>null</c> — нікуди.</param>
    public static int ResolveAuditRetentionMonths(string? stored, ILogger? logger = null)
        => ResolveRetention(
            AuditRetentionKey, stored, DefaultAuditRetentionMonths, MinAuditRetentionMonths, MaxAuditRetentionMonths, logger);

    /// <summary>
    /// Спільне правило строків зберігання — ТЕ САМЕ, що в <c>Ecr.Infrastructure.Jobs.RetentionSettings</c> (лан L6): немає рядка —
    /// типове (мовчки); не число чи нижче мінімуму — типове з попередженням у лозі; вище максимуму — максимум з попередженням.
    /// </summary>
    /// <remarks>
    /// ⛔ Нижче мінімуму — саме типове, а не мінімум: рядок «0»/«1»/«-5» — опечатка, і мовчазне «30 днів» стерло б
    /// журнали за добу замість обережних 400. Те, що показує екран і що діє у прибиранні, — одне й те саме число.
    /// </remarks>
    internal static int ResolveRetention(string key, string? stored, int fallback, int min, int max, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return fallback;
        }

        if (!int.TryParse(
                stored.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                out var value))
        {
            if (logger is not null)
            {
                LogRejected(logger, key, stored, "не число", fallback);
            }

            return fallback;
        }

        if (value < min)
        {
            if (logger is not null)
            {
                LogRejected(logger, key, stored, $"менше мінімуму {min.ToString(System.Globalization.CultureInfo.InvariantCulture)}", fallback);
            }

            return fallback;
        }

        if (value > max)
        {
            if (logger is not null)
            {
                LogRejected(logger, key, stored, $"більше максимуму {max.ToString(System.Globalization.CultureInfo.InvariantCulture)}", max);
            }

            return max;
        }

        return value;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Налаштування {Key} = «{Raw}» відкинуто ({Problem}); діє {Effective}.")]
    private static partial void LogRejected(ILogger logger, string key, string raw, string problem, int effective);

    /// <summary>Ключ відмови «строк зберігання технічних журналів (спроб входу) поза межами».</summary>
    public const string RetentionOutOfRangeKey = "err.ECR-REQ-0422.technicalLogRetentionOutOfRange";

    /// <summary>Чинний строк зберігання технічних журналів: нижче 30 чи не число — 400 з попередженням; вище 36500 — 36500 з попередженням.</summary>
    /// <param name="stored">Сире значення з <c>SystemSetting</c>; <c>null</c> — рядка немає (мовчки 400).</param>
    /// <param name="logger">Куди йде попередження про відкинуте значення; <c>null</c> — нікуди.</param>
    public static int ResolveTechnicalLogRetentionDays(string? stored, ILogger? logger = null)
        => ResolveRetention(
            TechnicalLogRetentionKey, stored, DefaultTechnicalLogRetentionDays,
            MinTechnicalLogRetentionDays, MaxTechnicalLogRetentionDays, logger);
}

/// <summary>Налаштування сеансу для екрана «Безпека».</summary>
/// <param name="IdleTimeoutMinutes">Вихід після стількох хвилин без дій.</param>
/// <param name="MinIdleTimeoutMinutes">Найменше допустиме значення.</param>
/// <param name="MaxIdleTimeoutMinutes">Найбільше допустиме значення.</param>
/// <param name="DefaultIdleTimeoutMinutes">Значення за замовчуванням.</param>
/// <param name="TechnicalLogRetentionDays">Скільки днів зберігаються технічні журнали, зокрема <c>sec.LoginAttempt</c> (<c>Retention.TechnicalLogDays</c>).</param>
/// <param name="MinTechnicalLogRetentionDays">Найменше допустиме значення.</param>
/// <param name="MaxTechnicalLogRetentionDays">Найбільше допустиме значення.</param>
/// <param name="DefaultTechnicalLogRetentionDays">Значення за замовчуванням (400).</param>
/// <param name="AuditRetentionMonths">Мінімальна глибина аудиту в <c>aud.*</c>, місяців (<c>Retention.AuditMonths</c>).</param>
/// <param name="MinAuditRetentionMonths">Найменше допустиме значення (120).</param>
/// <param name="MaxAuditRetentionMonths">Найбільше допустиме значення.</param>
/// <param name="DefaultAuditRetentionMonths">Значення за замовчуванням (120).</param>
public sealed record SessionSettingsView(
    int IdleTimeoutMinutes, int MinIdleTimeoutMinutes, int MaxIdleTimeoutMinutes, int DefaultIdleTimeoutMinutes,
    int TechnicalLogRetentionDays, int MinTechnicalLogRetentionDays, int MaxTechnicalLogRetentionDays,
    int DefaultTechnicalLogRetentionDays,
    int AuditRetentionMonths, int MinAuditRetentionMonths, int MaxAuditRetentionMonths,
    int DefaultAuditRetentionMonths);

/// <summary>Запит на зміну налаштувань сеансу.</summary>
/// <param name="IdleTimeoutMinutes">Вихід після стількох хвилин без дій.</param>
/// <param name="TechnicalLogRetentionDays">Строк зберігання технічних журналів, днів (30–36500); <c>null</c> — не змінювати.</param>
/// <param name="AuditRetentionMonths">Мінімальна глибина аудиту, місяців (120–1200); <c>null</c> — не змінювати.</param>
public sealed record SessionSettingsInput(
    int IdleTimeoutMinutes, int? TechnicalLogRetentionDays = null, int? AuditRetentionMonths = null);

/// <summary>Читання налаштувань сеансу. Право <c>Security.ManageUsers</c>.</summary>
public sealed class GetSessionSettingsHandler(
    IIdleTimeoutPolicy policy, ISessionSettingsStore store, IAccessDecisionService access, ICurrentUser currentUser,
    ILogger<GetSessionSettingsHandler>? logger = null)
{
    /// <summary>Поточне значення й межі.</summary>
    /// <param name="ct">Токен скасування.</param>
    public async Task<SessionSettingsView> HandleAsync(CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, "Security.ManageUsers", ct).ConfigureAwait(false);

        return await ViewAsync(policy, store, logger, ct).ConfigureAwait(false);
    }

    internal static async Task<SessionSettingsView> ViewAsync(
        IIdleTimeoutPolicy policy, ISessionSettingsStore store, ILogger? logger, CancellationToken ct)
        => new(
            await policy.GetMinutesAsync(ct).ConfigureAwait(false), policy.MinimumMinutes,
            SessionSettings.MaxIdleTimeoutMinutes, SessionSettings.DefaultIdleTimeoutMinutes,
            SessionSettings.ResolveTechnicalLogRetentionDays(
                await store.ReadTechnicalLogRetentionDaysAsync(ct).ConfigureAwait(false), logger),
            SessionSettings.MinTechnicalLogRetentionDays, SessionSettings.MaxTechnicalLogRetentionDays,
            SessionSettings.DefaultTechnicalLogRetentionDays,
            SessionSettings.ResolveAuditRetentionMonths(
                await store.ReadAuditRetentionMonthsAsync(ct).ConfigureAwait(false), logger),
            SessionSettings.MinAuditRetentionMonths, SessionSettings.MaxAuditRetentionMonths,
            SessionSettings.DefaultAuditRetentionMonths);
}

/// <summary>Зміна налаштувань сеансу. Право <c>Security.ManageUsers</c>.</summary>
/// <remarks>
/// ⚠ Діє без перезапуску і для ЧИННИХ сеансів: межу бездіяльності сервер звіряє на кожному запиті з
/// поточним значенням (<see cref="IIdleTimeoutPolicy"/>), а не з тим, що було на вході.
/// </remarks>
public sealed class SaveSessionSettingsHandler(
    IIdleTimeoutPolicy policy, ISessionSettingsStore store, IAccessDecisionService access, IUnitOfWork uow,
    IAuditWriter audit, ICurrentUser currentUser, IClock clock, ILogger<SaveSessionSettingsHandler>? logger = null)
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

        // ⛔ PSS-24: строк зберігання спроб входу — той самий екран і те саме право. Поза 30–36500 — 422, а не мовчазне
        // обмеження: людина має знати, що її число не прийнято (обмеження мовчки лишається лише для рядка, зміненого в обхід).
        if (input.TechnicalLogRetentionDays is { } retention
            && (retention < SessionSettings.MinTechnicalLogRetentionDays
                || retention > SessionSettings.MaxTechnicalLogRetentionDays))
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Строк зберігання спроб входу: від {SessionSettings.MinTechnicalLogRetentionDays} до "
                + $"{SessionSettings.MaxTechnicalLogRetentionDays} днів.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = SessionSettings.RetentionOutOfRangeKey,
                    ["name"] = "technicalLogRetentionDays",
                    ["min"] = SessionSettings.MinTechnicalLogRetentionDays,
                    ["max"] = SessionSettings.MaxTechnicalLogRetentionDays,
                });
        }

        // ⛔ Глибина аудиту: підлога 120 міс. (рішення людини 20.11 п. 7) — менше не приймається, а не «піднімається мовчки».
        if (input.AuditRetentionMonths is { } auditMonths
            && (auditMonths < SessionSettings.MinAuditRetentionMonths
                || auditMonths > SessionSettings.MaxAuditRetentionMonths))
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Глибина аудиту: від {SessionSettings.MinAuditRetentionMonths} до "
                + $"{SessionSettings.MaxAuditRetentionMonths} місяців.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = SessionSettings.AuditRetentionOutOfRangeKey,
                    ["name"] = "auditRetentionMonths",
                    ["min"] = SessionSettings.MinAuditRetentionMonths,
                    ["max"] = SessionSettings.MaxAuditRetentionMonths,
                });
        }

        var previousAudit = SessionSettings.ResolveAuditRetentionMonths(
            await store.ReadAuditRetentionMonthsAsync(ct).ConfigureAwait(false), logger);
        var previous = await store.ReadIdleTimeoutMinutesAsync(ct).ConfigureAwait(false);
        var previousRetention = SessionSettings.ResolveTechnicalLogRetentionDays(
            await store.ReadTechnicalLogRetentionDaysAsync(ct).ConfigureAwait(false), logger);
        var now = clock.UtcNow;

        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                await store.WriteIdleTimeoutMinutesAsync(minutes, profile.UserId, now, token).ConfigureAwait(false);
                if (input.TechnicalLogRetentionDays is { } retentionDays)
                {
                    await store.WriteTechnicalLogRetentionDaysAsync(retentionDays, profile.UserId, now, token)
                        .ConfigureAwait(false);
                }

                if (input.AuditRetentionMonths is { } auditToWrite)
                {
                    await store.WriteAuditRetentionMonthsAsync(auditToWrite, profile.UserId, now, token)
                        .ConfigureAwait(false);
                }

                // ⚠ Подія одна на збереження; поля строку зберігання — лише коли його передано (без них JSON як був).
                var details = new Dictionary<string, object?>
                {
                    ["idleTimeoutMinutes"] = minutes,
                    ["previousIdleTimeoutMinutes"] = previous ?? SessionSettings.DefaultIdleTimeoutMinutes,
                };
                if (input.TechnicalLogRetentionDays is { } newRetention)
                {
                    details["technicalLogRetentionDays"] = newRetention;
                    details["previousTechnicalLogRetentionDays"] = previousRetention;
                }

                if (input.AuditRetentionMonths is { } newAudit)
                {
                    details["auditRetentionMonths"] = newAudit;
                    details["previousAuditRetentionMonths"] = previousAudit;
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

        return await GetSessionSettingsHandler.ViewAsync(policy, store, logger, ct).ConfigureAwait(false);
    }
}
