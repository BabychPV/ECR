// src/Ecr.Application/Integration/GapFinder.cs
using Ecr.Application.Ports;

namespace Ecr.Application.Integration;

/// <summary>
/// Доповнення журналу покриття до суцільного інтервалу — що саме вважається
/// «діркою».
/// </summary>
/// <remarks>
/// ⚠ Живе в <c>Ecr.Application</c>, а не в адаптері PI AF, і це не смак:
/// прогалини потрібні <b>двом</b> — наздоганянню в
/// <c>Ecr.Adapters.PiAf.CatchUpPlanner</c> і екрану джерел, який будує
/// <c>Ecr.Infrastructure</c>. Проєкти сусідні й одне одного не бачать, тож
/// без спільного місця з'явилися б дві реалізації: екран показував би «усе
/// добре» рівно тоді, коли збирач наздоганяє, і навпаки. Обидві були б
/// правдоподібні, і жодна не падала б.
/// <para>
/// ⛔ Функція чиста навмисно. Помилка тут не проявляється як помилка: вона
/// мовчки не збирає даних за проміжок, і побачать це на звірці через місяць —
/// тому її треба вміти перевіряти без бази й без джерела.
/// </para>
/// </remarks>
public static class GapFinder
{
    /// <summary>
    /// Скільки прогалин має сенс віддати за раз.
    /// </summary>
    /// <remarks>
    /// П'ятсот окремих дірок означають, що джерело віддає уривками місяцями:
    /// це вже не наздоганяння, а інцидент — закривати його треба руками, а не
    /// чергою з тисяч мікрозапитів.
    /// </remarks>
    public const int MaxGaps = 500;

    /// <summary>Знаходить непокриті інтервали у вікні.</summary>
    /// <param name="covered">Покриті інтервали в будь-якому порядку.</param>
    /// <param name="from">Початок огляду.</param>
    /// <param name="to">Кінець огляду.</param>
    /// <returns>Прогалини від найстарішої: спершу закриваємо давнє.</returns>
    public static IReadOnlyList<(DateTime From, DateTime To)> Find(
        IReadOnlyList<TimeInterval> covered, DateTime from, DateTime to)
    {
        ArgumentNullException.ThrowIfNull(covered);

        if (to <= from)
        {
            return [];
        }

        // Обрізаємо по вікну огляду: інтервал, що виходить за межі, покриває
        // тільки свою частину всередині.
        var clipped = covered
            .Select(c => (From: Max(c.FromUtc, from), To: Min(c.ToUtc, to)))
            .Where(c => c.To > c.From)
            .OrderBy(c => c.From)
            .ToList();

        var gaps = new List<(DateTime From, DateTime To)>();
        var cursor = from;

        foreach (var interval in clipped)
        {
            if (gaps.Count >= MaxGaps)
            {
                break;
            }

            // Суміжні й перекриті інтервали зливаються самим ходом курсора:
            // окремий крок «злити» був би другим місцем, де живе те саме
            // правило.
            if (interval.From > cursor)
            {
                gaps.Add((cursor, interval.From));
            }

            if (interval.To > cursor)
            {
                cursor = interval.To;
            }
        }

        if (cursor < to && gaps.Count < MaxGaps)
        {
            gaps.Add((cursor, to));
        }

        return gaps;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
