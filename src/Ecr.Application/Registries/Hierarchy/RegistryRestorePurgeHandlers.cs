// src/Ecr.Application/Registries/Hierarchy/RegistryRestorePurgeHandlers.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;

namespace Ecr.Application.Registries.Hierarchy;

/// <summary>Shared rule of restore and purge: what «retired» means (the same as the tree's «Retired» badge).</summary>
internal static class RetiredState
{
    /// <summary>Switched off, or its validity window closed on or before <paramref name="today"/>.</summary>
    internal static bool IsRetired(RegistryEntry e, DateOnly today) => !e.IsActive || (e.ValidTo is { } to && to <= today);
}

/// <summary>
/// Restores a retired node (human 04.11: «a Restore button»). Permission <c>Registry.EditData</c> or grant <c>Write</c> — the
/// same as retire.
/// </summary>
/// <remarks>
/// The node only: its window is reopened (<c>ValidTo = null</c>) and it is switched on; children stay retired. A retired (or
/// deleted) ancestor is <c>409 parentRetired</c> unless <c>withAncestors</c>, which restores the chain up to the first live
/// ancestor. The node comes back only if it fits again: an NAE source whose number is now held by a live source in its
/// region/area is <c>409 ECR-REG-4094 naeCodeTaken</c>, a registry key held by a live entry is <c>409 ECR-REG-4092</c> — the
/// same checks as an upsert, in this transaction. The entry code cannot collide: <c>UQ_RegistryEntry (RegistryDefId, Code)</c>
/// is unique across live, retired and deleted entries.
/// </remarks>
public sealed class UnretireRegistryEntryHandler(
    IRegistryStore registries,
    IRegistryHierarchyStore hierarchy,
    IOrphanScanner scanner,
    IUnitOfWork uow,
    IAuditWriter audit,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock,
    Keys.RegistryKeyService? keys = null,
    Nae.NaeTreeNodePolicy? nae = null)
{
    // ⚠ `keys` and `nae` — the container supplies both; without them (hand-built tests) a restore checks neither.

    /// <summary>Permission to change registry data.</summary>
    public const string Permission = "Registry.EditData";

    /// <summary>Reopens the node (and, on request, its retired ancestors).</summary>
    /// <param name="registryCode">Registry code.</param>
    /// <param name="entryId">Entry.</param>
    /// <param name="request">Ancestors flag.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<UnretireRegistryEntryResponse> HandleAsync(
        string registryCode, long entryId, UnretireRegistryEntryRequest request, CancellationToken ct)
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

        var today = DateOnly.FromDateTime(clock.UtcNow);
        if (!RetiredState.IsRetired(entry, today))
        {
            return new UnretireRegistryEntryResponse([], 0);
        }

        var restored = new List<long>();
        var affected = 0;

        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                // Same serialisation as retire and move: the definition row lock.
                definition.BumpDataRevision();
                await uow.SaveChangesAsync(token).ConfigureAwait(false);

                // Ancestors up to the first live one. A deleted ancestor cannot be restored here (Restore of a deleted entry is
                // a separate action) — it blocks like a retired one, withAncestors or not.
                var chain = new List<RegistryEntry>();
                var blocked = new List<RegistryEntry>();
                var seen = new HashSet<long> { entry.Id };
                for (var parentId = entry.ParentEntryId; parentId is { } p && seen.Add(p);)
                {
                    var parent = await registries.FindEntryAsync(p, token).ConfigureAwait(false);
                    if (parent is null || !(parent.IsDeleted || RetiredState.IsRetired(parent, today)))
                    {
                        break;
                    }

                    chain.Add(parent);
                    if (parent.IsDeleted)
                    {
                        blocked.Add(parent);
                    }

                    parentId = parent.ParentEntryId;
                }

                if (chain.Count > 0 && (request.WithAncestors != true || blocked.Count > 0))
                {
                    throw RegistryHierarchyRules.Conflict(
                        "err.ECR-REG-0409.parentRetired",
                        $"Entry {entry.Code} sits under {chain.Count} retired ancestor(s); restore them first or together (withAncestors).",
                        new Dictionary<string, object?>
                        {
                            ["count"] = chain.Count.ToString(CultureInfo.InvariantCulture),
                            ["ancestorIds"] = chain.Select(a => a.Id).ToList(),
                            ["ancestorCodes"] = string.Join(", ", chain.Select(a => a.Code)),
                            ["deletedAncestorIds"] = blocked.Select(a => a.Id).ToList(),
                        });
                }

                chain.Reverse();
                var nodes = new List<RegistryEntry>(chain) { entry };
                var before = nodes.Select(e => new { e.Id, e.ValidFrom, e.ValidTo, e.IsActive }).ToList();

                var levels = await hierarchy.ListLevelsAsync(definition.Id, token).ConfigureAwait(false);
                var naeTree = nae is not null && Nae.NaeTreeNodePolicy.IsNaeTree(definition);
                var noValues = new Dictionary<string, object?>();
                foreach (var node in nodes)
                {
                    // The NAE number of a restored source must still be free in its region/area (the stored value is read).
                    if (naeTree && levels.FirstOrDefault(l => l.Id == node.LevelId) is { } level)
                    {
                        await nae!.EnsureCodeFreeAsync(definition, node, level, node.ParentEntryId, noValues, token).ConfigureAwait(false);
                    }

                    node.SetValidity(node.ValidFrom, null);
                    node.Activate();
                    node.MarkChangedBy(userId);
                    if (keys is not null)
                    {
                        await keys.ApplyAsync(definition, node, token).ConfigureAwait(false);
                    }
                }

                await uow.SaveChangesAsync(token).ConfigureAwait(false);

                restored = nodes.Select(e => e.Id).ToList();
                foreach (var id in restored)
                {
                    affected += await scanner.RescanForEntryAsync(id, token).ConfigureAwait(false);
                }

                await audit.WriteStructureChangeAsync(
                    new StructureChangeRecord(
                        ChangedAt: clock.UtcNow,
                        TemplateVersionId: 0,
                        EntityType: "dic.RegistryEntry",
                        EntityId: checked((int)entry.Id),
                        ChangeClass: ChangeClass.Guarded,
                        Operation: "Unretire",
                        OldJson: JsonSerializer.Serialize(before.Select(b => new
                        {
                            b.Id,
                            validFrom = Day(b.ValidFrom),
                            validTo = Day(b.ValidTo),
                            b.IsActive,
                        })),
                        NewJson: JsonSerializer.Serialize(new { restoredIds = restored, validTo = (string?)null, isActive = true }),
                        ChangeReason: HierarchyHandlerSupport.Reason("registryAudit.entryRestored", restored.Count),
                        ChangedByUserId: userId,
                        CorrelationId: currentUser.CorrelationId),
                    token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        return new UnretireRegistryEntryResponse(restored, affected);
    }

    private static string? Day(DateOnly? day) => day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// Physically deletes a retired or deleted entry that nothing uses (human 04.11: «full deletion if it is not used anywhere»).
/// Permission <c>Registry.EditData</c> or grant <c>Write</c> — the same as retire.
/// </summary>
/// <remarks>
/// Two steps against mistakes: an active live entry is <c>409 purgeActive</c> — retire it first. Any trace of the id outside
/// the rows the entry owns (<see cref="IRegistryStore.FindHardReferencesAsync"/>: cells, header values, values of other —
/// also deleted — entries, children in any state, links, methodology, event maps, submission snapshots, the archive, rule
/// predicates) is <c>409 entryUsed</c> with the kinds. A foreign key that still fires on the DELETE (a reference added between
/// the check and the delete) is the same 409 with kind <c>other</c>. The deleted row and its values go to
/// <c>aud.StructureChange</c> (<c>Purge</c>, <c>Breaking</c>) in the same transaction.
/// </remarks>
public sealed class PurgeRegistryEntryHandler(
    IRegistryStore registries,
    IUnitOfWork uow,
    IAuditWriter audit,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Permission to change registry data.</summary>
    public const string Permission = "Registry.EditData";

    /// <summary>Deletes the entry for good.</summary>
    /// <param name="registryCode">Registry code.</param>
    /// <param name="entryId">Entry.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task HandleAsync(string registryCode, long entryId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryCode);

        var lookup = new RegistryLookup(registries, registryCode);
        await RegistryAccess.RequireAsync(access, currentUser, Permission, GrantLevel.Write, lookup, ct).ConfigureAwait(false);
        var userId = HierarchyHandlerSupport.RequireUser(currentUser);
        var definition = await lookup.RequireAsync(ct).ConfigureAwait(false);

        // ⛔ D-211: an External registry is mastered by AF — no manual delete.
        ExternalRegistryGuard.EnsureManualEditAllowed(definition);

        // ⚠ A deleted entry is purgeable (that is the point), so unlike retire it is not a 404 here; another registry's is.
        var entry = await registries.FindEntryAsync(entryId, ct).ConfigureAwait(false);
        if (entry is null || entry.RegistryDefId != definition.Id)
        {
            throw RegistryAccess.EntryNotFound(entryId, registryCode);
        }

        var today = DateOnly.FromDateTime(clock.UtcNow);
        if (!entry.IsDeleted && !RetiredState.IsRetired(entry, today))
        {
            throw RegistryHierarchyRules.Conflict(
                "err.ECR-REG-0409.purgeActive",
                $"Entry {entry.Code} is active: retire it first, then delete it for good.",
                new Dictionary<string, object?> { ["code"] = entry.Code });
        }

        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                // The definition row lock serialises this with upserts, moves and retires of the same registry.
                definition.BumpDataRevision();
                await uow.SaveChangesAsync(token).ConfigureAwait(false);

                var used = await registries.FindHardReferencesAsync(entry, token).ConfigureAwait(false);
                if (used.Count > 0)
                {
                    throw Used(entry, used);
                }

                var values = await registries.ListValuesAsync(entry.Id, token).ConfigureAwait(false);
                var oldJson = JsonSerializer.Serialize(new
                {
                    entry = new
                    {
                        entry.Id,
                        entry.RegistryDefId,
                        entry.Code,
                        display = entry.DisplayL10n.Values,
                        entry.ParentEntryId,
                        entry.LevelId,
                        entry.Ordinal,
                        validFrom = entry.ValidFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        validTo = entry.ValidTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        entry.IsActive,
                        entry.IsDeleted,
                        entry.CreatedAt,
                        entry.CreatedByUserId,
                    },
                    values = values.OrderBy(v => v.RegistryFieldDefId).Select(v => new
                    {
                        fieldId = v.RegistryFieldDefId,
                        v.ValueString,
                        v.ValueNumeric,
                        v.ValueDate,
                        v.ValueBool,
                        v.ValueRefEntryId,
                        v.ValueUnitId,
                    }),
                });

                if (!await registries.PurgeEntryAsync(entry.Id, token).ConfigureAwait(false))
                {
                    throw Used(entry, new Dictionary<string, int> { [RegistryEntryHardReferences.Other] = 1 });
                }

                await audit.WriteStructureChangeAsync(
                    new StructureChangeRecord(
                        ChangedAt: clock.UtcNow,
                        TemplateVersionId: 0,
                        EntityType: "dic.RegistryEntry",
                        EntityId: checked((int)entryId),
                        ChangeClass: ChangeClass.Breaking,
                        Operation: "Purge",
                        OldJson: oldJson,
                        NewJson: null,
                        ChangeReason: HierarchyHandlerSupport.Reason("registryAudit.entryPurged", 1),
                        ChangedByUserId: userId,
                        CorrelationId: currentUser.CorrelationId),
                    token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>The same shape as <c>entryReferenced</c> of the soft delete: totals and the kinds for the client list.</summary>
    private static BusinessRuleException Used(RegistryEntry entry, IReadOnlyDictionary<string, int> kinds)
    {
        var total = kinds.Values.Sum();
        return RegistryHierarchyRules.Conflict(
            "err.ECR-REG-0409.entryUsed",
            $"Entry {entry.Code} is still used ({string.Join(", ", kinds.Select(k => $"{k.Key}: {k.Value}"))}); it cannot be deleted for good.",
            new Dictionary<string, object?>
            {
                ["code"] = entry.Code,
                ["referenceCount"] = total.ToString(CultureInfo.InvariantCulture),
                ["registryEntryId"] = entry.Id,
                ["references"] = total,
                ["referenceKinds"] = kinds,
            });
    }
}
