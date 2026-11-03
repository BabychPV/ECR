// src/Ecr.Application/Registries/Nae/NaeTreeNodePolicy.cs
using System.Globalization;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Registries.Nae;

/// <summary>
/// Typed NAE tree nodes on the manual path (human 03.11, quiz): what the generic entry upsert and move add for the NAE tree.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Q1: every node needs an English name (<c>422 naeNameEnRequired</c>); Russian and Kazakh are optional.</item>
/// <item>Q3: <c>NaeCode</c> is required on SRC (the field is bound to SRC and required — the writer's
/// <c>requiredFieldsMissing</c>), normalised like the AF/Excel loads, and unique within (ancestor REGION, ancestor AREA), or
/// within REGION where the region has no AREA (<c>409 ECR-REG-4094 naeCodeTaken</c>).</item>
/// <item><c>RegionCode</c> / <c>AreaCode</c> are derived from the ancestors by the server; <c>AfPath</c> is written only by the
/// AF load. Client input for the three is ignored.</item>
/// <item>A move (or an upsert with another parent) that would change the region/area of an NAE source below the node is refused
/// (<c>422 naeMoveChangesScope</c>): REGION+AREA is part of the NAE's business identity that permits point at.</item>
/// </list>
/// The registry is recognised by its shape (<see cref="IsNaeTree"/>), not by the configured code: the AF load may write any code.
/// </remarks>
public sealed class NaeTreeNodePolicy(IRegistryStore registries, IRegistryHierarchyStore hierarchy, IClock clock)
{
    /// <summary>A node without an English name.</summary>
    internal const string NameEnRequiredKey = "err.ECR-REG-0422.naeNameEnRequired";

    /// <summary>The NAE number is taken in its region/area.</summary>
    internal const string CodeTakenKey = "err.ECR-REG-4094.naeCodeTaken";

    /// <summary>A move would change the region/area of a source.</summary>
    internal const string MoveChangesScopeKey = "err.ECR-REG-0422.naeMoveChangesScope";

    /// <summary>An NAE source changed into another node type.</summary>
    internal const string SourceLevelChangeKey = "err.ECR-REG-0422.naeSourceLevelChange";

    /// <summary>
    /// A level change OUT of SRC is refused (<c>422 naeSourceLevelChange</c>): the source is the object permits point at, and
    /// its SRC-only values (number, region/area, type, point) would otherwise sit on a group node. To stop being a source the
    /// node is retired and a group created instead. A level change INTO SRC goes through <see cref="SourceValuesAsync"/>
    /// (move) or the normal upsert rules (number required and free, derived region/area).
    /// </summary>
    /// <param name="levels">Levels of the tree.</param>
    /// <param name="oldLevelId">Level before the change.</param>
    /// <param name="newLevel">Level after it.</param>
    /// <param name="entryCode">Entry code (detail).</param>
    public static void EnsureNotLeavingSource(IReadOnlyList<RegistryLevelDef> levels, int? oldLevelId, RegistryLevelDef newLevel, string entryCode)
    {
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(newLevel);
        var old = levels.FirstOrDefault(l => l.Id == oldLevelId);
        if (old is not null && IsSource(old) && !IsSource(newLevel))
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"NAE source {entryCode} cannot become a {newLevel.Code} node; retire it and create the group instead.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = SourceLevelChangeKey,
                    ["entryCode"] = entryCode,
                    ["levelCode"] = newLevel.Code,
                });
        }
    }

    /// <summary>Whether a level is the NAE source level.</summary>
    /// <param name="level">Level.</param>
    public static bool IsSource(RegistryLevelDef level)
    {
        ArgumentNullException.ThrowIfNull(level);
        return string.Equals(level.Code, NaeTreeSchema.LevelSource, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A move INTO SRC (the move carries no values): the node's stored NAE number is required (<c>422 requiredFieldsMissing</c>
    /// otherwise), and the values to write are prepared like an upsert's — English name, normalised number, derived region/area.
    /// </summary>
    /// <param name="definition">The NAE tree.</param>
    /// <param name="entry">Moved entry.</param>
    /// <param name="level">New level (SRC).</param>
    /// <param name="parentId">New parent.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyDictionary<string, object?>> SourceValuesAsync(
        RegistryDef definition, RegistryEntry entry, RegistryLevelDef level, long? parentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(entry);

        var codeField = definition.Fields.FirstOrDefault(f => string.Equals(f.Code, NaeTreeSchema.NaeCodeField, StringComparison.OrdinalIgnoreCase));
        var stored = codeField is null
            ? null
            : (await registries.ListValuesAsync(entry.Id, ct).ConfigureAwait(false)).FirstOrDefault(v => v.RegistryFieldDefId == codeField.Id)?.ValueString;
        if (string.IsNullOrWhiteSpace(stored))
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Entry {entry.Code} has no NAE number: it cannot become an NAE source by a move. Edit it with the number instead.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0422.requiredFieldsMissing",
                    ["registryCode"] = definition.Code,
                    ["fields"] = NaeTreeSchema.NaeCodeField,
                });
        }

        return await PrepareAsync(
                definition, entry, level, parentId, new Dictionary<string, object?> { [NaeTreeSchema.NaeCodeField] = stored }, ct)
            .ConfigureAwait(false);
    }

    /// <summary>A hierarchical registry with the NAE business fields and the REGION/SRC levels' fields is an NAE tree.</summary>
    /// <param name="definition">Registry.</param>
    public static bool IsNaeTree(RegistryDef definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.IsHierarchical
               && new[] { NaeTreeSchema.NaeCodeField, NaeTreeSchema.RegionField, NaeTreeSchema.AreaField }.All(code =>
                   definition.Fields.Any(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)
                                              && f.DataType == CellDataType.String));
    }

    /// <summary>
    /// Before the values are written: English name required, server-owned fields dropped from the input, the NAE code normalised
    /// and, on SRC, the derived region/area set from the new ancestors.
    /// </summary>
    /// <param name="definition">The NAE tree.</param>
    /// <param name="entry">Entry with its final names.</param>
    /// <param name="level">Its (validated) level.</param>
    /// <param name="parentId">Its (validated) parent.</param>
    /// <param name="values">Values from the request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The values to write.</returns>
    public async Task<IReadOnlyDictionary<string, object?>> PrepareAsync(
        RegistryDef definition,
        RegistryEntry entry,
        RegistryLevelDef level,
        long? parentId,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(values);

        if (!entry.DisplayL10n.Values.Any(v => string.Equals(v.Key, "en", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v.Value)))
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                "An NAE tree node needs an English name; Russian and Kazakh are optional.",
                new Dictionary<string, object?> { ["messageKey"] = NameEnRequiredKey, ["levelCode"] = level.Code });
        }

        var result = values
            .Where(v => !NaeTreeSchema.ReadOnlyFields.Contains(v.Key))
            .ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

        if (result.TryGetValue(NaeTreeSchema.NaeCodeField, out var raw)
            && NaeCodeNormalizer.Normalize(CellValueReader.Normalize(raw)) is { } normalized)
        {
            result[NaeTreeSchema.NaeCodeField] = normalized;
        }

        // Region/area codes (decision 03.11): written by the client only on the node's OWN level (REGION: RegionCode, AREA:
        // AreaCode, normalised like the permits scope); everywhere else the input is ignored.
        foreach (var field in new[] { NaeTreeSchema.RegionField, NaeTreeSchema.AreaField })
        {
            if (!result.TryGetValue(field, out var scopeRaw))
            {
                continue;
            }

            var own = string.Equals(NaeTreeSchema.OwnLevelOfScopeField(field), level.Code, StringComparison.OrdinalIgnoreCase);
            var text = own ? Permits.AirLimits.AirLimitsSchema.NormalizeScope(CellValueReader.Normalize(scopeRaw) as string, "") : "";
            if (text.Length == 0)
            {
                _ = result.Remove(field);
            }
            else
            {
                result[field] = text;
            }
        }

        if (string.Equals(level.Code, NaeTreeSchema.LevelSource, StringComparison.OrdinalIgnoreCase))
        {
            var tree = await TreeAsync(definition.Id, ct).ConfigureAwait(false);
            var (region, area) = await ScopeCodesAsync(definition, ScopeOf(parentId, tree), tree, ct).ConfigureAwait(false);
            result[NaeTreeSchema.RegionField] = region;
            result[NaeTreeSchema.AreaField] = area;
        }

        return result;
    }

    /// <summary>
    /// Region/area codes of a scope: the stored <c>RegionCode</c> of the REGION node / <c>AreaCode</c> of the AREA node when set,
    /// else derived from the technical code (<see cref="NaeTreeSchema.DerivedRegion"/> / <see cref="NaeTreeSchema.DerivedArea"/>) —
    /// the same rule as the air scope-codes list, so a source and its region group never disagree.
    /// </summary>
    private async Task<(string Region, string Area)> ScopeCodesAsync(
        RegistryDef definition, (long? Region, long? Area) scope, Tree tree, CancellationToken ct)
    {
        var ids = new List<long>();
        if (scope.Region is { } ri)
        {
            ids.Add(ri);
        }

        if (scope.Area is { } ai)
        {
            ids.Add(ai);
        }

        var stored = new Dictionary<(long Entry, string Field), string>();
        if (ids.Count > 0)
        {
            var fieldCodeById = definition.Fields.ToDictionary(f => f.Id, f => f.Code);
            foreach (var v in await registries.ListValuesForEntriesAsync(ids, ct).ConfigureAwait(false))
            {
                if (fieldCodeById.TryGetValue(v.RegistryFieldDefId, out var fieldCode) && v.ValueString is { } text)
                {
                    stored[(v.RegistryEntryId, fieldCode)] = text;
                }
            }
        }

        string Stored(long? id, string field)
            => id is { } i && stored.TryGetValue((i, field), out var s) ? Permits.AirLimits.AirLimitsSchema.NormalizeScope(s, "") : "";

        var region = scope.Region is { } r
            ? Stored(r, NaeTreeSchema.RegionField) is { Length: > 0 } sr ? sr : NaeTreeSchema.DerivedRegion(tree.Nodes[r].Code)
            : "";
        var area = scope.Area is { } a
            ? Stored(a, NaeTreeSchema.AreaField) is { Length: > 0 } sa ? sa : NaeTreeSchema.DerivedArea(tree.Nodes[a].Code, region)
            : NaeAfTreeBuilder.NoArea;
        return (region, area);
    }

    /// <summary>
    /// Inside the write transaction, after the tree lock: the NAE code of an SRC node is free in its region/area.
    /// </summary>
    /// <param name="definition">The NAE tree.</param>
    /// <param name="entry">Entry (new or existing).</param>
    /// <param name="level">Its level.</param>
    /// <param name="parentId">Its parent.</param>
    /// <param name="values">Prepared values (<see cref="PrepareAsync"/>); a code missing there is read from the stored entry.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="BusinessRuleException"><c>409 ECR-REG-4094 naeCodeTaken</c>.</exception>
    public async Task EnsureCodeFreeAsync(
        RegistryDef definition,
        RegistryEntry entry,
        RegistryLevelDef level,
        long? parentId,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(values);

        if (!string.Equals(level.Code, NaeTreeSchema.LevelSource, StringComparison.OrdinalIgnoreCase)
            || definition.Fields.FirstOrDefault(f => string.Equals(f.Code, NaeTreeSchema.NaeCodeField, StringComparison.OrdinalIgnoreCase)) is not { } codeField)
        {
            return;
        }

        string? code;
        if (values.TryGetValue(NaeTreeSchema.NaeCodeField, out var raw))
        {
            code = CellValueReader.Normalize(raw) as string;
        }
        else
        {
            code = entry.IsPersisted
                ? (await registries.ListValuesAsync(entry.Id, ct).ConfigureAwait(false)).FirstOrDefault(v => v.RegistryFieldDefId == codeField.Id)?.ValueString
                : null;
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return; // the writer refuses a missing required code
        }

        var tree = await TreeAsync(definition.Id, ct).ConfigureAwait(false);
        var scope = ScopeOf(parentId, tree);
        var today = DateOnly.FromDateTime(clock.UtcNow);
        var rivals = tree.Nodes.Values
            .Where(n => n.Id != entry.Id
                        && n.LevelId == tree.SourceLevelId
                        && n.IsActive
                        && (n.ValidTo is not { } to || to > today)
                        && ScopeOf(n.Id, tree) == scope)
            .ToDictionary(n => n.Id);
        if (rivals.Count == 0)
        {
            return;
        }

        var taken = (await registries.ListValuesForEntriesAsync([.. rivals.Keys], ct).ConfigureAwait(false))
            .FirstOrDefault(v => v.RegistryFieldDefId == codeField.Id
                                 && string.Equals(v.ValueString?.Trim(), code.Trim(), StringComparison.OrdinalIgnoreCase));
        if (taken is null)
        {
            return;
        }

        var (region, area) = await ScopeCodesAsync(definition, scope, tree, ct).ConfigureAwait(false);
        var holder = rivals[taken.RegistryEntryId];
        throw new BusinessRuleException(
            ErrorCodes.NaeCodeTaken,
            $"NAE {code} is already used in {region}/{(scope.Area is null ? "-" : area)} by entry {holder.Code}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = CodeTakenKey,
                ["naeCode"] = code,
                ["regionCode"] = region,
                ["areaCode"] = area,
                ["entryId"] = holder.Id.ToString(CultureInfo.InvariantCulture),
                ["entryCode"] = holder.Code,
            });
    }

    /// <summary>
    /// A move of <paramref name="entryId"/> under <paramref name="newParentId"/> keeps the region/area of every NAE source in its
    /// subtree (the node itself included).
    /// </summary>
    /// <param name="definition">The NAE tree.</param>
    /// <param name="entryId">Moved node.</param>
    /// <param name="newParentId">New parent.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="BusinessRuleException"><c>422 naeMoveChangesScope</c>.</exception>
    public async Task EnsureMoveKeepsScopeAsync(RegistryDef definition, long entryId, long? newParentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var tree = await TreeAsync(definition.Id, ct).ConfigureAwait(false);
        if (!tree.Nodes.TryGetValue(entryId, out var moved) || moved.ParentEntryId == newParentId)
        {
            return;
        }

        var children = tree.Nodes.Values.Where(n => n.ParentEntryId != null).ToLookup(n => n.ParentEntryId!.Value);
        var subtree = new List<RegistryEntry> { moved };
        for (var i = 0; i < subtree.Count && i < 100_000; i++)
        {
            subtree.AddRange(children[subtree[i].Id]);
        }

        var moved2 = tree with { ParentOverride = (entryId, newParentId) };
        foreach (var source in subtree.Where(n => n.LevelId == tree.SourceLevelId))
        {
            if (ScopeOf(source.Id, tree) != ScopeOf(source.Id, moved2))
            {
                throw new BusinessRuleException(
                    "ECR-REG-0422",
                    $"Moving entry {moved.Code} would change the region/area of NAE source {source.Code}.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = MoveChangesScopeKey,
                        ["entryCode"] = moved.Code,
                        ["sourceCode"] = source.Code,
                    });
            }
        }
    }

    private async Task<Tree> TreeAsync(int registryDefId, CancellationToken ct)
    {
        var levels = await hierarchy.ListLevelsAsync(registryDefId, ct).ConfigureAwait(false);
        int? LevelId(string code) => levels.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))?.Id;
        var nodes = (await hierarchy.ListLiveEntriesAsync(registryDefId, ct).ConfigureAwait(false)).ToDictionary(e => e.Id);
        return new Tree(nodes, LevelId(NaeTreeSchema.LevelRegion), LevelId(NaeTreeSchema.LevelArea), LevelId(NaeTreeSchema.LevelSource), null);
    }

    /// <summary>Nearest REGION and AREA at or above <paramref name="startId"/> (the node itself included).</summary>
    private static (long? Region, long? Area) ScopeOf(long? startId, Tree tree)
    {
        long? region = null;
        long? area = null;
        var cursor = startId;
        for (var step = 0; cursor is { } id && step <= 64 && tree.Nodes.TryGetValue(id, out var node); step++)
        {
            if (area is null && node.LevelId == tree.AreaLevelId)
            {
                area = id;
            }

            if (node.LevelId == tree.RegionLevelId)
            {
                region = id;
                break;
            }

            cursor = tree.ParentOverride is { } o && o.Id == id ? o.ParentId : node.ParentEntryId;
        }

        return (region, area);
    }

    private sealed record Tree(
        IReadOnlyDictionary<long, RegistryEntry> Nodes,
        int? RegionLevelId,
        int? AreaLevelId,
        int? SourceLevelId,
        (long Id, long? ParentId)? ParentOverride);
}
