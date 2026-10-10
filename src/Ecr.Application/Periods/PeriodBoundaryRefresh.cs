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
/// <para>
/// ⛔ R9-F3 / F3-03. «Закритий» — не лише ЗБЕРЕЖЕНИЙ <c>Closed</c>. Ще два випадки межі не
/// отримують:
/// </para>
/// <list type="number">
/// <item>ПЕРЕВІДКРИТИЙ період (<c>ReopenedUntil</c> задано; <see cref="Period.Reopen"/> приймає лише
/// <c>Closed</c>). Його вже було закрито, і відкритим його тримає лише дедлайн людини з правом
/// <c>Period.Reopen</c> і причиною. Доти нові межі діставались і йому: після <c>until</c>
/// <c>Calculate</c> повертався до меж, і зсув hard-close у майбутнє (право <c>Project.Manage</c>,
/// без причини й події Reopen) тримав період у <c>Grace</c> днями чи місяцями.</item>
/// <item>Період, що ЕФЕКТИВНО вже минув власну межу закриття (поточний момент ≥
/// <c>ComputedCloseAt</c>), а збережений стан ще <c>Grace</c>, бо годинний <c>PeriodStateJob</c>
/// не відпрацював. Інакше зміна політики в цьому вікні відкривала б закритий період знову —
/// а <c>Closed</c> назад веде лише Reopen (<c>PeriodStateCalculator.Effective</c>, D-204).</item>
/// </list>
/// </remarks>
internal static class PeriodBoundaryRefresh
{
    /// <summary>Перераховує межі незакритих періодів; повертає, скільки періодів оновлено.</summary>
    /// <param name="periods">Періоди проєкту.</param>
    /// <param name="policy">Політика зсувів.</param>
    /// <param name="siteTimeZone">Пояс майданчика.</param>
    /// <param name="utcNow">
    /// Поточний момент; <c>null</c> — без перевірки «межу закриття вже минуто» (зміна поясу:
    /// домен дозволяє її лише поки всі періоди <c>Scheduled</c>, ФВ-1.1a).
    /// </param>
    public static int Apply(
        IEnumerable<Period> periods, PeriodPolicy policy, TimeZoneInfo siteTimeZone, DateTime? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(periods);

        var refreshed = 0;
        foreach (var period in periods)
        {
            if (period.State == PeriodState.Closed || period.ReopenedUntil is not null)
            {
                continue;
            }

            // ⚠ Нульові межі (календар ще не рахував) «минулими» не вважаються — їх саме треба порахувати.
            if (utcNow is { } now && period.ComputedCloseAt != default && now >= period.ComputedCloseAt)
            {
                continue;
            }

            period.RecomputeBoundaries(policy, siteTimeZone);
            refreshed++;
        }

        return refreshed;
    }
}
