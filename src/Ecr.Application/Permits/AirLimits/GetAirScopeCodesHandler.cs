// src/Ecr.Application/Permits/AirLimits/GetAirScopeCodesHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Nae;
using Ecr.Application.Security;

namespace Ecr.Application.Permits.AirLimits;

/// <summary>
/// Region and area scope codes known to the NAE tree, for the import wizard (no free text). Permission <c>Registry.View</c>.
/// </summary>
public sealed class GetAirScopeCodesHandler(
    AirLimitsSettingsAccessor accessor,
    IRegistryRowsQuery rows,
    IRegistryHierarchyStore hierarchy,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Permission to read registries.</summary>
    public const string Permission = "Registry.View";

    /// <summary>Lists the regions with their areas.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AirScopeCodesDto> HandleAsync(CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var data = accessor.Data;
        var tables = await data.TablesAsync(ct).ConfigureAwait(false);
        var entries = (await rows.ListEntriesAsync(tables.Nae.Id, null, ct).ConfigureAwait(false)).Where(e => !e.IsDeleted).ToList();
        var regionValues = (await rows.ListFieldValuesAsync([AirTables.Field(tables.Nae, NaeTreeSchema.RegionField)], null, ct).ConfigureAwait(false))
            .ToDictionary(v => v.RegistryEntryId, v => v.Text);
        var areaValues = (await rows.ListFieldValuesAsync([AirTables.Field(tables.Nae, NaeTreeSchema.AreaField)], null, ct).ConfigureAwait(false))
            .ToDictionary(v => v.RegistryEntryId, v => v.Text);

        var levels = await hierarchy.ListLevelsAsync(tables.Nae.Id, ct).ConfigureAwait(false);
        int? Level(string code) => levels.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))?.Id;
        var regionLevel = Level(NaeTreeSchema.LevelRegion);
        var areaLevel = Level(NaeTreeSchema.LevelArea);
        var sourceLevel = Level(NaeTreeSchema.LevelSource);
        var byId = entries.ToDictionary(e => e.Id);
        string Name(Domain.Entities.Dictionaries.RegistryEntry e) => e.DisplayL10n.Get("en") ?? e.Code;
        string Strip(string code, string prefix) => code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? code[prefix.Length..] : code;

        // Region code of a REGION node: its RegionCode, else its technical code without "R_".
        string RegionOf(Domain.Entities.Dictionaries.RegistryEntry e)
            => AirLimitsSchema.NormalizeScope(regionValues.GetValueOrDefault(e.Id) is { Length: > 0 } r ? r : Strip(e.Code, "R_"), "");

        var regions = new SortedDictionary<string, (string? Name, SortedDictionary<string, string?> Areas, bool LooseSources)>(StringComparer.Ordinal);
        (string? Name, SortedDictionary<string, string?> Areas, bool LooseSources) RegionSlot(string code)
        {
            if (!regions.TryGetValue(code, out var slot))
            {
                slot = (null, new SortedDictionary<string, string?>(StringComparer.Ordinal), false);
                regions[code] = slot;
            }

            return slot;
        }

        // 1. REGION group nodes: a region exists even before it has any NAE under it.
        foreach (var e in entries.Where(x => regionLevel is not null && x.LevelId == regionLevel))
        {
            var code = RegionOf(e);
            if (code.Length > 0)
            {
                var slot = RegionSlot(code);
                regions[code] = (Name(e), slot.Areas, slot.LooseSources);
            }
        }

        // 2. AREA group nodes: the areas of the region they hang under (the REGION group node has RegionCode but no AreaCode, so
        //    it never yields an area itself).
        foreach (var e in entries.Where(x => areaLevel is not null && x.LevelId == areaLevel))
        {
            var region = "";
            for (var cursor = e.ParentEntryId; cursor is { } p && byId.TryGetValue(p, out var parent); cursor = parent.ParentEntryId)
            {
                if (parent.LevelId == regionLevel)
                {
                    region = RegionOf(parent);
                    break;
                }
            }

            if (region.Length == 0)
            {
                region = AirLimitsSchema.NormalizeScope(regionValues.GetValueOrDefault(e.Id), "");
            }

            var area = AirLimitsSchema.NormalizeScope(
                areaValues.GetValueOrDefault(e.Id) is { Length: > 0 } a ? a : Strip(Strip(e.Code, "A_"), region + "_"), AirLimitsSchema.NoArea);
            if (region.Length > 0 && area != AirLimitsSchema.NoArea)
            {
                RegionSlot(region).Areas[area] = Name(e);
            }
        }

        // 3. NAE (source) nodes: the scope their business key carries; a node created by an import may sit outside its region's
        //    group nodes. A source without an area marks the region as having "no area" sources.
        foreach (var e in entries.Where(x => x.LevelId is null || x.LevelId == sourceLevel))
        {
            var region = AirLimitsSchema.NormalizeScope(regionValues.GetValueOrDefault(e.Id), "");
            if (region.Length == 0)
            {
                continue;
            }

            var area = AirLimitsSchema.NormalizeScope(areaValues.GetValueOrDefault(e.Id), AirLimitsSchema.NoArea);
            var slot = RegionSlot(region);
            if (area == AirLimitsSchema.NoArea)
            {
                regions[region] = (slot.Name, slot.Areas, true);
            }
            else
            {
                slot.Areas.TryAdd(area, null);
            }
        }

        var list = regions.Select(r => new AirRegionCodeDto(
                r.Key,
                r.Value.Name,
                r.Value.Areas.Count == 0
                    ? [new AirAreaCodeDto(AirLimitsSchema.NoArea, null)]
                    : [.. r.Value.Areas.Select(a => new AirAreaCodeDto(a.Key, a.Value)),
                        .. r.Value.LooseSources ? [new AirAreaCodeDto(AirLimitsSchema.NoArea, null)] : Array.Empty<AirAreaCodeDto>()]))
            .ToList();        return new AirScopeCodesDto(list);
    }
}

/// <summary>Region and area codes of the NAE tree.</summary>
/// <param name="Regions">Regions, by code.</param>
public sealed record AirScopeCodesDto(IReadOnlyList<AirRegionCodeDto> Regions);

/// <summary>One region.</summary>
/// <param name="Code">Normalised region code (what <c>region</c> of the import options takes).</param>
/// <param name="Name">Name of the region group node, when the tree has one.</param>
/// <param name="Areas">Areas used in the region, by code (<c>-</c> style "no area" code included when NAE exist without one).</param>
public sealed record AirRegionCodeDto(string Code, string? Name, IReadOnlyList<AirAreaCodeDto> Areas);

/// <summary>One area of a region.</summary>
/// <param name="Code">Normalised area code (what <c>area</c> of the import options takes).</param>
/// <param name="Name">Name of the area group node, when the tree has one.</param>
public sealed record AirAreaCodeDto(string Code, string? Name);
