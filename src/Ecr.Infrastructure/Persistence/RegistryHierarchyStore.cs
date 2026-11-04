using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>EF implementation of <see cref="IRegistryHierarchyStore"/>.</summary>
/// <remarks>
/// The child/subtree reads ride <c>IX_RegistryEntry_Parent</c> (RegistryDefId, ParentEntryId, filtered, covering);
/// the per-level counts ride <c>IX_RegistryEntry_Level</c>.
/// </remarks>
public sealed class RegistryHierarchyStore(EcrDbContext db) : IRegistryHierarchyStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<RegistryLevelDef>> ListLevelsAsync(int registryDefId, CancellationToken ct)
        => await db.RegistryLevelDefs
                   .Where(l => l.RegistryDefId == registryDefId)
                   .OrderBy(l => l.Ordinal)
                   .ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task LockTreeAsync(int registryDefId, CancellationToken ct)
        => _ = await db.Database
            .ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cfg.RegistryDef WITH (UPDLOCK, ROWLOCK) WHERE Id = {registryDefId}", ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public void AddLevel(RegistryLevelDef level) => db.RegistryLevelDefs.Add(level);

    /// <inheritdoc />
    public void RemoveLevel(RegistryLevelDef level) => db.RegistryLevelDefs.Remove(level);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, int>> CountEntriesByLevelAsync(int registryDefId, CancellationToken ct)
        => await db.RegistryEntries
                   .AsNoTracking()
                   .Where(e => e.RegistryDefId == registryDefId && e.LevelId != null)
                   .GroupBy(e => e.LevelId!.Value)
                   .Select(g => new { g.Key, Count = g.Count() })
                   .ToDictionaryAsync(x => x.Key, x => x.Count, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RegistryEntry>> ListLiveEntriesAsync(int registryDefId, CancellationToken ct)
        => await db.RegistryEntries
                   .AsNoTracking()
                   .Where(e => e.RegistryDefId == registryDefId && !e.IsDeleted)
                   .OrderBy(e => e.ParentEntryId)
                   .ThenBy(e => e.Ordinal)
                   .ThenBy(e => e.Code)
                   .ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RegistryEntry>> ListChildrenTrackedAsync(
        int registryDefId, long? parentEntryId, CancellationToken ct)
    {
        // EF translates the long? -> int? conversion of ParentEntryId through the value converter.
        return parentEntryId is { } parent
            ? await db.RegistryEntries
                      .Where(e => e.RegistryDefId == registryDefId && e.ParentEntryId == parent && !e.IsDeleted)
                      .OrderBy(e => e.Ordinal).ThenBy(e => e.Code)
                      .ToListAsync(ct).ConfigureAwait(false)
            : await db.RegistryEntries
                      .Where(e => e.RegistryDefId == registryDefId && e.ParentEntryId == null && !e.IsDeleted)
                      .OrderBy(e => e.Ordinal).ThenBy(e => e.Code)
                      .ToListAsync(ct).ConfigureAwait(false);
    }
}
