// src/Ecr.Application/Registries/OrphanScanPlan.cs
using Ecr.Domain.Enums;

namespace Ecr.Application.Registries;

/// <summary>
/// Рішення нічної перевірки й точкового перерахунку: яким рядкам поставити
/// <c>IsOrphaned</c>, а яким — зняти (ФВ-8.13a, D-98).
/// </summary>
/// <remarks>
/// ⚠ Виділено окремо від сховища навмисно. Реалізація в базі — один
/// set-based <c>UPDATE</c> на мільйони рядків, і перевірити його правило
/// інакше, ніж піднявши SQL Server, неможливо. Тут те саме правило живе у
/// вигляді, який читається і перевіряється; SQL лишається його перекладом.
/// <para>
/// Механізм **симетричний**: те, що ставить ознаку, її ж і знімає. Асиметрія
/// тут — не половина функції, а пастка: користувач виправляє довідник, а
/// <c>Submit</c> лишається заблокованим із помилкою, причину якої вже усунуто.
/// </para>
/// </remarks>
public static class OrphanScanPlan
{
    /// <summary>Обчислює, що змінити.</summary>
    /// <param name="candidates">Рядки з посиланнями на довідник.</param>
    /// <returns>Розділені переліки: поставити і зняти.</returns>
    public static OrphanScanDecision Plan(IEnumerable<OrphanCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var toFlag = new List<long>();
        var toClear = new List<long>();

        foreach (var candidate in candidates)
        {
            // ⛔ Закриті періоди не чіпаються. Їхні дані вже подані й
            // погоджені; ознака нічого не розблокує і нічого не заборонить,
            // зате перепише рядок, що входить у контрольну суму зрізу.
            if (!IsScannable(candidate.PeriodState))
            {
                continue;
            }

            switch (candidate)
            {
                case { ReferenceIsValid: false, IsOrphaned: false }:
                    toFlag.Add(candidate.RowId);
                    break;

                case { ReferenceIsValid: true, IsOrphaned: true }:
                    toClear.Add(candidate.RowId);
                    break;

                default:
                    // Стан збігається з дійсністю — писати нічого. Це не
                    // оптимізація: зайвий UPDATE підняв би ModifiedAt і зламав
                    // оптимістичне блокування чужої відкритої форми.
                    break;
            }
        }

        return new OrphanScanDecision(toFlag, toClear);
    }

    /// <summary>Чи перевіряється період узагалі.</summary>
    /// <param name="state">Стан періоду.</param>
    public static bool IsScannable(PeriodState state)
        => state is PeriodState.Open or PeriodState.Grace;
}

/// <summary>Рядок-кандидат для перевірки осиротілості.</summary>
/// <param name="RowId">Рядок <c>doc.TableRow</c>.</param>
/// <param name="PeriodState">Стан періоду рядка.</param>
/// <param name="IsOrphaned">Ознака, що зараз збережена на рядку.</param>
/// <param name="ReferenceIsValid">
/// Чи чинний запис довідника, на який рядок посилається, на дату його періоду.
/// Обчислюється <c>RegistryResolver.IsSelectable</c> — тим самим правилом, що
/// формує випадний список.
/// </param>
public sealed record OrphanCandidate(
    long RowId, PeriodState PeriodState, bool IsOrphaned, bool ReferenceIsValid);

/// <summary>Що змінити за підсумком перевірки.</summary>
/// <param name="ToFlag">Рядкам поставити <c>IsOrphaned</c>.</param>
/// <param name="ToClear">Рядкам зняти ознаку і занулити <c>OrphanedAt</c>.</param>
public sealed record OrphanScanDecision(IReadOnlyList<long> ToFlag, IReadOnlyList<long> ToClear)
{
    /// <summary>Скільки рядків буде змінено.</summary>
    public int Total => ToFlag.Count + ToClear.Count;
}
