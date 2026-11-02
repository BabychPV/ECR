// src/Ecr.Application/Permits/AirLimits/AirScopeMatcher.cs
namespace Ecr.Application.Permits.AirLimits;

/// <summary>Where a Region/Area registry entry lands in the NAE tree.</summary>
/// <param name="NaeRegion">NAE tree region code; <c>null</c> — the region is not in the NAE tree.</param>
/// <param name="NaeArea">
/// NAE tree area code; <c>null</c> — match by region only (the tree region has no areas, no area was chosen, or the chosen area has
/// no counterpart in the tree).
/// </param>
public sealed record AirScopeMatch(string? NaeRegion, string? NaeArea)
{
    /// <summary>The region maps to the NAE tree.</summary>
    public bool InTree => NaeRegion is not null;
}

/// <summary>
/// ✎ AIR-ON-AIR: the ONE place that maps a Region/Area registry entry (<c>LAND_REGION</c>, <c>LAND_AREA</c>) to the NAE tree scope.
/// </summary>
/// <remarks>
/// Region entry → NAE tree region by normalised English name (<c>Atyrau</c> → <c>ATYRAU</c>). Area entry → NAE tree area ONLY where
/// that tree region has areas, by normalised name with a trailing «complex» dropped (<c>Offshore complex</c> → <c>OFFSHORE</c>,
/// <c>Onshore complex</c> → <c>ONSHORE</c>). Any other area (work areas such as <c>Bautino base</c>) → match by region only.
/// Normalised = upper case, letters and digits only. Coordinator's decision 02.11.
/// </remarks>
public static class AirScopeMatcher
{
    /// <summary>Maps a region name (and optional area name) to the NAE tree scopes.</summary>
    /// <param name="regionName">English name of the Region entry.</param>
    /// <param name="areaName">English name of the Area entry; <c>null</c> — none chosen.</param>
    /// <param name="tree">Regions and areas of the NAE tree (<see cref="GetAirScopeCodesHandler"/>).</param>
    public static AirScopeMatch Match(string regionName, string? areaName, IReadOnlyList<AirRegionCodeDto> tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var wanted = Normalize(regionName);
        var region = wanted.Length == 0
            ? null
            : tree.FirstOrDefault(r => Normalize(r.Name) == wanted || Normalize(r.Code) == wanted);
        if (region is null)
        {
            return new AirScopeMatch(null, null);
        }

        var realAreas = region.Areas.Where(a => a.Code != AirLimitsSchema.NoArea).ToList();
        if (areaName is null || realAreas.Count == 0)
        {
            return new AirScopeMatch(region.Code, null);
        }

        var area = AreaKey(areaName);
        var hit = realAreas.FirstOrDefault(a => Normalize(a.Code) == area || Normalize(a.Name) == area);
        return new AirScopeMatch(region.Code, hit?.Code);
    }

    /// <summary>Normalised area name with a trailing «complex» dropped (<c>Offshore complex</c> → <c>OFFSHORE</c>).</summary>
    public static string AreaKey(string areaName)
    {
        var area = Normalize(areaName);
        return area.EndsWith(Complex, StringComparison.Ordinal) && area.Length > Complex.Length ? area[..^Complex.Length] : area;
    }

    /// <summary>NAE tree area code an Area value maps to in ANY tree region that has areas; <c>null</c> — none (region only).</summary>
    /// <param name="areaName">Name (or code) of the Area value.</param>
    /// <param name="tree">Regions and areas of the NAE tree.</param>
    public static string? AreaCode(string areaName, IReadOnlyList<AirRegionCodeDto> tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return tree
            .Select(r => Match(r.Code, areaName, tree).NaeArea)
            .FirstOrDefault(a => a is not null);
    }

    private const string Complex = "COMPLEX";

    /// <summary>Upper case, ASCII/Unicode letters and digits only.</summary>
    public static string Normalize(string? text)
        => text is null ? string.Empty : new string([.. text.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant)]);
}
