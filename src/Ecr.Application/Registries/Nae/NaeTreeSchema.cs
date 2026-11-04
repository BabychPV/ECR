// src/Ecr.Application/Registries/Nae/NaeTreeSchema.cs
namespace Ecr.Application.Registries.Nae;

/// <summary>Where the manual NAE tree actions write and read (configuration <c>AirLimits:NaeRegistryCode</c>,
/// <c>NaeTree:RegionSourceRegistryCode</c>).</summary>
/// <param name="RegistryCode">NAE tree registry (the same one the air limits permits point at).</param>
/// <param name="RegionSourceRegistryCode">Registry the seed-roots action reads the regions from.</param>
public sealed record NaeTreeSettings(string RegistryCode, string RegionSourceRegistryCode)
{
    /// <summary>Defaults: <c>NAE_TREE</c>, <c>LAND_REGION</c>.</summary>
    public static NaeTreeSettings Default { get; } = new(NaeTreeSchema.RegistryCode, "LAND_REGION");
}

/// <summary>Fixed names of the NAE tree registry (NAE plan N2).</summary>
/// <remarks>
/// The registry is an ordinary hierarchical registry (N1): five levels, string fields for the business key and the extra
/// attributes. The technical entry <c>Code</c> is generated (unique per registry); the NAE code lives in the
/// <see cref="NaeCodeField"/> field, so one NAE code can repeat under different regions and areas.
/// </remarks>
public static class NaeTreeSchema
{
    /// <summary>Registry code.</summary>
    public const string RegistryCode = "NAE_TREE";

    /// <summary>Default AF element that holds the equipment tree.</summary>
    public const string DefaultAfRoot = "ECR_01_Air Equipment";

    /// <summary>Level: region (Atyrau, Mangistau).</summary>
    public const string LevelRegion = "REGION";

    /// <summary>Level: onshore / offshore (optional — Mangistau has none).</summary>
    public const string LevelArea = "AREA";

    /// <summary>Level: site / installation (barge, island, gas treatment plant).</summary>
    public const string LevelSite = "SITE";

    /// <summary>Level: equipment group (optional).</summary>
    public const string LevelEquipment = "EQUIP";

    /// <summary>Level: the NAE source itself.</summary>
    public const string LevelSource = "SRC";

    /// <summary>Normalised NAE code (4-digit padded or <c>NN_suffix</c>). Part of the business key.</summary>
    public const string NaeCodeField = "NaeCode";

    /// <summary>Region code (<c>ATYRAU</c>). Part of the business key.</summary>
    public const string RegionField = "RegionCode";

    /// <summary>Area code (<c>ONSHORE</c>, <c>OFFSHORE</c>); empty for a region without areas. Part of the business key.</summary>
    public const string AreaField = "AreaCode";

    /// <summary>AF element path the node was loaded from (informational).</summary>
    public const string AfPathField = "AfPath";

    /// <summary>Source type (Т / П1) — filled by the permit import later.</summary>
    public const string SourceTypeField = "SourceType";

    /// <summary>Emission point name — filled by the permit import later.</summary>
    public const string PointNameField = "PointName";

    /// <summary>Composite business key of an NAE: NAE code + region + area (legacy <c>CompositeKeyImport</c>).</summary>
    public const string BusinessKeyCode = "NAE_BUSINESS";

    /// <summary>Level codes in order.</summary>
    public static readonly IReadOnlyList<string> LevelCodes =
        [LevelRegion, LevelArea, LevelSite, LevelEquipment, LevelSource];

    /// <summary>
    /// AF element template → node type (human 03.11, quiz): the AF load maps by TEMPLATE NAME, not by depth.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LevelByAfTemplate =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Air_Region"] = LevelRegion,
            ["Air_OnShoreOffShore"] = LevelArea,
            ["Air_Area"] = LevelSite,
            ["Air_Unit"] = LevelEquipment,
            ["Equipment"] = LevelSource,
        };

    /// <summary>
    /// Field → node type it belongs to (human 03.11: attributes are configured per node type; <c>null</c> — all levels).
    /// <see cref="RegionField"/> / <see cref="AreaField"/> are not bound to a level (decision 03.11, «Аналіз»): they are the OWN
    /// identity of a REGION / AREA node (writable there, see <see cref="OwnLevelOfScopeField"/>) and derived from the ancestors on
    /// every other node; <see cref="AfPathField"/> is informational on every node.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string?> LevelByField =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [NaeCodeField] = LevelSource,
            [RegionField] = null,
            [AreaField] = null,
            [AfPathField] = null,
            [SourceTypeField] = LevelSource,
            [PointNameField] = LevelSource,
        };

    /// <summary>Fields the server owns on every node: client input is ignored (written by the AF load).</summary>
    public static readonly IReadOnlySet<string> ReadOnlyFields =
        new HashSet<string>([AfPathField], StringComparer.OrdinalIgnoreCase);

    /// <summary>The only level where the client may write <see cref="RegionField"/> / <see cref="AreaField"/>; <c>null</c> — other field.</summary>
    /// <param name="field">Field code.</param>
    public static string? OwnLevelOfScopeField(string field)
        => string.Equals(field, RegionField, StringComparison.OrdinalIgnoreCase) ? LevelRegion
            : string.Equals(field, AreaField, StringComparison.OrdinalIgnoreCase) ? LevelArea
            : null;

    /// <summary>The level that may nest inside itself (human 03.11, Q2: Air_Area inside Air_Area).</summary>
    public const string SelfNestingLevel = LevelSite;

    /// <summary>Node type of an AF template; <c>null</c> — not an NAE tree template.</summary>
    /// <param name="template">AF template name.</param>
    public static string? LevelOfAfTemplate(string? template)
        => template is not null && LevelByAfTemplate.TryGetValue(template.Trim(), out var level) ? level : null;

    /// <summary>
    /// The same placement rule as <c>RegistryHierarchyRules.CheckPlacement</c> over the fixed NAE levels (pure, for the AF plan):
    /// the ordinal strictly grows (SITE may sit under SITE), skipped levels must be optional (AREA, SITE, EQUIP; SITE became optional on 04.11 — AN-158).
    /// </summary>
    /// <param name="parentLevel">Parent level; <c>null</c> — root.</param>
    /// <param name="level">Node level.</param>
    /// <returns><c>true</c> — the place is valid.</returns>
    public static bool IsValidPlacement(string? parentLevel, string level)
    {
        var rank = LevelCodes.ToList();
        var node = rank.IndexOf(level);
        var parent = parentLevel is null ? -1 : rank.IndexOf(parentLevel);
        if (node < 0 || (parentLevel is not null && parent < 0))
        {
            return false;
        }

        if (parent == node)
        {
            return level == SelfNestingLevel;
        }

        return node > parent && rank.Skip(parent + 1).Take(node - parent - 1).All(l => l is LevelArea or LevelSite or LevelEquipment);
    }

    /// <summary>
    /// Region code derived from the REGION ancestor (the business-key part): the technical code without the <c>R_</c> prefix the
    /// loads give it (<c>R_ATYRAU</c> → <c>ATYRAU</c>), otherwise the upper-cased code itself.
    /// </summary>
    /// <param name="regionCode">Technical code of the REGION node.</param>
    public static string DerivedRegion(string regionCode)
        => Meaningful(NaeAfTreeBuilder.Upper(regionCode.StartsWith("R_", StringComparison.OrdinalIgnoreCase) ? regionCode[2..] : regionCode));

    /// <summary>
    /// AN-158: a derived code made only of <c>_</c> (a non-ASCII code flattened into the technical one) is no code — empty, so the
    /// UI shows an empty field instead of <c>_____</c>.
    /// </summary>
    /// <param name="derived">Derived code.</param>
    public static string Meaningful(string derived) => NaeAfTreeBuilder.HasMeaning(derived) ? derived : "";

    /// <summary>
    /// Area code derived from the AREA ancestor: the technical code without the <c>A_&lt;REGION&gt;_</c> prefix the loads give it
    /// (<c>A_ATYRAU_OFFSHORE</c> → <c>OFFSHORE</c>), otherwise the upper-cased code; no AREA ancestor → <see cref="NaeAfTreeBuilder.NoArea"/>.
    /// </summary>
    /// <param name="areaCode">Technical code of the AREA node; <c>null</c> — the region has no On/Offshore split.</param>
    /// <param name="region">Derived region code.</param>
    public static string DerivedArea(string? areaCode, string region)
    {
        if (areaCode is null)
        {
            return NaeAfTreeBuilder.NoArea;
        }

        var prefix = $"A_{region}_";
        return Meaningful(NaeAfTreeBuilder.Upper(areaCode.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? areaCode[prefix.Length..] : areaCode));
    }
}
