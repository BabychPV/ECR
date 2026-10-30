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

        // Pairs actually used by NAE nodes of the tree (the business key carries them), normalised like the import does.
        var pairs = new HashSet<(string Region, string Area)>();
        foreach (var entry in entries)
        {
            var region = AirLimitsSchema.NormalizeScope(regionValues.GetValueOrDefault(entry.Id), "");
            if (region.Length == 0)
            {
                continue;
            }

            pairs.Add((region, AirLimitsSchema.NormalizeScope(areaValues.GetValueOrDefault(entry.Id), AirLimitsSchema.NoArea)));
        }

        // Names: the REGION / AREA group nodes of the tree (code R_<REGION> / A_<REGION>_<AREA>), when they exist.
        var levels = await hierarchy.ListLevelsAsync(tables.Nae.Id, ct).ConfigureAwait(false);
        var regionLevel = levels.FirstOrDefault(l => l.Code == NaeTreeSchema.LevelRegion)?.Id;
        var areaLevel = levels.FirstOrDefault(l => l.Code == NaeTreeSchema.LevelArea)?.Id;
        var regionNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var areaNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Where(e => e.LevelId is not null))
        {
            var name = entry.DisplayL10n.Get("en") ?? entry.Code;
            if (entry.LevelId == regionLevel && entry.Code.StartsWith("R_", StringComparison.OrdinalIgnoreCase))
            {
                regionNames[entry.Code[2..]] = name;
            }
            else if (entry.LevelId == areaLevel && entry.Code.StartsWith("A_", StringComparison.OrdinalIgnoreCase))
            {
                areaNames[entry.Code[2..]] = name;
            }
        }

        var regions = pairs.GroupBy(p => p.Region, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new AirRegionCodeDto(
                g.Key,
                regionNames.GetValueOrDefault(g.Key),
                [.. g.OrderBy(p => p.Area, StringComparer.Ordinal)
                    .Select(p => new AirAreaCodeDto(p.Area, areaNames.GetValueOrDefault($"{g.Key}_{p.Area}")))]))
            .ToList();
        return new AirScopeCodesDto(regions);
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
