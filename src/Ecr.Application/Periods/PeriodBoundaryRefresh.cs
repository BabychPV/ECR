using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;

namespace Ecr.Application.Periods;

/// <summary>
/// Перераховує збережені <c>ComputedOpenAt/GraceAt/CloseAt</c> наявних періодів після зміни
/// політики зсувів чи поясу проєкту (ФВ-1.6, D-217).
/// </summary>
/// <remarks>
/// ⛔ ЗАКРИТІ періоди не чіпаються: їхні межі — вже чиїсь зобов'язання (подана звітність,
/// <c>IsLateEdit</c>), і ретроактивний зсув переписав би минуле (той самий принцип, що
/// <c>Project.ChangeTimeZone</c>, ФВ-1.1a). Лишається Scheduled/Open/Grace: саме їхні межі
/// ще дійсні для майбутніх переходів станів.
/// ⚠ Межі рахує <see cref="Period.RecomputeBoundaries"/> через <c>TimeZoneInfo</c> — тобто з
/// бази поясів ОС, яка діє на момент виклику (D-217).
/// </remarks>
internal static class PeriodBoundaryRefresh
{
    /// <summary>Перераховує межі незакритих періодів; повертає, скільки періодів оновлено.</summary>
    public static int Apply(IEnumerable<Period> periods, PeriodPolicy policy, TimeZoneInfo siteTimeZone)
    {
        ArgumentNullException.ThrowIfNull(periods);

        var refreshed = 0;
        foreach (var period in periods)
        {
            if (period.State == PeriodState.Closed)
            {
                continue;
            }

            period.RecomputeBoundaries(policy, siteTimeZone);
            refreshed++;
        }

        return refreshed;
    }
}
