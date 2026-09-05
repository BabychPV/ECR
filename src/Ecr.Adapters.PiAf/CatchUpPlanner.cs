using Ecr.Application.Integration;
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
    /// <summary>Скільки прогалин має сенс віддати за раз; межа спільна з <see cref="GapFinder"/>.</summary>
    public const int MaxGaps = GapFinder.MaxGaps;

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
    /// Доповнення покриття до суцільного інтервалу.
    /// </summary>
    /// <param name="covered">Покриті інтервали в будь-якому порядку.</param>
    /// <param name="from">Початок огляду.</param>
    /// <param name="to">Кінець огляду.</param>
    /// <remarks>
    /// ⚠ Делегує в <see cref="GapFinder"/>, який живе в <c>Ecr.Application</c>.
    /// Те саме питання — «що таке дірка» — ставить і екран джерел, який будує
    /// <c>Ecr.Infrastructure</c>; проєкти сусідні, і без спільного місця тут
    /// з'явилася б друга реалізація, яка розійшлася б із цією мовчки
    /// (`A7-03`).
    /// </remarks>
    public static IReadOnlyList<(DateTime From, DateTime To)> FindGaps(
        IReadOnlyList<TimeInterval> covered, DateTime from, DateTime to)
        => GapFinder.Find(covered, from, to);
}
