// src/Ecr.Application/Permits/PermitTypeActor.cs
using Ecr.Domain.Enums;

namespace Ecr.Application.Permits;

/// <summary>
/// Хто змінює тип дозволу: користувач запиту (після перевірки прав) або СИСТЕМА — дія розгортання, як сід (✎ AIRSYS01).
/// </summary>
/// <param name="UserId">Користувач; <c>null</c> — система (у <c>cfg.PermitTypeDef</c> — NULL, у журналі — 0, «system»).</param>
/// <param name="Profile">Профіль доступу для перевірок заборон довідників; у системи — без заборон.</param>
/// <param name="CorrelationId">Кореляція для журналу.</param>
/// <remarks>
/// ⛔ Системного автора будує лише <see cref="System"/>, і викликає його тільки крок старту (<c>AirTypeStartupStep</c>) через
/// <see cref="AirLimits.AirTypeSystemProvisioner"/>: жоден контролер не має до цього шляху доступу (сторож —
/// <c>AirTypeStartupTests</c>). Перевірку ПРАВ система обходить, доменні перевірки складу типу — ні.
/// </remarks>
public sealed record PermitTypeActor(int? UserId, Security.AccessProfile Profile, string CorrelationId)
{
    /// <summary>Id автора в журналах (<c>aud.StructureChange.ChangedByUserId</c>): користувач або 0 — «system».</summary>
    public int AuditUserId => UserId ?? SystemUserId;

    /// <summary>Умовний Id системи в журналах — та сама умовність, що в задачах (<c>PeriodStateJob.SystemUserId</c>).</summary>
    public const int SystemUserId = 0;

    /// <summary>Система: без перевірки прав, без заборон, автор — NULL / 0 «system».</summary>
    internal static PermitTypeActor System() => new(
        null,
        new Security.AccessProfile
        {
            CacheKey = "system",
            UserId = SystemUserId,
            SecurityStamp = "system",
            Permissions = new HashSet<string>(StringComparer.Ordinal),
            Grants = new Dictionary<string, GrantLevel>(StringComparer.Ordinal),
            Denies = new HashSet<string>(StringComparer.Ordinal),
            RoleIds = new HashSet<int>(),
        },
        Guid.NewGuid().ToString("N"));
}
