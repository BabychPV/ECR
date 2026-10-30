// src/Ecr.Application/Ports/IRegistryHierarchyStore.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;

namespace Ecr.Application.Ports;

/// <summary>Persistence of registry levels and the tree reads of hierarchical registries (NAE plan N1).</summary>
public interface IRegistryHierarchyStore
{
    /// <summary>Levels of a registry by ordinal; tracked (the save handler edits them).</summary>
    /// <param name="registryDefId">Registry.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<IReadOnlyList<RegistryLevelDef>> ListLevelsAsync(int registryDefId, CancellationToken ct);

    /// <summary>Starts tracking a new level.</summary>
    /// <param name="level">Level.</param>
    public void AddLevel(RegistryLevelDef level);

    /// <summary>Marks a level for removal.</summary>
    /// <param name="level">Level.</param>
    public void RemoveLevel(RegistryLevelDef level);

    /// <summary>
    /// Takes the tree lock of the registry until the transaction ends (<c>UPDLOCK</c> on the definition row — the same row
    /// move/retire write), so a placement validated afterwards cannot be invalidated by a concurrent tree change.
    /// </summary>
    /// <param name="registryDefId">Registry.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task LockTreeAsync(int registryDefId, CancellationToken ct);

    /// <summary>Entries per level id, soft-deleted ones included (they keep the level FK, so they pin the level).</summary>
    /// <param name="registryDefId">Registry.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<IReadOnlyDictionary<int, int>> CountEntriesByLevelAsync(int registryDefId, CancellationToken ct);

    /// <summary>All live (not deleted) entries of the registry, untracked, ordered by (parent, ordinal, code).</summary>
    /// <param name="registryDefId">Registry.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<IReadOnlyList<RegistryEntry>> ListLiveEntriesAsync(int registryDefId, CancellationToken ct);

    /// <summary>Live direct children of a node, TRACKED, ordered by (ordinal, code).</summary>
    /// <param name="registryDefId">Registry.</param>
    /// <param name="parentEntryId">Parent; <c>null</c> — roots.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<IReadOnlyList<RegistryEntry>> ListChildrenTrackedAsync(
        int registryDefId, long? parentEntryId, CancellationToken ct);
}
