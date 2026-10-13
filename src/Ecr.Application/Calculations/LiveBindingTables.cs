// src/Ecr.Application/Calculations/LiveBindingTables.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Calculations;

/// <summary>
/// Таблиці прив'язок методології, які звіряються з аргументами формул (<c>ECR-CALC-0438</c>): «жива» версія
/// шаблону, а не всі (D-400).
/// </summary>
internal static class LiveBindingTables
{
    /// <summary>TableDefId для звірки колонок: лише таблиці останньої опублікованої версії шаблону.</summary>
    /// <param name="store">Сховище прив'язок.</param>
    /// <param name="methodologyBindings">Усі прив'язки методології (включно з вимкненими).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Підмножина таблиць прив'язок; порожня відповідь сховища — усі (запасний шлях: перевірка не слабшає
    /// там, де живу версію визначити не вдалося).
    /// </returns>
    public static async Task<IReadOnlyCollection<int>> ResolveAsync(
        ICalculationBindingStore store,
        IReadOnlyList<CalculationBinding> methodologyBindings,
        CancellationToken ct)
    {
        var all = methodologyBindings.Select(b => b.TableDefId).Distinct().ToList();
        if (all.Count <= 1)
        {
            return all;
        }

        var live = await store.ListLiveTableDefIdsAsync(all, ct).ConfigureAwait(false);
        var liveSet = live?.Where(all.Contains).Distinct().ToList() ?? [];

        return liveSet.Count == 0 ? all : liveSet;
    }
}
