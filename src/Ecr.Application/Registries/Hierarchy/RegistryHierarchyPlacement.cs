// src/Ecr.Application/Registries/Hierarchy/RegistryHierarchyPlacement.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Registries.Hierarchy;

/// <summary>
/// The one place that validates where an entry may sit (upsert, move and level save all go through it), against FRESH data.
/// </summary>
internal static class RegistryHierarchyPlacement
{
    /// <summary>Finds a level by code (case-insensitive) or throws <c>422 levelUnknown</c>.</summary>
    internal static RegistryLevelDef LevelByCode(IReadOnlyList<RegistryLevelDef> levels, string? code)
        => levels.FirstOrDefault(l => string.Equals(l.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase))
           ?? throw RegistryHierarchyRules.Invalid(
               "err.ECR-REG-0422.levelUnknown",
               $"Level «{code}» is not declared for this registry.",
               new Dictionary<string, object?> { ["levelCode"] = code ?? string.Empty });

    /// <summary>Throws <c>422 levelRequired</c>: a hierarchical registry entry has no level.</summary>
    internal static BusinessRuleException LevelRequired()
        => RegistryHierarchyRules.Invalid(
            "err.ECR-REG-0422.levelRequired", "An entry of a hierarchical registry needs a level (levelCode).");

    /// <summary>Validates the place of an entry: parent level, order, skipped levels, cycle and the place of its subtree.</summary>
    /// <param name="store">Hierarchy store.</param>
    /// <param name="registryDefId">Registry.</param>
    /// <param name="levels">Levels of the registry.</param>
    /// <param name="entryId">Entry; <c>null</c> for a new one (no subtree, no cycle possible).</param>
    /// <param name="newParentId">Proposed parent; <c>null</c> — root.</param>
    /// <param name="level">Proposed level of the entry.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="BusinessRuleException"><c>422 ECR-REG-0422</c>.</exception>
    internal static async Task EnsureAsync(
        IRegistryHierarchyStore store,
        int registryDefId,
        IReadOnlyList<RegistryLevelDef> levels,
        long? entryId,
        long? newParentId,
        RegistryLevelDef level,
        CancellationToken ct)
    {
        var live = await store.ListLiveEntriesAsync(registryDefId, ct).ConfigureAwait(false);
        var all = live.ToDictionary(e => e.Id, e => new HierarchyNode(e.Id, e.ParentEntryId, e.LevelId));

        RegistryLevelDef? parentLevel = null;
        if (newParentId is { } parentId)
        {
            if (!all.TryGetValue(parentId, out var parent)
                || parent.LevelId is not { } parentLevelId
                || levels.FirstOrDefault(l => l.Id == parentLevelId) is not { } found)
            {
                throw LevelRequired();
            }

            parentLevel = found;
        }

        if (entryId is { } id
            && RegistryHierarchyRules.WouldCycle(id, newParentId, x => all.TryGetValue(x, out var n) ? n.ParentId : null))
        {
            throw RegistryHierarchyRules.Invalid(
                "err.ECR-REG-0422.hierarchyCycle",
                $"Entry {id} cannot be placed under its own descendant.",
                new Dictionary<string, object?> { ["entryId"] = id.ToString(CultureInfo.InvariantCulture) });
        }

        if (RegistryHierarchyRules.CheckPlacement(levels, parentLevel, level) is { } reason)
        {
            throw RegistryHierarchyRules.Invalid(
                reason,
                $"Level «{level.Code}» cannot sit under «{parentLevel?.Code ?? "(root)"}».",
                new Dictionary<string, object?>
                {
                    ["levelCode"] = level.Code,
                    ["parentLevelCode"] = parentLevel?.Code ?? string.Empty,
                });
        }

        if (entryId is { } eid)
        {
            all[eid] = new HierarchyNode(eid, newParentId, level.Id);
            var violations = RegistryHierarchyRules.FindViolations(levels, Descendants(all, eid), all);
            if (violations.Count > 0)
            {
                throw RegistryHierarchyRules.Invalid(
                    violations[0].Reason,
                    $"The new place of entry {eid} would break {violations.Count} descendant(s).",
                    new Dictionary<string, object?>
                    {
                        ["entryId"] = violations[0].Id.ToString(CultureInfo.InvariantCulture),
                        ["count"] = violations.Count.ToString(CultureInfo.InvariantCulture),
                    });
            }
        }
    }

    /// <summary>All descendants of a node (breadth-first, node itself excluded).</summary>
    internal static List<HierarchyNode> Descendants(IReadOnlyDictionary<long, HierarchyNode> all, long rootId)
    {
        var children = all.Values.Where(n => n.ParentId != null).ToLookup(n => n.ParentId!.Value);
        var result = new List<HierarchyNode>();
        var queue = new Queue<long>();
        queue.Enqueue(rootId);
        var seen = new HashSet<long> { rootId };

        while (queue.Count > 0)
        {
            foreach (var child in children[queue.Dequeue()])
            {
                if (seen.Add(child.Id))
                {
                    result.Add(child);
                    queue.Enqueue(child.Id);
                }
            }
        }

        return result;
    }
}
