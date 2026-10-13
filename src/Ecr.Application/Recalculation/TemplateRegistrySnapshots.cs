using System.Collections.Concurrent;
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Recalculation;

/// <summary>
/// Знімки довідників для форм <c>REG*</c> у формулах шаблону (LP-40): коди з літералів цілей →
/// ідентифікатори → джерело знімка → знімок на бізнес-дату періоду.
/// </summary>
/// <remarks>
/// ⚠ Читання БД — раз на набір довідників (<see cref="IRegistrySnapshotLoader.LoadSourceAsync"/>); знімок
/// на дату (видимість — вікно чинності на ОСТАННІЙ день періоду, не «сьогодні») будується в пам'яті раз на
/// (набір, дата). Документ-рік із 12 місяцями — одне читання БД, а не дванадцять і тим паче не одне на
/// комірку чи рядок.
///
/// ⚠ Живе стільки, скільки екземпляр <c>RecalculationService</c> (scoped: задача перерахунку чи HTTP-запит):
/// зміна довідника ПІД ЧАС того самого скоупу знімок не оновить. Задачі й запити довідник і перерахунок
/// у одному скоупі не змішують (перерахунок після зміни довідника ставиться окремою задачею).
///
/// ⛔ Невдале завантаження з кешу прибирається: наступний виклик спробує знову, а не отримає чужий виняток.
/// </remarks>
internal sealed class TemplateRegistrySnapshots
{
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<int>>>> _ids = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<IRegistrySnapshotSource>>> _sources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IRegistrySnapshot> _built = new(StringComparer.Ordinal);

    /// <summary>
    /// Знімок для цілей прогону; <c>null</c> — жодна ціль не вживає <c>REG*</c>, довідників не знайдено або
    /// завантажувача немає (тоді <c>REG*</c> дає <c>#REF</c>, як до LP-40).
    /// </summary>
    /// <param name="targets">Корені розібраних формул-цілей прогону.</param>
    /// <param name="businessDate">Бізнес-дата — останній день періоду документа.</param>
    /// <param name="loader">Завантажувач знімків; <c>null</c> — немає.</param>
    /// <param name="registryStore">Сховище довідників — розв'язання кодів.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IRegistrySnapshot?> GetAsync(
        IEnumerable<AstNode> targets,
        DateOnly businessDate,
        IRegistrySnapshotLoader? loader,
        IRegistryStore registryStore,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(registryStore);

        if (loader is null)
        {
            return null;
        }

        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in targets)
        {
            TemplateRegistryUse.Scan(root, codes);
        }

        if (codes.Count == 0)
        {
            return null;
        }

        var codesKey = string.Join(',', codes.Select(c => c.ToUpperInvariant()).Order(StringComparer.Ordinal));

        var ids = await CachedAsync(
                _ids, codesKey,
                async () =>
                {
                    var definitions = await registryStore.FindDefinitionsAsync([.. codes], ct).ConfigureAwait(false);
                    return (IReadOnlyList<int>)[.. definitions.Select(d => d.Id).Distinct().Order()];
                })
            .ConfigureAwait(false);

        if (ids.Count == 0)
        {
            return null;
        }

        var idsKey = string.Join(',', ids);
        var source = await CachedAsync(
                _sources, idsKey,
                () => loader.LoadSourceAsync(ids, registryAsOfUtc: null, ct))
            .ConfigureAwait(false);

        var builtKey = idsKey + "|" + businessDate.ToString("O", CultureInfo.InvariantCulture);
        return _built.GetOrAdd(builtKey, _ => source.Build(businessDate));
    }

    private static async Task<T> CachedAsync<T>(
        ConcurrentDictionary<string, Lazy<Task<T>>> cache, string key, Func<Task<T>> load)
    {
        var entry = cache.GetOrAdd(key, _ => new Lazy<Task<T>>(load));
        try
        {
            return await entry.Value.ConfigureAwait(false);
        }
        catch
        {
            cache.TryRemove(new KeyValuePair<string, Lazy<Task<T>>>(key, entry));
            throw;
        }
    }
}
