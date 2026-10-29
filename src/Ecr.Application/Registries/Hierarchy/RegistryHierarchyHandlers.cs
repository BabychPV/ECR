// src/Ecr.Application/Registries/Hierarchy/RegistryHierarchyHandlers.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Registries.Hierarchy;

/// <summary>Shared pieces of the hierarchy handlers.</summary>
internal static class HierarchyHandlerSupport
{
    internal static int RequireUser(ICurrentUser currentUser)
        => currentUser.UserId
           ?? throw new AccessDeniedException(
               "ECR-AUTH-0401",
               "Анонімний запит не змінює довідники.",
               new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

    internal static RegistryHierarchyDto Build(
        RegistryDef definition, IReadOnlyList<RegistryLevelDef> levels, IReadOnlyDictionary<int, int> counts)
        => new(
            definition.Code,
            definition.IsHierarchical,
            levels.OrderBy(l => l.Ordinal).Select(l => ToDto(l, counts)).ToList(),
            RegistryLevelDef.MaxLevels);

    internal static RegistryLevelDto ToDto(RegistryLevelDef level, IReadOnlyDictionary<int, int> counts)
        => new(level.Id, level.Ordinal, level.Code, level.NameL10n, level.IsOptional, counts.GetValueOrDefault(level.Id));

    internal static string Reason(string key, int count)
        => JsonSerializer.Serialize(new
        {
            k = key,
            p = new { count = count.ToString(CultureInfo.InvariantCulture) },
        });
}

/// <summary>Reads hierarchy settings of a registry. Permission <c>Registry.View</c> or grant <c>Read</c>.</summary>
public sealed class GetRegistryHierarchyHandler(
    IRegistryStore registries,
    IRegistryHierarchyStore hierarchy,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Permission to read registries.</summary>
    public const string Permission = "Registry.View";

    /// <summary>Hierarchy settings.</summary>
    /// <param name="registryCode">Registry code.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<RegistryHierarchyDto> HandleAsync(string registryCode, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryCode);

        var lookup = new RegistryLookup(registries, registryCode);
        await RegistryAccess.RequireAsync(access, currentUser, Permission, GrantLevel.Read, lookup, ct).ConfigureAwait(false);
        var definition = await lookup.RequireAsync(ct).ConfigureAwait(false);

        return HierarchyHandlerSupport.Build(
            definition,
            await hierarchy.ListLevelsAsync(definition.Id, ct).ConfigureAwait(false),
            await hierarchy.CountEntriesByLevelAsync(definition.Id, ct).ConfigureAwait(false));
    }
}

/// <summary>Saves hierarchy settings. Permission <c>Registry.EditDefinition</c> or grant <c>Write</c>.</summary>
public sealed class SaveRegistryHierarchyHandler(
    IRegistryStore registries,
    IRegistryHierarchyStore hierarchy,
    IUnitOfWork uow,
    IAuditWriter audit,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Permission to change the registry description.</summary>
    public const string Permission = "Registry.EditDefinition";

    /// <summary>Replaces the level list (all-or-nothing).</summary>
    /// <param name="registryCode">Registry code.</param>
    /// <param name="request">New settings.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="BusinessRuleException">
    /// <c>409 levelInUse</c> — a level that holds entries is removed; <c>422</c> — invalid list, or the new order would leave an entry in an invalid place.
    /// </exception>
    public async Task<RegistryHierarchyDto> HandleAsync(
        string registryCode, SaveRegistryHierarchyRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryCode);
        ArgumentNullException.ThrowIfNull(request);

        var lookup = new RegistryLookup(registries, registryCode);
        await RegistryAccess.RequireAsync(access, currentUser, Permission, GrantLevel.Write, lookup, ct).ConfigureAwait(false);
        var userId = HierarchyHandlerSupport.RequireUser(currentUser);
        var definition = await lookup.RequireAsync(ct).ConfigureAwait(false);

        var input = request.Levels ?? [];
        if (request.IsHierarchical)
        {
            RegistryHierarchyRules.EnsureLevelListValid(input);
        }
        else if (input.Count > 0)
        {
            throw RegistryHierarchyRules.Invalid(
                "err.ECR-REG-0422.hierarchyLevelsInvalid",
                "A flat registry has no levels.",
                new Dictionary<string, object?> { ["problem"] = "notHierarchical", ["max"] = "0" });
        }

        var existing = await hierarchy.ListLevelsAsync(definition.Id, ct).ConfigureAwait(false);
        var counts = await hierarchy.CountEntriesByLevelAsync(definition.Id, ct).ConfigureAwait(false);
        var oldJson = LevelsJson(definition.IsHierarchical, existing);
        var originalOrdinal = existing.ToDictionary(l => l.Id, l => l.Ordinal);

        var byCode = existing.ToDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);
        var finals = new List<(RegistryLevelDef? Existing, EcrCode Code, int Ordinal, LocalizedText Name, bool Optional)>();
        for (var i = 0; i < input.Count; i++)
        {
            var code = EcrCode.Create(input[i].Code.Trim());
            finals.Add((byCode.GetValueOrDefault(code.Value), code, i + 1, input[i].Name, input[i].IsOptional));
        }

        var kept = finals.Where(f => f.Existing is not null).Select(f => f.Existing!).ToHashSet();
        var removed = existing.Where(l => !kept.Contains(l)).ToList();
        foreach (var level in removed.Where(l => counts.GetValueOrDefault(l.Id) > 0))
        {
            throw RegistryHierarchyRules.Conflict(
                "err.ECR-REG-0409.levelInUse",
                $"Level «{level.Code}» holds {counts[level.Id]} entr(ies) and cannot be removed.",
                new Dictionary<string, object?>
                {
                    ["levelCode"] = level.Code,
                    ["count"] = counts[level.Id].ToString(CultureInfo.InvariantCulture),
                });
        }

        // Project the new levels in memory, then make sure no placed entry ends up in an invalid place.
        foreach (var f in finals.Where(f => f.Existing is not null))
        {
            f.Existing!.Update(f.Ordinal, f.Name, f.Optional);
        }

        var projected = finals
            .Select(f => f.Existing ?? new RegistryLevelDef(definition.Id, f.Ordinal, f.Code, f.Name, f.Optional))
            .ToList();
        var live = await hierarchy.ListLiveEntriesAsync(definition.Id, ct).ConfigureAwait(false);
        var all = live.ToDictionary(e => e.Id, e => new HierarchyNode(e.Id, e.ParentEntryId, e.LevelId));
        var violations = RegistryHierarchyRules.FindViolations(projected, all.Values.Where(n => n.LevelId != null), all);
        if (violations.Count > 0)
        {
            throw RegistryHierarchyRules.Invalid(
                "err.ECR-REG-0422.hierarchyLevelChange",
                $"The new level order would leave {violations.Count} entr(ies) in an invalid place.",
                new Dictionary<string, object?> { ["count"] = violations.Count.ToString(CultureInfo.InvariantCulture) });
        }

        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                foreach (var level in removed)
                {
                    hierarchy.RemoveLevel(level);
                }

                // Park every level whose ordinal moves outside 1..MaxLevels first: UQ (RegistryDefId, Ordinal) is checked per
                // statement, so a swap would trip it.
                var parkIndex = 0;
                foreach (var f in finals.Where(f => f.Existing is not null && originalOrdinal[f.Existing.Id] != f.Ordinal))
                {
                    f.Existing!.Park(1000 + parkIndex++);
                }

                await uow.SaveChangesAsync(token).ConfigureAwait(false);

                foreach (var f in finals)
                {
                    if (f.Existing is { } ex)
                    {
                        ex.Update(f.Ordinal, f.Name, f.Optional);
                    }
                    else
                    {
                        hierarchy.AddLevel(new RegistryLevelDef(definition.Id, f.Ordinal, f.Code, f.Name, f.Optional));
                    }
                }

                definition.UseHierarchy(request.IsHierarchical);
                definition.BumpDataRevision();
                definition.MarkDefinitionUpdated(userId, clock.UtcNow);
                await uow.SaveChangesAsync(token).ConfigureAwait(false);

                await audit.WriteStructureChangeAsync(
                    new StructureChangeRecord(
                        ChangedAt: clock.UtcNow,
                        TemplateVersionId: 0,
                        EntityType: "cfg.RegistryDef",
                        EntityId: definition.Id,
                        ChangeClass: ChangeClass.Guarded,
                        Operation: "SaveHierarchy",
                        OldJson: oldJson,
                        NewJson: LevelsJson(request.IsHierarchical, input),
                        ChangeReason: HierarchyHandlerSupport.Reason("registryAudit.hierarchySaved", input.Count),
                        ChangedByUserId: userId,
                        CorrelationId: currentUser.CorrelationId),
                    token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        var saved = await hierarchy.ListLevelsAsync(definition.Id, ct).ConfigureAwait(false);
        return HierarchyHandlerSupport.Build(
            definition, saved, await hierarchy.CountEntriesByLevelAsync(definition.Id, ct).ConfigureAwait(false));
    }

    private static string LevelsJson(bool isHierarchical, IEnumerable<RegistryLevelDef> levels)
        => JsonSerializer.Serialize(new
        {
            isHierarchical,
            levels = levels.OrderBy(l => l.Ordinal).Select(l => new { l.Code, l.IsOptional }),
        });

    private static string LevelsJson(bool isHierarchical, IEnumerable<RegistryLevelInput> levels)
        => JsonSerializer.Serialize(new
        {
            isHierarchical,
            levels = levels.Select(l => new { l.Code, l.IsOptional }),
        });
}

/// <summary>Reads the tree. Permission <c>Registry.View</c> or grant <c>Read</c>.</summary>
public sealed class GetRegistryTreeHandler(
    IRegistryStore registries,
    IRegistryHierarchyStore hierarchy,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Permission to read registries.</summary>
    public const string Permission = "Registry.View";

    /// <summary>Flat node list with levels.</summary>
    /// <param name="registryCode">Registry code.</param>
    /// <param name="asOf">Day the nodes must be valid on; <c>null</c> — today (UTC date).</param>
    /// <param name="includeRetired">Also nodes outside the window and switched-off ones.</param>
    /// <param name="query">Substring of code or any name; matches come with their ancestors.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<RegistryTreeDto> HandleAsync(
        string registryCode, DateOnly? asOf, bool includeRetired, string? query, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryCode);

        var lookup = new RegistryLookup(registries, registryCode);
        await RegistryAccess.RequireAsync(access, currentUser, Permission, GrantLevel.Read, lookup, ct).ConfigureAwait(false);
        var definition = await lookup.RequireAsync(ct).ConfigureAwait(false);

        var levels = await hierarchy.ListLevelsAsync(definition.Id, ct).ConfigureAwait(false);
        var counts = await hierarchy.CountEntriesByLevelAsync(definition.Id, ct).ConfigureAwait(false);
        var entries = await hierarchy.ListLiveEntriesAsync(definition.Id, ct).ConfigureAwait(false);

        var day = asOf ?? DateOnly.FromDateTime(clock.UtcNow);
        var byId = entries.ToDictionary(e => e.Id);
        var childCount = entries.Where(e => e.ParentEntryId != null).GroupBy(e => e.ParentEntryId!.Value)
                                .ToDictionary(g => g.Key, g => g.Count());

        // A node is visible only when it and ALL its ancestors pass the validity filter: a retired parent hides its subtree.
        var visibleMemo = new Dictionary<long, bool>();
        bool Visible(RegistryEntry e)
        {
            if (visibleMemo.TryGetValue(e.Id, out var known))
            {
                return known;
            }

            visibleMemo[e.Id] = false; // guards a corrupt cycle
            var self = includeRetired || (e.IsActive && e.IsValidOn(day));
            var parentOk = e.ParentEntryId is not { } p || !byId.TryGetValue(p, out var parent) || Visible(parent);
            return visibleMemo[e.Id] = self && parentOk;
        }

        var visible = entries.Where(Visible).ToList();
        var levelCode = levels.ToDictionary(l => l.Id, l => l.Code);
        string Display(RegistryEntry e) => e.DisplayL10n.Get(currentUser.Language) ?? e.Code;

        var matches = new HashSet<long>();
        var included = visible.Select(e => e.Id).ToHashSet();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var needle = query.Trim();
            matches = visible
                .Where(e => e.Code.Contains(needle, StringComparison.OrdinalIgnoreCase)
                            || e.DisplayL10n.Values.Values.Any(v => v.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                .Select(e => e.Id).ToHashSet();

            included = [];
            foreach (var id in matches)
            {
                for (long? cursor = id; cursor is { } c && included.Add(c) && byId.TryGetValue(c, out var node); cursor = node.ParentEntryId)
                {
                }
            }
        }

        var naeCodes = new Dictionary<long, string>();
        if (definition.Fields.FirstOrDefault(f => string.Equals(f.Code, "NaeCode", StringComparison.OrdinalIgnoreCase)
                                                  && f.DataType == CellDataType.String) is { } naeField)
        {
            var ids = visible.Where(e => included.Contains(e.Id)).Select(e => e.Id).ToList();
            foreach (var v in await registries.ListValuesForEntriesAsync(ids, ct).ConfigureAwait(false))
            {
                if (v.RegistryFieldDefId == naeField.Id && !string.IsNullOrWhiteSpace(v.ValueString))
                {
                    naeCodes[v.RegistryEntryId] = v.ValueString;
                }
            }
        }

        var nodes = visible
            .Where(e => included.Contains(e.Id))
            .Select(e => new RegistryTreeNodeDto(
                e.Id,
                e.ParentEntryId,
                e.LevelId is { } l && levelCode.TryGetValue(l, out var code) ? code : null,
                e.Code,
                Display(e),
                e.Ordinal,
                e.ValidFrom,
                e.ValidTo,
                e.IsActive,
                childCount.GetValueOrDefault(e.Id),
                matches.Contains(e.Id),
                naeCodes.GetValueOrDefault(e.Id)))
            .ToList();

        return new RegistryTreeDto(
            definition.Code,
            definition.IsHierarchical,
            levels.Select(l => HierarchyHandlerSupport.ToDto(l, counts)).ToList(),
            nodes,
            definition.DataRevision);
    }
}

/// <summary>Moves an entry. Permission <c>Registry.EditData</c> or grant <c>Write</c>.</summary>
public sealed class MoveRegistryEntryHandler(
    IRegistryStore registries,
    IRegistryHierarchyStore hierarchy,
    IUnitOfWork uow,
    IAuditWriter audit,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Permission to change registry data.</summary>
    public const string Permission = "Registry.EditData";

    /// <summary>Re-parents, re-levels and re-orders an entry.</summary>
    /// <param name="registryCode">Registry code.</param>
    /// <param name="entryId">Entry.</param>
    /// <param name="request">Target place.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<MoveRegistryEntryResponse> HandleAsync(
        string registryCode, long entryId, MoveRegistryEntryRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryCode);
        ArgumentNullException.ThrowIfNull(request);

        var lookup = new RegistryLookup(registries, registryCode);
        await RegistryAccess.RequireAsync(access, currentUser, Permission, GrantLevel.Write, lookup, ct).ConfigureAwait(false);
        var userId = HierarchyHandlerSupport.RequireUser(currentUser);
        var definition = await lookup.RequireAsync(ct).ConfigureAwait(false);
        ExternalRegistryGuard.EnsureManualEditAllowed(definition);

        if (!definition.IsHierarchical)
        {
            throw RegistryHierarchyRules.Invalid(
                "err.ECR-REG-0422.hierarchyNotEnabled",
                $"Registry «{definition.Code}» is not hierarchical.",
                new Dictionary<string, object?> { ["registryCode"] = definition.Code });
        }

        var entry = await registries.FindEntryAsync(entryId, ct).ConfigureAwait(false);
        if (entry is null || entry.IsDeleted || entry.RegistryDefId != definition.Id)
        {
            throw RegistryAccess.EntryNotFound(entryId, registryCode);
        }

        if (request.ParentEntryId is { } parentId)
        {
            var parent = await registries.FindEntryAsync(parentId, ct).ConfigureAwait(false);
            if (parent is null || parent.IsDeleted || parent.RegistryDefId != definition.Id)
            {
                throw RegistryAccess.EntryNotFound(parentId, registryCode);
            }
        }

        var levels = await hierarchy.ListLevelsAsync(definition.Id, ct).ConfigureAwait(false);
        var level = request.LevelCode is null
            ? levels.FirstOrDefault(l => l.Id == entry.LevelId) ?? throw RegistryHierarchyPlacement.LevelRequired()
            : RegistryHierarchyPlacement.LevelByCode(levels, request.LevelCode);

        var oldParent = entry.ParentEntryId;
        var oldLevel = entry.LevelId;
        var oldOrdinal = entry.Ordinal;
        var ordinal = 0;

        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                // Serialise tree mutations of this registry: writing the definition row takes its lock until commit, so two
                // concurrent moves cannot both pass the cycle check on the same snapshot (A under B while B under A).
                definition.BumpDataRevision();
                await uow.SaveChangesAsync(token).ConfigureAwait(false);

                await RegistryHierarchyPlacement.EnsureAsync(
                    hierarchy, definition.Id, levels, entry.Id, request.ParentEntryId, level, token).ConfigureAwait(false);

                var siblings = (await hierarchy.ListChildrenTrackedAsync(definition.Id, request.ParentEntryId, token)
                                    .ConfigureAwait(false))
                    .Where(s => s.Id != entry.Id).ToList();

                var index = siblings.Count;
                if (request.BeforeEntryId is { } before)
                {
                    index = siblings.FindIndex(s => s.Id == before);
                    if (index < 0)
                    {
                        throw RegistryHierarchyRules.Invalid(
                            "err.ECR-REG-0422.moveBeforeNotSibling",
                            $"Entry {before} is not a child of the target parent.",
                            new Dictionary<string, object?>
                            {
                                ["entryId"] = before.ToString(CultureInfo.InvariantCulture),
                            });
                    }
                }

                siblings.Insert(index, entry);
                entry.SetParent(request.ParentEntryId);
                entry.SetLevel(level.Id);
                for (var i = 0; i < siblings.Count; i++)
                {
                    if (siblings[i].Ordinal != i + 1 || ReferenceEquals(siblings[i], entry))
                    {
                        siblings[i].SetOrdinal(i + 1);
                        siblings[i].MarkChangedBy(userId);
                    }
                }

                ordinal = entry.Ordinal;
                await uow.SaveChangesAsync(token).ConfigureAwait(false);

                await audit.WriteStructureChangeAsync(
                    new StructureChangeRecord(
                        ChangedAt: clock.UtcNow,
                        TemplateVersionId: 0,
                        EntityType: "dic.RegistryEntry",
                        EntityId: checked((int)entry.Id),
                        ChangeClass: ChangeClass.Guarded,
                        Operation: "Move",
                        OldJson: Place(oldParent, oldLevel, oldOrdinal),
                        NewJson: Place(request.ParentEntryId, level.Id, ordinal),
                        ChangeReason: HierarchyHandlerSupport.Reason("registryAudit.entryMoved", siblings.Count),
                        ChangedByUserId: userId,
                        CorrelationId: currentUser.CorrelationId),
                    token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        return new MoveRegistryEntryResponse(entry.Id, entry.ParentEntryId, level.Code, ordinal);
    }

    private static string Place(long? parent, int? level, int ordinal)
        => JsonSerializer.Serialize(new { parentEntryId = parent, levelId = level, ordinal });
}

/// <summary>Retires a node softly. Permission <c>Registry.EditData</c> or grant <c>Write</c>.</summary>
public sealed class RetireRegistryEntryHandler(
    IRegistryStore registries,
    IRegistryHierarchyStore hierarchy,
    IOrphanScanner scanner,
    IUnitOfWork uow,
    IAuditWriter audit,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock,
    Keys.RegistryKeyService? keys = null)
{
    /// <summary>Permission to change registry data.</summary>
    public const string Permission = "Registry.EditData";

    /// <summary>Closes the validity window of the node (and its subtree with <c>Cascade</c>).</summary>
    /// <param name="registryCode">Registry code.</param>
    /// <param name="entryId">Entry.</param>
    /// <param name="request">Retire date and cascade flag.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<RetireRegistryEntryResponse> HandleAsync(
        string registryCode, long entryId, RetireRegistryEntryRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryCode);
        ArgumentNullException.ThrowIfNull(request);

        var lookup = new RegistryLookup(registries, registryCode);
        await RegistryAccess.RequireAsync(access, currentUser, Permission, GrantLevel.Write, lookup, ct).ConfigureAwait(false);
        var userId = HierarchyHandlerSupport.RequireUser(currentUser);
        var definition = await lookup.RequireAsync(ct).ConfigureAwait(false);
        ExternalRegistryGuard.EnsureManualEditAllowed(definition);

        var entry = await registries.FindEntryAsync(entryId, ct).ConfigureAwait(false);
        if (entry is null || entry.IsDeleted || entry.RegistryDefId != definition.Id)
        {
            throw RegistryAccess.EntryNotFound(entryId, registryCode);
        }

        var on = request.On ?? DateOnly.FromDateTime(clock.UtcNow);
        var retired = new List<long>();
        var affected = 0;

        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                // Same serialisation as a move: the definition row lock.
                definition.BumpDataRevision();
                await uow.SaveChangesAsync(token).ConfigureAwait(false);

                // A node is "live" while its window is not closed on or before the retire day.
                static bool Live(RegistryEntry e, DateOnly day) => e.ValidTo is null || e.ValidTo > day;

                var subtree = new List<RegistryEntry> { entry };
                var queue = new Queue<long>();
                queue.Enqueue(entry.Id);
                while (queue.Count > 0)
                {
                    var children = await hierarchy.ListChildrenTrackedAsync(definition.Id, queue.Dequeue(), token)
                        .ConfigureAwait(false);
                    foreach (var child in children.Where(c => Live(c, on)))
                    {
                        subtree.Add(child);
                        queue.Enqueue(child.Id);
                    }
                }

                if (subtree.Count > 1 && !request.Cascade)
                {
                    var liveChildren = subtree.Count(e => e.ParentEntryId == entry.Id);
                    throw RegistryHierarchyRules.Conflict(
                        "err.ECR-REG-0409.nodeHasLiveChildren",
                        $"Entry {entry.Id} has {liveChildren} live child(ren); retire them too (cascade) or first.",
                        new Dictionary<string, object?>
                        {
                            ["count"] = liveChildren.ToString(CultureInfo.InvariantCulture),
                        });
                }

                subtree = subtree.Where(e => Live(e, on)).ToList();
                var before = subtree.Select(e => new { e.Id, e.ValidFrom, e.ValidTo }).ToList();
                foreach (var node in subtree)
                {
                    node.SetValidity(node.ValidFrom, on);
                    node.MarkChangedBy(userId);
                    if (keys is not null)
                    {
                        await keys.ApplyAsync(definition, node, token).ConfigureAwait(false);
                    }
                }

                await uow.SaveChangesAsync(token).ConfigureAwait(false);

                retired = subtree.Select(e => e.Id).ToList();
                foreach (var id in retired)
                {
                    affected += await scanner.RescanForEntryAsync(id, token).ConfigureAwait(false);
                }

                await audit.WriteStructureChangeAsync(
                    new StructureChangeRecord(
                        ChangedAt: clock.UtcNow,
                        TemplateVersionId: 0,
                        EntityType: "dic.RegistryEntry",
                        EntityId: checked((int)entry.Id),
                        ChangeClass: ChangeClass.Breaking,
                        Operation: "Retire",
                        OldJson: JsonSerializer.Serialize(before.Select(b => new
                        {
                            b.Id,
                            validFrom = b.ValidFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            validTo = b.ValidTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        })),
                        NewJson: JsonSerializer.Serialize(new { retiredIds = retired, validTo = on.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }),
                        ChangeReason: HierarchyHandlerSupport.Reason("registryAudit.entryRetired", retired.Count),
                        ChangedByUserId: userId,
                        CorrelationId: currentUser.CorrelationId),
                    token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        return new RetireRegistryEntryResponse(retired, affected);
    }
}
