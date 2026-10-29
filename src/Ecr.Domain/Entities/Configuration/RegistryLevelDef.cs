// src/Ecr.Domain/Entities/Configuration/RegistryLevelDef.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// One declared level of a hierarchical registry (NAE plan N1): Region, Area, Site, ... A node sits on exactly one level;
/// along any path the ordinal strictly grows and every skipped level must be optional.
/// </summary>
public sealed class RegistryLevelDef : Entity<int>
{
    /// <summary>Hard limit of levels, and so of the tree depth.</summary>
    public const int MaxLevels = 6;

    private RegistryLevelDef() { }

    /// <summary>Creates a level.</summary>
    /// <param name="registryDefId">Owning registry.</param>
    /// <param name="ordinal">1-based depth, 1..<see cref="MaxLevels"/>.</param>
    /// <param name="code">Level code, unique within the registry.</param>
    /// <param name="name">Name, all languages.</param>
    /// <param name="isOptional">A node may skip this level.</param>
    public RegistryLevelDef(int registryDefId, int ordinal, EcrCode code, LocalizedText name, bool isOptional)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(registryDefId);
        RegistryDefId = registryDefId;
        Code = code.Value;
        Update(ordinal, name, isOptional);
    }

    /// <summary>Owning registry.</summary>
    public int RegistryDefId { get; private set; }

    /// <summary>1-based depth.</summary>
    public int Ordinal { get; private set; }

    /// <summary>Level code; never changes (it is the identity the API uses).</summary>
    public string Code { get; private set; } = null!;

    /// <summary>Name, all languages.</summary>
    public LocalizedText NameL10n { get; private set; } = null!;

    /// <summary>A node may skip this level.</summary>
    public bool IsOptional { get; private set; }

    /// <summary>Parks the level on a temporary ordinal outside 1..MaxLevels so a swap does not trip the unique index.</summary>
    /// <param name="tempOrdinal">Ordinal above <see cref="MaxLevels"/>.</param>
    public void Park(int tempOrdinal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(tempOrdinal, MaxLevels);
        Ordinal = tempOrdinal;
    }

    /// <summary>Changes the mutable part of the level.</summary>
    /// <param name="ordinal">1-based depth.</param>
    /// <param name="name">Name.</param>
    /// <param name="isOptional">Optional flag.</param>
    public void Update(int ordinal, LocalizedText name, bool isOptional)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ordinal, MaxLevels);
        ArgumentNullException.ThrowIfNull(name);
        Ordinal = ordinal;
        NameL10n = name;
        IsOptional = isOptional;
    }
}
