using Ecr.Application.Ports;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IPermitListStore"/> над <see cref="EcrDbContext"/> (Permits, PM-08e).</summary>
public sealed class PermitListStore(EcrDbContext db) : IPermitListStore
{
    // Стовпців зі списками у всіх типах - десятки-сотні (межа складу типу 200 стовпців); стеля захищає від вибірки без меж.
    private const int MaxRows = 20_000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<PermitListUsage>> ListUsagesAsync(CancellationToken ct)
    {
        // Один запит: надбудова поля -> поле -> таблиця типу -> тип; реєстр списку лівим джойном. DraftJson не читається:
        // проєкція бере лише названі стовпці (інакше кожен тип тягнув би чернетку до 2 Мі символів).
        var rows = await (
            from meta in db.PermitFieldMetas.AsNoTracking()
            join field in db.RegistryFieldDefs.AsNoTracking() on meta.RegistryFieldDefId equals field.Id
            join table in db.PermitTypeTables.AsNoTracking()
                on new { meta.PermitTypeDefId, RegistryId = field.RegistryDefId }
                equals new { table.PermitTypeDefId, RegistryId = table.RegistryDefId }
            join type in db.PermitTypeDefs.AsNoTracking() on meta.PermitTypeDefId equals type.Id
            join list in db.RegistryDefs.AsNoTracking() on meta.ListRegistryDefId equals (int?)list.Id into lists
            from list in lists.DefaultIfEmpty()
            where meta.ListScope != null && type.Status == PermitTypeStatus.Published
            orderby type.Code, table.Ordinal, field.Ordinal
            select new
            {
                TypeId = type.Id,
                TypeCode = type.Code,
                TypeName = type.NameL10n,
                type.HeaderRegistryDefId,
                TableCode = table.Code,
                TableName = table.NameL10n,
                FieldCode = field.Code,
                FieldName = field.NameL10n,
                Scope = meta.ListScope!.Value,
                meta.ListRegistryDefId,
                ListRegistryCode = list == null ? null : list.Code,
                ListRegistryName = list == null ? null : list.NameL10n,
                SiblingTableCode = meta.ListSiblingTableCode,
            })
            .Take(MaxRows)
            .ToListAsync(ct).ConfigureAwait(false);

        return [.. rows.Select(r => new PermitListUsage(
            r.TypeId, r.TypeCode, r.TypeName, r.HeaderRegistryDefId, r.TableCode, r.TableName, r.FieldCode, r.FieldName,
            r.Scope, r.ListRegistryDefId, r.ListRegistryCode, r.ListRegistryName, r.SiblingTableCode))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, int>> CountActiveEntriesAsync(
        IReadOnlyCollection<int> registryDefIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registryDefIds);

        if (registryDefIds.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        return await db.RegistryEntries.AsNoTracking()
            .Where(e => registryDefIds.Contains(e.RegistryDefId) && e.IsActive && !e.IsDeleted)
            .GroupBy(e => e.RegistryDefId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<RegistryEntry>>> ListTopActiveEntriesAsync(
        IReadOnlyCollection<int> registryDefIds, int perRegistry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registryDefIds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perRegistry);

        if (registryDefIds.Count == 0)
        {
            return new Dictionary<int, IReadOnlyList<RegistryEntry>>();
        }

        // Один запит: для кожного реєстру - TOP(perRegistry) чинних записів за (Ordinal, Id) (EF: CROSS APPLY (SELECT TOP(@n) ...)).
        // Раніше був корельований COUNT «скільки записів перед цим» - O(N²) на реєстр (4509 записів = 14 с); тепер
        // кожен реєстр читається один раз і відсікається після perRegistry рядків (реєстр НСІ - до 20 тис. записів).
        var rows = await db.RegistryDefs.AsNoTracking()
            .Where(d => registryDefIds.Contains(d.Id))
            .SelectMany(d => db.RegistryEntries
                .Where(e => e.RegistryDefId == d.Id && e.IsActive && !e.IsDeleted)
                .OrderBy(e => e.Ordinal).ThenBy(e => e.Id)
                .Take(perRegistry))
            .ToListAsync(ct).ConfigureAwait(false);
        return rows
            .GroupBy(e => e.RegistryDefId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<RegistryEntry>)[.. g.OrderBy(e => e.Ordinal).ThenBy(e => e.Id)]);
    }
}
