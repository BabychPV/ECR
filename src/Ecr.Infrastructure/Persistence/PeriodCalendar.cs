using Ecr.Domain.Entities.Documents;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Календар проєкту за рік: рядки <c>doc.Period</c>, ключ яких лежить у
/// <c>(рік*100, рік*100+12]</c>.
/// </summary>
/// <remarks>
/// ⚠ Q-17. Одна формула для двох споживачів: стани місяців року
/// (<c>AccessDecisionService.MonthEnvironmentAsync</c>) і маршрутизація
/// місячних стовпців (<see cref="MonthColumnRouter"/>, «якір року» = найменший
/// ключ календаря). Дві копії предиката розійшлися б першою ж правкою однієї.
/// Лише для місячних проєктів, де номер періоду дорівнює номеру місяця.
/// </remarks>
public static class PeriodCalendar
{
    /// <summary>Місяців (періодів) у році місячного проєкту.</summary>
    public const int MonthsInYear = 12;

    /// <summary>Періоди проєкту за рік — рівність за ключем партиції в діапазоні.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="year">Рік.</param>
    /// <returns>Незавершений запит без відстеження; проєкцію добирає викликач.</returns>
    public static IQueryable<Period> YearPeriods(EcrDbContext db, int projectId, int year)
    {
        ArgumentNullException.ThrowIfNull(db);

        var first = year * 100;
        return db.Periods
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.PeriodKeyValue > first && x.PeriodKeyValue <= first + MonthsInYear);
    }

    /// <summary>
    /// «Якір року»: найменший ключ календаря; <c>null</c> — у році немає жодного рядка періоду.
    /// </summary>
    /// <param name="keys">Ключі періодів року.</param>
    public static int? AnchorOf(IEnumerable<int> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        int? anchor = null;
        foreach (var key in keys)
        {
            if (anchor is null || key < anchor)
            {
                anchor = key;
            }
        }

        return anchor;
    }
}
