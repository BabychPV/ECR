using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Expressions.Evaluation;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Реалізація <see cref="IRegistrySnapshotLoader"/> над <see cref="EcrDbContext"/>: записи й
/// значення <c>dic.*</c> — <c>FOR SYSTEM_TIME AS OF</c> моменту прогону (RT-22, <c>D-158</c>).
/// </summary>
/// <remarks>
/// ⛔ <b>Кількість запитів стала</b> — п'ять, хоч би скільки було довідників і записів
/// (<c>D-162</c>): ребра <c>Lookup</c> (для замикання переліку), описи з полями, первинні
/// ключі, записи, значення. Перелік довідників іде одним параметром (<c>OPENJSON</c>), а не
/// запитом на кожен.
///
/// ⚠ Фільтра видимості в SQL немає навмисно: її застосовує <see cref="RegistrySnapshot"/>
/// умовою пікера (<see cref="RegistryResolver.IsSelectable"/>). Друге, «майже таке саме»
/// формулювання вікна в SQL розійшлося б із ним рівно на межі дня. Цього коштує кілька
/// зайвих рядків закритих і видалених записів — на FLERT одиниці відсотків.
///
/// ⚠ Опис довідника (<c>cfg.*</c>) системної історії не має — він читається поточним. Склад
/// полів, змінений після прогону, дає <c>#REF</c> на зниклому полі, як і задумано (§5.5).
/// </remarks>
public sealed class RegistrySnapshotLoader(EcrDbContext db) : IRegistrySnapshotLoader
{
    /// <inheritdoc />
    public async Task<IRegistrySnapshot> LoadAsync(
        IReadOnlyCollection<int> registryDefIds,
        DateOnly businessDate,
        DateTime? registryAsOfUtc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registryDefIds);

        if (registryDefIds.Count == 0)
        {
            return RegistrySnapshot.Empty;
        }

        var ids = await CloseOverLookupsAsync(registryDefIds, ct).ConfigureAwait(false);

        var registries = await db.RegistryDefs.AsNoTracking()
            .Include(d => d.Fields)
            .Where(d => ids.Contains(d.Id))
            .ToListAsync(ct).ConfigureAwait(false);

        var primaryKeys = await db.RegistryKeyDefs.AsNoTracking()
            .Include(k => k.Fields)
            .Where(k => ids.Contains(k.RegistryDefId) && k.IsPrimary && k.IsActive)
            .ToListAsync(ct).ConfigureAwait(false);

        var entries = await Entries(registryAsOfUtc)
            .Where(e => ids.Contains(e.RegistryDefId))
            .ToListAsync(ct).ConfigureAwait(false);

        var values = await (
                from e in Entries(registryAsOfUtc)
                where ids.Contains(e.RegistryDefId)
                join v in Values(registryAsOfUtc) on e.Id equals v.RegistryEntryId
                join u in db.Units on v.ValueUnitId equals (int?)u.Id into units
                from u in units.DefaultIfEmpty()
                select new RegistrySnapshotValue(
                    v.RegistryEntryId,
                    v.RegistryFieldDefId,
                    v.ValueNumeric,
                    v.ValueString,
                    v.ValueDate,
                    v.ValueBool,
                    v.ValueRefEntryId,
                    v.ValueUnitId,
                    u == null ? null : u.Code))
            .ToListAsync(ct).ConfigureAwait(false);

        return RegistrySnapshot.Create(registries, primaryKeys, entries, values, businessDate);
    }

    /// <summary>
    /// Записи «станом на» момент прогону; <c>null</c> — поточні (прогін до системної історії).
    /// </summary>
    private IQueryable<RegistryEntry> Entries(DateTime? asOfUtc)
        => asOfUtc is { } asOf
            ? db.RegistryEntries.TemporalAsOf(asOf).AsNoTracking()
            : db.RegistryEntries.AsNoTracking();

    /// <summary>Значення «станом на» той самий момент, що й записи.</summary>
    private IQueryable<RegistryValue> Values(DateTime? asOfUtc)
        => asOfUtc is { } asOf
            ? db.RegistryValues.TemporalAsOf(asOf).AsNoTracking()
            : db.RegistryValues.AsNoTracking();

    /// <summary>
    /// Додає до переліку цілі <c>Lookup</c>-полів, транзитивно: шлях <c>ROW.COMPONENT.MW</c> і
    /// видимість батька композиції читають довідник, якого формула прямо не називає.
    /// </summary>
    /// <remarks>
    /// Ребра читаються одним запитом (їх — десятки на всю систему), замикання — у пам'яті.
    /// </remarks>
    private async Task<List<int>> CloseOverLookupsAsync(IReadOnlyCollection<int> registryDefIds, CancellationToken ct)
    {
        var edges = await db.RegistryFieldDefs.AsNoTracking()
            .Where(f => f.DataType == CellDataType.Lookup && f.RefRegistryDefId != null)
            .Select(f => new { f.RegistryDefId, Target = f.RefRegistryDefId!.Value })
            .ToListAsync(ct).ConfigureAwait(false);

        var targets = edges
            .GroupBy(e => e.RegistryDefId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Target).ToList());

        var closed = new HashSet<int>(registryDefIds);
        var pending = new Queue<int>(closed);
        while (pending.TryDequeue(out var registryDefId))
        {
            if (!targets.TryGetValue(registryDefId, out var next))
            {
                continue;
            }

            foreach (var target in next.Where(closed.Add))
            {
                pending.Enqueue(target);
            }
        }

        return [.. closed.Order()];
    }
}
