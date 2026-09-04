using Ecr.Application.Ports;

namespace Ecr.Adapters.PiAf;

/// <summary>
/// Планує дозбір пропущених інтервалів за журналом покриття.
/// </summary>
/// <remarks>
/// <c>Watermark</c> у розкладі — **оптимізація, а не стан** (ER-I-03): його
/// втрата не має коштувати даних, тому справжнім джерелом істини є
/// <c>itg.CollectionCoverage</c>.
/// </remarks>
public sealed class CatchUpPlanner(ICollectionStore store)
{
    /// <summary>Знаходить непокриті інтервали за період lookback.</summary>
    public Task<IReadOnlyList<(DateTime From, DateTime To)>> PlanAsync(
        int sourceEntityId, DateTime notBefore, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: узяти інтервали через store.GetCoverageAsync(sourceEntityId, notBefore), " +
            "злити суміжні, знайти прогалини " +
            "від notBefore до тепер. Повертати впорядковано від найстаршої прогалини: " +
            "спершу закриваємо давнє, бо саме воно потрібне для звітності.");
}
