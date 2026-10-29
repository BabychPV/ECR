// src/Ecr.Application/Registries/Hierarchy/RegistryHierarchyDtos.cs
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Registries.Hierarchy;

/// <summary>One declared level of a hierarchical registry (NAE plan N1).</summary>
/// <param name="Id">Level id (stable while the level code is kept).</param>
/// <param name="Ordinal">1-based depth: roots sit on the lowest ordinal present on their path.</param>
/// <param name="Code">Level code, unique within the registry.</param>
/// <param name="Name">Level name, all languages.</param>
/// <param name="IsOptional">A node may skip this level (child on a deeper level than <c>parent + 1</c>).</param>
/// <param name="EntryCount">Live entries placed on this level.</param>
public sealed record RegistryLevelDto(
    int Id,
    int Ordinal,
    string Code,
    LocalizedText Name,
    bool IsOptional,
    int EntryCount);

/// <summary>Level in a save request; its position in the list is its ordinal.</summary>
/// <param name="Code">Level code (EcrCode); an existing code keeps the level id.</param>
/// <param name="Name">Name; <c>en</c> is required.</param>
/// <param name="IsOptional">A node may skip this level.</param>
public sealed record RegistryLevelInput(string Code, LocalizedText Name, bool IsOptional);

/// <summary>Hierarchy settings of a registry.</summary>
/// <param name="RegistryCode">Registry code.</param>
/// <param name="IsHierarchical">Entries form a tree with declared levels.</param>
/// <param name="Levels">Levels ordered by ordinal (empty for a flat registry).</param>
/// <param name="MaxDepth">Hard limit on the number of levels (and so on the tree depth).</param>
public sealed record RegistryHierarchyDto(
    string RegistryCode,
    bool IsHierarchical,
    IReadOnlyList<RegistryLevelDto> Levels,
    int MaxDepth);

/// <summary>Save hierarchy settings: all-or-nothing replacement of the level list.</summary>
/// <param name="IsHierarchical"><c>false</c> is accepted only when no entry has a level.</param>
/// <param name="Levels">1..<c>MaxDepth</c> levels when hierarchical; empty otherwise.</param>
public sealed record SaveRegistryHierarchyRequest(
    bool IsHierarchical,
    IReadOnlyList<RegistryLevelInput> Levels);

/// <summary>One node of the tree (flat list; the client assembles the tree by <c>ParentEntryId</c>).</summary>
/// <param name="Id">Entry id.</param>
/// <param name="ParentEntryId">Parent; <c>null</c> for a root.</param>
/// <param name="LevelCode">Level code; <c>null</c> for an entry that has no level yet.</param>
/// <param name="Code">Entry code.</param>
/// <param name="Display">Name in the reader's language.</param>
/// <param name="Ordinal">Position among siblings.</param>
/// <param name="ValidFrom">First valid day.</param>
/// <param name="ValidTo">First NOT valid day (exclusive).</param>
/// <param name="IsActive">Entry is switched on.</param>
/// <param name="ChildCount">Live direct children (all, regardless of the filter).</param>
/// <param name="IsMatch">The node itself matches the <c>q</c> filter (ancestors of matches are included, unmarked).</param>
public sealed record RegistryTreeNodeDto(
    long Id,
    long? ParentEntryId,
    string? LevelCode,
    string Code,
    string Display,
    int Ordinal,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    bool IsActive,
    int ChildCount,
    bool IsMatch);

/// <summary>The tree of a hierarchical registry.</summary>
/// <param name="RegistryCode">Registry code.</param>
/// <param name="IsHierarchical">Registry is hierarchical.</param>
/// <param name="Levels">Levels by ordinal.</param>
/// <param name="Nodes">Nodes ordered by (parent, ordinal, code).</param>
/// <param name="DataRevision">Registry data revision (cache key).</param>
public sealed record RegistryTreeDto(
    string RegistryCode,
    bool IsHierarchical,
    IReadOnlyList<RegistryLevelDto> Levels,
    IReadOnlyList<RegistryTreeNodeDto> Nodes,
    int DataRevision);

/// <summary>Move an entry (re-parent and/or re-level and/or re-order).</summary>
/// <param name="ParentEntryId">New parent; <c>null</c> — make it a root.</param>
/// <param name="LevelCode">New level; <c>null</c> — keep the current one.</param>
/// <param name="BeforeEntryId">Place before this sibling; <c>null</c> — last among siblings.</param>
public sealed record MoveRegistryEntryRequest(
    long? ParentEntryId,
    string? LevelCode,
    long? BeforeEntryId);

/// <summary>Result of a move.</summary>
/// <param name="Id">Entry id.</param>
/// <param name="ParentEntryId">Parent after the move.</param>
/// <param name="LevelCode">Level after the move.</param>
/// <param name="Ordinal">Ordinal among siblings after the move.</param>
public sealed record MoveRegistryEntryResponse(
    long Id,
    long? ParentEntryId,
    string LevelCode,
    int Ordinal);

/// <summary>Retire a node (soft: closes the validity window, never deletes).</summary>
/// <param name="On">First day the node is NOT valid (exclusive); <c>null</c> — today.</param>
/// <param name="Cascade">Retire the whole live subtree; without it a node with live children is refused.</param>
public sealed record RetireRegistryEntryRequest(DateOnly? On, bool Cascade);

/// <summary>Result of a retire.</summary>
/// <param name="RetiredIds">Entries whose window was closed (node first, then descendants).</param>
/// <param name="AffectedRows">Document rows flagged as orphaned by the closed windows.</param>
public sealed record RetireRegistryEntryResponse(
    IReadOnlyList<long> RetiredIds,
    int AffectedRows);
