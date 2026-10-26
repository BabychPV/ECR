using Ecr.Domain.Enums;

namespace Ecr.Application.Security;

/// <summary>
/// Q-17 (уточнення D-456 для D-457): «річний контекст» введеного не-місячного стовпця й складу рядків таблиці
/// «місяці в колонках» місячного проєкту — агрегат за місяцями календаря року.
/// </summary>
/// <remarks>
/// <para>
/// Стан періоду: є <c>Open</c> (або <c>Scheduled</c> — внесення наперед) → <c>Open</c>; інакше є <c>Grace</c> →
/// <c>Grace</c>; інакше (усі <c>Closed</c>) → <c>Closed</c>.
/// </para>
/// <para>
/// Стан аркуша: будь-який місяць <c>Draft</c> → <c>Draft</c>; інакше будь-який <c>Rejected</c> → <c>Rejected</c>;
/// інакше будь-який <c>Submitted</c> → <c>Submitted</c>; інакше <c>Approved</c>. Подання одного місяця
/// не-місячних стовпців НЕ блокує, доки решта місяців — чернетки.
/// </para>
/// </remarks>
public static class AnnualPeriodRule
{
    /// <summary>Агрегат стану періоду; порожній набір — <c>Open</c> (нема що обмежувати).</summary>
    /// <param name="states">Ефективні стани місяців календаря.</param>
    public static PeriodState AggregateState(IEnumerable<PeriodState> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        var hasGrace = false;
        var any = false;
        foreach (var state in states)
        {
            any = true;
            if (state is PeriodState.Open or PeriodState.Scheduled)
            {
                return PeriodState.Open;
            }

            hasGrace |= state == PeriodState.Grace;
        }

        if (!any)
        {
            return PeriodState.Open;
        }

        return hasGrace ? PeriodState.Grace : PeriodState.Closed;
    }

    /// <summary>Агрегат стану аркуша; порожній набір — <c>Draft</c>.</summary>
    /// <param name="statuses">Стани аркуша за місяці календаря (відсутній запис — <c>Draft</c>).</param>
    public static DocumentStatus AggregateStatus(IEnumerable<DocumentStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);

        var any = false;
        var rejected = false;
        var submitted = false;
        foreach (var status in statuses)
        {
            any = true;
            switch (status)
            {
                case DocumentStatus.Draft:
                    return DocumentStatus.Draft;
                case DocumentStatus.Rejected:
                    rejected = true;
                    break;
                case DocumentStatus.Submitted:
                    submitted = true;
                    break;
                default:
                    break;
            }
        }

        if (!any)
        {
            return DocumentStatus.Draft;
        }

        if (rejected)
        {
            return DocumentStatus.Rejected;
        }

        return submitted ? DocumentStatus.Submitted : DocumentStatus.Approved;
    }

    /// <summary>
    /// Пізня правка за річним правилом: немає <c>Open</c>, але є <c>Grace</c> (<c>IsLateEdit</c>).
    /// </summary>
    /// <param name="states">Ефективні стани місяців календаря.</param>
    public static bool IsLate(IEnumerable<PeriodState> states) => AggregateState(states) == PeriodState.Grace;
}
