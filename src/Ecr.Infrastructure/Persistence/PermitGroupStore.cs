using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IPermitGroupStore"/> над <see cref="EcrDbContext"/> (Permits, PRMG).</summary>
public sealed class PermitGroupStore(EcrDbContext db) : IPermitGroupStore
{
    // Груп десятки; стеля захищає від вибірки без меж.
    private const int MaxRows = 5_000;

    // Записів-дозволів у складі групи (усі типи разом).
    private const int MaxMemberEntries = 200_000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<PermitGroup>> ListAsync(CancellationToken ct)
        => await db.PermitGroups.AsNoTracking().Include(g => g.Members)
            .OrderBy(g => g.Code).Take(MaxRows)
            .ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PermitGroupBrief>> ListBriefAsync(CancellationToken ct)
        => await db.PermitGroups.AsNoTracking()
            .OrderBy(g => g.Id).Take(MaxRows)
            .Select(g => new PermitGroupBrief(g.RegistryDefId, g.IsActive, g.DescriptionL10n))
            .ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public Task<PermitGroup?> FindByCodeAsync(string code, CancellationToken ct)
        => db.PermitGroups.Include(g => g.Members).FirstOrDefaultAsync(g => g.Code == code, ct);

    /// <inheritdoc />
    public void Add(PermitGroup group) => db.PermitGroups.Add(group);

    /// <inheritdoc />
    public void RemoveMembersNotIn(PermitGroup group, IReadOnlyCollection<int> keepPermitTypeDefIds)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(keepPermitTypeDefIds);

        db.PermitGroupMembers.RemoveRange(group.Members.Where(m => !keepPermitTypeDefIds.Contains(m.PermitTypeDefId)).ToList());
    }

    /// <inheritdoc />
    public async Task<bool> IsReferencedByTemplatesAsync(int registryDefId, CancellationToken ct)
        => await db.ColumnDefs.AsNoTracking().AnyAsync(c => c.LookupRegistryDefId == registryDefId, ct).ConfigureAwait(false)
           || await db.HeaderFieldDefs.AsNoTracking().AnyAsync(f => f.LookupRegistryDefId == registryDefId, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task RemoveAsync(PermitGroup group, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(group);

        var registry = await db.RegistryDefs.FirstOrDefaultAsync(d => d.Id == group.RegistryDefId, ct).ConfigureAwait(false);

        // Усі FK - Restrict: спершу склад, потім група, потім реєстр-«ідентичність».
        db.PermitGroupMembers.RemoveRange(group.Members.ToList());
        db.PermitGroups.Remove(group);
        if (registry is not null)
        {
            db.RegistryDefs.Remove(registry);
        }
    }

    /// <inheritdoc />
    public async Task<PermitGroupSnapshot> LoadSnapshotAsync(CancellationToken ct)
    {
        var revision = await GetSnapshotRevisionAsync(ct).ConfigureAwait(false);

        var groups = await db.PermitGroups.AsNoTracking()
            .OrderBy(g => g.Id).Take(MaxRows)
            .Select(g => new { g.Id, g.RegistryDefId, g.IsActive })
            .Take(MaxRows)
            .ToListAsync(ct).ConfigureAwait(false);

        // Реєстри шапок типів складу - одним запитом (join складу з типами), порядок - за позицією у складі.
        var members = await (
                from m in db.PermitGroupMembers.AsNoTracking()
                join t in db.PermitTypeDefs.AsNoTracking() on m.PermitTypeDefId equals t.Id
                orderby m.PermitGroupId, m.Ordinal
                select new { m.PermitGroupId, t.HeaderRegistryDefId })
            .Take(MaxRows * 100)
            .ToListAsync(ct).ConfigureAwait(false);

        var byGroup = members.GroupBy(m => m.PermitGroupId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<int>)[.. g.Select(m => m.HeaderRegistryDefId)]);

        return new PermitGroupSnapshot(
            revision,
            [.. groups.Select(g => new PermitGroupSnapshotItem(
                g.RegistryDefId, g.IsActive, byGroup.GetValueOrDefault(g.Id) ?? []))]);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Ecr.Domain.Entities.Dictionaries.RegistryEntry>> ListMemberEntriesAsync(
        IReadOnlyCollection<int> registryDefIds, DateOnly windowStart, DateOnly windowEndInclusive, CancellationToken ct)
    {
        if (registryDefIds.Count == 0)
        {
            return [];
        }

        // Дозволи типів складу (десятки-сотні на тип): стеля захищає від вибірки без меж. Дата відсікається в SQL:
        // запис [ValidFrom, ValidTo) перетинає вікно [windowStart, windowEndInclusive], коли ValidFrom <= кінця і ValidTo > початку.
        return await db.RegistryEntries.AsNoTracking()
            .Where(e => registryDefIds.Contains(e.RegistryDefId) && !e.IsDeleted && e.IsActive
                        && (e.ValidFrom == null || e.ValidFrom <= windowEndInclusive)
                        && (e.ValidTo == null || e.ValidTo > windowStart))
            .OrderBy(e => e.RegistryDefId).ThenBy(e => e.Code)
            .Take(MaxMemberEntries)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, int>> GetDataRevisionsAsync(IReadOnlyCollection<int> registryDefIds, CancellationToken ct)
    {
        if (registryDefIds.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        return await db.RegistryDefs.AsNoTracking()
            .Where(d => registryDefIds.Contains(d.Id))
            .OrderBy(d => d.Id)
            .Select(d => new { d.Id, d.DataRevision })
            .Take(MaxRows)
            .ToDictionaryAsync(x => x.Id, x => x.DataRevision, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> GetSnapshotRevisionAsync(CancellationToken ct)
    {
        // Кочування хешу по парах (Id групи, DataRevision реєстру) в порядку Id: видалення однієї групи й створення іншої з тим самим
        // внеском більше не дає ту саму ревізію (раніше SUM(DataRevision+1) міг збігтися). Груп десятки - рядки малі.
        var rows = await db.PermitGroups.AsNoTracking()
            .Join(db.RegistryDefs.AsNoTracking(), g => g.RegistryDefId, d => d.Id, (g, d) => new { g.Id, d.DataRevision })
            .OrderBy(x => x.Id)
            .Take(MaxRows)
            .ToListAsync(ct).ConfigureAwait(false);

        var hash = 17L;
        foreach (var row in rows)
        {
            hash = unchecked((hash * 31) + (row.Id * 1_000_003L) + row.DataRevision + 1);
        }

        return hash;
    }
}
