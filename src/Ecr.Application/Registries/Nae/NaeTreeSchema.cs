// src/Ecr.Application/Registries/Nae/NaeTreeSchema.cs
namespace Ecr.Application.Registries.Nae;

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
}
