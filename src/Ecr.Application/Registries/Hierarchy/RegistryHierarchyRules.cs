// src/Ecr.Application/Registries/Hierarchy/RegistryHierarchyRules.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Registries.Hierarchy;

/// <summary>A node as the placement rules see it.</summary>
/// <param name="Id">Entry id.</param>
/// <param name="ParentId">Parent id.</param>
/// <param name="LevelId">Level id.</param>
public readonly record struct HierarchyNode(long Id, long? ParentId, int? LevelId);

/// <summary>
/// Placement rules of a hierarchical registry (NAE plan N1), pure functions so one rule serves upsert, move, level save and tests.
/// </summary>
/// <remarks>
/// Rules: a node sits on exactly one declared level; along any path the level ordinal STRICTLY grows; every level skipped
/// between a parent and its child (and above a root) must be optional; no cycles; the depth never exceeds the number of levels
/// (at most <see cref="RegistryLevelDef.MaxLevels"/>), which strict growth guarantees.
/// </remarks>
public static class RegistryHierarchyRules
{
    /// <summary>Checks that <paramref name="level"/> may sit under <paramref name="parentLevel"/> (<c>null</c> — root).</summary>
    /// <param name="levels">All levels of the registry.</param>
    /// <param name="parentLevel">Level of the parent; <c>null</c> for a root.</param>
    /// <param name="level">Level of the node.</param>
    /// <returns><c>null</c> when valid, otherwise the full messageKey (<c>err.ECR-REG-0422.levelOrder</c> or <c>…levelSkipsRequired</c>).</returns>
    public static string? CheckPlacement(
        IReadOnlyCollection<RegistryLevelDef> levels, RegistryLevelDef? parentLevel, RegistryLevelDef level)
    {
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(level);

        var floor = parentLevel?.Ordinal ?? 0;
        if (level.Ordinal <= floor)
        {
            return "err.ECR-REG-0422.levelOrder";
        }

        // Every level strictly between the parent (or the top) and the node must be optional.
        return levels.Any(l => l.Ordinal > floor && l.Ordinal < level.Ordinal && !l.IsOptional)
            ? "err.ECR-REG-0422.levelSkipsRequired"
            : null;
    }

    /// <summary>Would hanging <paramref name="entryId"/> under <paramref name="newParentId"/> create a cycle?</summary>
    /// <param name="entryId">Moved node.</param>
    /// <param name="newParentId">Proposed parent.</param>
    /// <param name="parentOf">Current parent lookup.</param>
    /// <returns><c>true</c> — the parent is the node itself or one of its descendants, or the chain is endless.</returns>
    public static bool WouldCycle(long entryId, long? newParentId, Func<long, long?> parentOf)
    {
        ArgumentNullException.ThrowIfNull(parentOf);

        var cursor = newParentId;
        // The cap makes a corrupt (already cyclic) chain finish; a legal chain is at most MaxLevels long.
        for (var step = 0; cursor is { } id && step <= 64; step++)
        {
            if (id == entryId)
            {
                return true;
            }

            cursor = parentOf(id);
        }

        return cursor is not null;
    }

    /// <summary>Finds every node whose current place breaks the rules (used after a level change and for a moved subtree).</summary>
    /// <param name="levels">Levels.</param>
    /// <param name="nodes">Nodes to check (the whole registry or a subtree); parents outside the set are looked up in <paramref name="all"/>.</param>
    /// <param name="all">Every live node of the registry by id.</param>
    /// <returns>Violations as (node id, full messageKey).</returns>
    public static IReadOnlyList<(long Id, string Reason)> FindViolations(
        IReadOnlyCollection<RegistryLevelDef> levels,
        IEnumerable<HierarchyNode> nodes,
        IReadOnlyDictionary<long, HierarchyNode> all)
    {
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(all);

        var byId = levels.ToDictionary(l => l.Id);
        var violations = new List<(long, string)>();

        foreach (var node in nodes)
        {
            if (node.LevelId is not { } levelId || !byId.TryGetValue(levelId, out var level))
            {
                violations.Add((node.Id, "err.ECR-REG-0422.levelRequired"));
                continue;
            }

            RegistryLevelDef? parentLevel = null;
            if (node.ParentId is { } parentId)
            {
                if (!all.TryGetValue(parentId, out var parent)
                    || parent.LevelId is not { } parentLevelId
                    || !byId.TryGetValue(parentLevelId, out parentLevel))
                {
                    violations.Add((node.Id, "err.ECR-REG-0422.levelRequired"));
                    continue;
                }
            }

            if (CheckPlacement(levels, parentLevel, level) is { } reason)
            {
                violations.Add((node.Id, reason));
            }
        }

        return violations;
    }

    /// <summary>Validates a level list from a save request.</summary>
    /// <param name="levels">Codes, names and flags in ordinal order.</param>
    /// <exception cref="BusinessRuleException"><c>422 ECR-REG-0422 hierarchyLevelsInvalid</c>.</exception>
    public static void EnsureLevelListValid(IReadOnlyList<RegistryLevelInput> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);

        string? problem = null;
        if (levels.Count is < 1 or > RegistryLevelDef.MaxLevels)
        {
            problem = "count";
        }
        else if (levels.Select(l => l.Code?.Trim().ToLowerInvariant()).Distinct().Count() != levels.Count)
        {
            problem = "duplicateCode";
        }
        else if (levels.Any(l => string.IsNullOrWhiteSpace(l.Code)
                                 || l.Name is null
                                 || !l.Name.Values.TryGetValue("en", out var en)
                                 || string.IsNullOrWhiteSpace(en)))
        {
            problem = "codeOrName";
        }

        if (problem is not null)
        {
            throw Invalid(
                "err.ECR-REG-0422.hierarchyLevelsInvalid",
                $"Level list is invalid ({problem}): 1..{RegistryLevelDef.MaxLevels} levels with unique codes and an English name.",
                new Dictionary<string, object?>
                {
                    ["problem"] = problem,
                    ["max"] = RegistryLevelDef.MaxLevels.ToString(CultureInfo.InvariantCulture),
                });
        }
    }

    /// <summary>Builds a <c>422 ECR-REG-0422</c> with a hierarchy messageKey.</summary>
    /// <param name="messageKey">Full catalog key, as a literal at the call site (the catalog guards read literals).</param>
    /// <param name="message">Technical message.</param>
    /// <param name="details">Extra details for the message template.</param>
    public static BusinessRuleException Invalid(string messageKey, string message, IDictionary<string, object?>? details = null)
    {
        var all = new Dictionary<string, object?> { ["messageKey"] = messageKey };
        foreach (var (name, value) in details ?? new Dictionary<string, object?>())
        {
            all[name] = value;
        }

        return new BusinessRuleException("ECR-REG-0422", message, all);
    }

    /// <summary>Builds a <c>409 ECR-REG-0409</c> with a hierarchy messageKey.</summary>
    /// <param name="messageKey">Full catalog key, as a literal at the call site (the catalog guards read literals).</param>
    /// <param name="message">Technical message.</param>
    /// <param name="details">Extra details for the message template.</param>
    public static BusinessRuleException Conflict(string messageKey, string message, IDictionary<string, object?>? details = null)
    {
        var all = new Dictionary<string, object?> { ["messageKey"] = messageKey };
        foreach (var (name, value) in details ?? new Dictionary<string, object?>())
        {
            all[name] = value;
        }

        return new BusinessRuleException("ECR-REG-0409", message, all);
    }
}
