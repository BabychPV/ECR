// src/Ecr.Infrastructure/Persistence/RegistryRowsQuery.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Реалізація <see cref="IRegistryRowsQuery"/> над <see cref="EcrDbContext"/>: <c>dic.*</c> поточні
/// або <c>FOR SYSTEM_TIME AS OF</c> момент (RT-13, <c>D-158</c>).
/// </summary>
/// <remarks>
/// ⚠ Версія рядка — найпізніший <c>PeriodStart</c> запису та його значень (<c>D-166</c>): правка
/// значення не змінює рядка запису, і версія лише запису її не помітила б.
/// </remarks>
public sealed class RegistryRowsQuery(EcrDbContext db) : IRegistryRowsQuery
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<RegistryEntry>> ListEntriesAsync(
        int registryDefId, DateTime? asOfUtc, CancellationToken ct)
        => await Entries(asOfUtc)
            .Where(e => e.RegistryDefId == registryDefId)
            .ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RegistryRowValue>> ListFieldValuesAsync(
        IReadOnlyCollection<int> registryFieldDefIds, DateTime? asOfUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registryFieldDefIds);
        if (registryFieldDefIds.Count == 0)
        {
            return [];
        }

        var fields = registryFieldDefIds.Distinct().ToList();
        return await Project(Values(asOfUtc).Where(v => fields.Contains(v.RegistryFieldDefId)))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RegistryRowsSlice> ReadRowsAsync(
        IReadOnlyCollection<long> registryEntryIds, DateTime? asOfUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registryEntryIds);
        if (registryEntryIds.Count == 0)
        {
            return new RegistryRowsSlice([], new Dictionary<long, DateTime>(), []);
        }

        var ids = registryEntryIds.Distinct().ToList();

        var values = await Project(Values(asOfUtc).Where(v => ids.Contains(v.RegistryEntryId)))
            .ToListAsync(ct).ConfigureAwait(false);

        var entryStarts = await Entries(asOfUtc)
            .Where(e => ids.Contains(e.Id))
            .Select(e => new { e.Id, Start = EF.Property<DateTime>(e, RegistryEntryConfiguration.PeriodStart) })
            .ToListAsync(ct).ConfigureAwait(false);

        var valueStarts = await Values(asOfUtc)
            .Where(v => ids.Contains(v.RegistryEntryId))
            .GroupBy(v => v.RegistryEntryId)
            .Select(g => new { Id = g.Key, Start = g.Max(v => EF.Property<DateTime>(v, RegistryEntryConfiguration.PeriodStart)) })
            .ToListAsync(ct).ConfigureAwait(false);

        var versions = entryStarts.ToDictionary(e => e.Id, e => DateTime.SpecifyKind(e.Start, DateTimeKind.Utc));
        foreach (var value in valueStarts)
        {
            var start = DateTime.SpecifyKind(value.Start, DateTimeKind.Utc);
            if (!versions.TryGetValue(value.Id, out var current) || start > current)
            {
                versions[value.Id] = start;
            }
        }

        // Назви цілей Lookup — того самого моменту, що й значення.
        var targets = values.Where(v => v.RefEntryId is not null).Select(v => v.RefEntryId!.Value).Distinct().ToList();
        IReadOnlyList<RegistryEntry> referenced = targets.Count == 0
            ? []
            : await Entries(asOfUtc).Where(e => targets.Contains(e.Id)).ToListAsync(ct).ConfigureAwait(false);

        return new RegistryRowsSlice(values, versions, referenced);
    }

    /// <inheritdoc />
    public async Task<RegistryEntryHistorySlice> ReadEntryHistoryAsync(long registryEntryId, CancellationToken ct)
    {
        // ⛔ FOR SYSTEM_TIME ALL: історію пише сама база на КОЖНОМУ шляху запису (ручний, пакет, CSV,
        // синк, прямий SQL — R-9), а автора версії ставить UnitOfWork (D-158). Журнал aud.* цього не
        // дає: частина шляхів пише туди лише підсумок на весь файл або нічого.
        var entries = (await db.RegistryEntries.TemporalAll().AsNoTracking()
                .Where(e => e.Id == registryEntryId)
                .Select(e => new
                {
                    From = EF.Property<DateTime>(e, RegistryEntryConfiguration.PeriodStart),
                    To = EF.Property<DateTime>(e, RegistryEntryConfiguration.PeriodEnd),
                    e.ChangedByUserId,
                    e.DisplayL10n,
                    e.ValidFrom,
                    e.ValidTo,
                    e.IsActive,
                    e.IsDeleted,
                })
                .ToListAsync(ct).ConfigureAwait(false))
            .Where(e => e.From != e.To)
            .Select(e => new RegistryEntryVersion(
                Utc(e.From), Utc(e.To), e.ChangedByUserId, e.DisplayL10n, e.ValidFrom, e.ValidTo, e.IsActive, e.IsDeleted))
            .OrderBy(e => e.FromUtc)
            .ToList();

        var values = (await (
                    from v in db.RegistryValues.TemporalAll().AsNoTracking()
                    where v.RegistryEntryId == registryEntryId
                    join u in db.Units on v.ValueUnitId equals (int?)u.Id into units
                    from u in units.DefaultIfEmpty()
                    select new
                    {
                        v.Id,
                        From = EF.Property<DateTime>(v, RegistryEntryConfiguration.PeriodStart),
                        To = EF.Property<DateTime>(v, RegistryEntryConfiguration.PeriodEnd),
                        v.ChangedByUserId,
                        Value = new RegistryRowValue(
                            v.RegistryEntryId,
                            v.RegistryFieldDefId,
                            v.ValueNumeric,
                            v.ValueString,
                            v.ValueDate,
                            v.ValueBool,
                            v.ValueRefEntryId,
                            v.ValueUnitId,
                            u == null ? null : u.Code),
                    })
                .ToListAsync(ct).ConfigureAwait(false))
            .Where(v => v.From != v.To)
            .Select(v => new RegistryValueVersion(v.Id, Utc(v.From), Utc(v.To), v.ChangedByUserId, v.Value))
            .OrderBy(v => v.FromUtc)
            .ThenBy(v => v.ValueId)
            .ToList();

        var authors = entries.Select(e => e.ChangedByUserId)
            .Concat(values.Select(v => v.ChangedByUserId))
            .OfType<int>()
            .Distinct()
            .ToList();
        var names = authors.Count == 0
            ? new Dictionary<int, string>()
            : await db.Users.AsNoTracking()
                .Where(u => authors.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct).ConfigureAwait(false);

        var targets = values.Select(v => v.Value.RefEntryId).OfType<long>().Distinct().ToList();
        IReadOnlyList<RegistryEntry> referenced = targets.Count == 0
            ? []
            : await Entries(null).Where(e => targets.Contains(e.Id)).ToListAsync(ct).ConfigureAwait(false);

        return new RegistryEntryHistorySlice(entries, values, names, referenced);
    }

    /// <inheritdoc />
    public async Task<VisibleEntryPage> PageVisibleEntriesAsync(
        VisibleEntriesFilter filter, long after, int take, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var visible = Visible(filter);
        var total = await visible.CountAsync(ct).ConfigureAwait(false);
        var items = await visible.Where(e => e.Id > after).OrderBy(e => e.Id).Take(take)
            .ToListAsync(ct).ConfigureAwait(false);
        return new VisibleEntryPage(items, total);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RegistryEntrySlim>> ListVisibleSlimAsync(
        VisibleEntriesFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return await Visible(filter).OrderBy(e => e.Id)
            .Select(e => new RegistryEntrySlim(e.Id, e.Code, e.DisplayL10n))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RegistryRowValue>> ListTextMatchesAsync(
        IReadOnlyCollection<int> registryFieldDefIds, string contains, DateTime? asOfUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registryFieldDefIds);
        if (registryFieldDefIds.Count == 0)
        {
            return [];
        }

        var fields = registryFieldDefIds.Distinct().ToList();
        return await Plain(Values(asOfUtc).Where(v =>
                fields.Contains(v.RegistryFieldDefId)
                && v.ValueString != null
                && EF.Functions.Collate(v.ValueString, LooseCollation).Contains(contains)))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RegistryRowValue>> ListEqualMatchesAsync(
        int registryFieldDefId, Domain.Enums.CellDataType type, string canonical, DateTime? asOfUtc, CancellationToken ct)
    {
        var values = Values(asOfUtc).Where(v => v.RegistryFieldDefId == registryFieldDefId);
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        switch (type)
        {
            case Domain.Enums.CellDataType.String:
                values = values.Where(v => v.ValueString != null
                    && EF.Functions.Collate(v.ValueString, LooseCollation) == canonical);
                break;
            case Domain.Enums.CellDataType.Int or Domain.Enums.CellDataType.Decimal:
                var number = decimal.Parse(canonical, System.Globalization.NumberStyles.Number, ci);
                values = values.Where(v => v.ValueNumeric == number);
                break;
            case Domain.Enums.CellDataType.Bool:
                var flag = bool.Parse(canonical);
                values = values.Where(v => v.ValueBool == flag);
                break;
            case Domain.Enums.CellDataType.Date:
                var from = DateTime.ParseExact(canonical, "yyyy-MM-dd", ci);
                var to = from.AddDays(1);
                values = values.Where(v => v.ValueDate >= from && v.ValueDate < to);
                break;
            case Domain.Enums.CellDataType.Lookup:
                var target = long.Parse(canonical, ci);
                values = values.Where(v => v.ValueRefEntryId == target);
                break;
            case Domain.Enums.CellDataType.Unit:
                var unit = int.Parse(canonical, ci);
                values = values.Where(v => v.ValueUnitId == unit);
                break;
            default:
                return [];
        }

        return await Plain(values).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Без регістру й діакритики: ширше за <c>OrdinalIgnoreCase</c>, тож SQL дає НАДМНОЖИНУ, а не
    /// залежить від колації бази (у «чутливій» базі точний збіг без цього губив би рядки).
    /// </summary>
    private const string LooseCollation = "Latin1_General_100_CI_AI";

    /// <summary>Значення без з'єднання з одиницями — лише типізовані колонки.</summary>
    private static IQueryable<RegistryRowValue> Plain(IQueryable<RegistryValue> values)
        => values.Select(v => new RegistryRowValue(
            v.RegistryEntryId, v.RegistryFieldDefId, v.ValueNumeric, v.ValueString, v.ValueDate,
            v.ValueBool, v.ValueRefEntryId, v.ValueUnitId, null));

    /// <summary>Видимі записи плоского довідника: те саме правило, що <c>RegistryResolver.Select</c>.</summary>
    private IQueryable<RegistryEntry> Visible(VisibleEntriesFilter filter)
    {
        var asOf = filter.AsOf;
        var query = Entries(filter.AsOfUtc).Where(e =>
            e.RegistryDefId == filter.RegistryDefId
            && e.IsActive && !e.IsDeleted
            && (e.ValidFrom == null || e.ValidFrom <= asOf)
            && (e.ValidTo == null || asOf < e.ValidTo));

        if (filter.CascadeParentId is { } parent)
        {
            var allowed = filter.CascadeAllowed.ToList();
            query = query.Where(e => allowed.Contains(e.Id) || e.ParentEntryId == parent);
        }

        if (filter.EntryIds is { } ids)
        {
            var wanted = ids.ToList();
            query = query.Where(e => wanted.Contains(e.Id));
        }

        return query;
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>Значення в типізованих колонках разом із кодом одиниці.</summary>
    private IQueryable<RegistryRowValue> Project(IQueryable<RegistryValue> values)
        => from v in values
           join u in db.Units on v.ValueUnitId equals (int?)u.Id into units
           from u in units.DefaultIfEmpty()
           orderby v.Id
           select new RegistryRowValue(
               v.RegistryEntryId,
               v.RegistryFieldDefId,
               v.ValueNumeric,
               v.ValueString,
               v.ValueDate,
               v.ValueBool,
               v.ValueRefEntryId,
               v.ValueUnitId,
               u == null ? null : u.Code);

    /// <summary>Записи «станом на» момент; <c>null</c> — поточні.</summary>
    private IQueryable<RegistryEntry> Entries(DateTime? asOfUtc)
        => asOfUtc is { } asOf
            ? db.RegistryEntries.TemporalAsOf(asOf).AsNoTracking()
            : db.RegistryEntries.AsNoTracking();

    /// <summary>Значення «станом на» той самий момент, що й записи.</summary>
    private IQueryable<RegistryValue> Values(DateTime? asOfUtc)
        => asOfUtc is { } asOf
            ? db.RegistryValues.TemporalAsOf(asOf).AsNoTracking()
            : db.RegistryValues.AsNoTracking();
}
