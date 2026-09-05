using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Adapters.PiAf;

/// <summary>
/// Планує дозбір пропущених інтервалів за журналом покриття.
/// </summary>
/// <remarks>
/// <c>Watermark</c> у розкладі — **оптимізація, а не стан** (ER-I-03): його
/// втрата не має коштувати даних, тому справжнім джерелом істини є
/// <c>itg.CollectionCoverage</c>.
/// </remarks>
public sealed class CatchUpPlanner(ICollectionStore store, IClock clock)
{
    /// <summary>
    /// Скільки прогалин має сенс віддати за раз.
    /// </summary>
    /// <remarks>
    /// Не оптимізація: п'ятсот окремих дірок у покритті означають, що джерело
    /// віддає уривками місяцями, і це вже не наздоганяння, а інцидент —
    /// закривати його треба руками, а не чергою з тисяч мікрозапитів.
    /// </remarks>
    public const int MaxGaps = 500;

    /// <summary>Знаходить непокриті інтервали за період lookback.</summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="notBefore">Нижня межа огляду; давніше не шукаємо.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Прогалини від найстарішої: спершу закриваємо давнє.</returns>
    public async Task<IReadOnlyList<(DateTime From, DateTime To)>> PlanAsync(
        int sourceEntityId, DateTime notBefore, CancellationToken ct)
    {
        var covered = await store
            .GetCoverageAsync(sourceEntityId, notBefore, ct)
            .ConfigureAwait(false);

        // ⚠ Верхня межа — «зараз», а не кінець доби. Прогалина «до кінця
        // сьогоднішнього дня» існувала б завжди, і наздоганяння ганяло б
        // джерело за даними, яких ще немає.
        return FindGaps(covered, notBefore, clock.UtcNow);
    }

    /// <summary>
    /// Доповнення покриття до суцільного інтервалу — чиста функція.
    /// </summary>
    /// <param name="covered">Покриті інтервали в будь-якому порядку.</param>
    /// <param name="from">Початок огляду.</param>
    /// <param name="to">Кінець огляду.</param>
    /// <remarks>
    /// ⚠ Виділена і статична навмисно: це єдине місце, де вирішується, що
    /// таке «дірка». Перевірити її можна без бази й без джерела, а помилка
    /// тут не падає — вона мовчки не збирає даних за проміжок, і побачать це
    /// на звірці.
    /// </remarks>
    public static IReadOnlyList<(DateTime From, DateTime To)> FindGaps(
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
