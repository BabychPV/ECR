using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// ✎ g4 (координатор 08.11, «State filter, counters and the Needs recalculation chip count for the selected year only»):
/// застарілість результатів за РІК — документ застарілий за рік, якщо він застарілий хоч в одному періоді року.
/// </summary>
/// <remarks>
/// ⛔ Нового предиката тут НЕМАЄ: рік — це ОБ'ЄДНАННЯ місячних карт (<see cref="StaleResultsMapCache"/>) і, при
/// переповненні карти, місячних запитів <see cref="StaleResultsQuery.Documents"/>; «відколи» — найраніший момент серед
/// місяців. Беруться лише періоди року з актуальним (<c>Current</c>) прогоном: без нього застарілості в періоді не буває
/// (<c>StaleResultsQuery</c> починається з актуального прогону), тож решта місяців дала б лише порожні карти й зайві
/// запити відбитка. Видимість (гранти, звуження, D-446) — як і за місяць, у викликача.
/// </remarks>
internal static class StaleResultsYear
{
    /// <summary>Стеля кількості періодів року (ключ <c>yyyyMM</c>: їх щонайбільше 99).</summary>
    private const int MaxPeriodsInYear = 100;

    /// <summary>Періоди року з актуальним прогоном — одним запитом.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="year">Рік.</param>
    /// <param name="projectId">
    /// Фільтр запиту за проєктом: лише його прогони (карти решти місяців не потрібні); <c>null</c> — прогони всіх проєктів
    /// (видимість однаково застосовує викликач по документах).
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<int[]> PeriodKeysAsync(EcrDbContext db, int year, int? projectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var from = year * 100;
        var to = (year + 1) * 100;

        return await db.CalculationRuns
            .AsNoTracking()
            .Where(r => r.PeriodKey > from && r.PeriodKey < to && r.Status == "Current"
                        && (projectId == null || r.ProjectId == projectId))
            .Select(r => r.PeriodKey!.Value)
            .Distinct()
            .OrderBy(k => k)
            .Take(MaxPeriodsInYear)
            .ToArrayAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Карти року для кількох фільтрів автора (як <see cref="StaleResultsMapCache.GetAsync"/>): елемент — об'єднання
    /// місячних карт; <c>null</c> — хоч одна місячна карта переповнена, викликач бере прямий запит.
    /// </summary>
    /// <param name="maps">Кеш місячних карт.</param>
    /// <param name="periodKeys">Періоди року (<see cref="PeriodKeysAsync"/>).</param>
    /// <param name="byUserIds">Фільтри автора: <c>null</c> — будь-який автор.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<IReadOnlyList<IReadOnlyDictionary<long, DateTime?>?>> GetAsync(
        StaleResultsMapCache maps, IReadOnlyList<int> periodKeys, IReadOnlyList<int?> byUserIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(periodKeys);
        ArgumentNullException.ThrowIfNull(byUserIds);

        var union = new Dictionary<long, DateTime?>?[byUserIds.Count];
        for (var i = 0; i < union.Length; i++)
        {
            union[i] = [];
        }

        foreach (var periodKey in periodKeys)
        {
            var monthly = await maps.GetAsync(periodKey, byUserIds, ct).ConfigureAwait(false);
            for (var i = 0; i < union.Length; i++)
            {
                if (union[i] is not { } into)
                {
                    continue;
                }

                if (monthly[i] is not { } month)
                {
                    union[i] = null;
                    continue;
                }

                Merge(into, month);
            }
        }

        return union;
    }

    /// <summary>Додає місячну карту до річної: документ — раз, момент — найраніший.</summary>
    /// <param name="into">Річна карта.</param>
    /// <param name="month">Місячна карта.</param>
    public static void Merge(Dictionary<long, DateTime?> into, IReadOnlyDictionary<long, DateTime?> month)
    {
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(month);

        foreach (var (documentId, since) in month)
        {
            if (!into.TryGetValue(documentId, out var known))
            {
                into[documentId] = since;
            }
            else if (since is { } candidate && (known is null || candidate < known))
            {
                into[documentId] = candidate;
            }
        }
    }

    /// <summary>
    /// Документи, застарілі хоч в одному з періодів, — підзапитом (переповнена карта): об'єднання
    /// <see cref="StaleResultsQuery.Documents"/> по місяцях. Без періодів — порожньо.
    /// </summary>
    /// <param name="db">Контекст.</param>
    /// <param name="periodKeys">Періоди року.</param>
    /// <param name="byUserId">Лише правки цього користувача; <c>null</c> — будь-які.</param>
    public static IQueryable<long> Documents(EcrDbContext db, IReadOnlyList<int> periodKeys, int? byUserId = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(periodKeys);

        if (periodKeys.Count == 0)
        {
            return db.Documents.AsNoTracking().Where(_ => false).Select(d => d.Id);
        }

        return periodKeys
            .Select(k => StaleResultsQuery.Documents(db, k, byUserId).Select(r => r.DocumentId))
            .Aggregate(Queryable.Concat);
    }
}
