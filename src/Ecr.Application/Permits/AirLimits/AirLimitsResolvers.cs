// src/Ecr.Application/Permits/AirLimits/AirLimitsResolvers.cs
using Ecr.Application.RegistryImport;

namespace Ecr.Application.Permits.AirLimits;

/// <summary>A scope value of an import: a registry entry (Registry / List column) or a text (Text column).</summary>
/// <param name="EntryId">Registry entry the row stores; <c>null</c> for Text.</param>
/// <param name="Text">Text the row stores (Text column); <c>null</c> for an entry.</param>
/// <param name="Label">Name used for the NAE matching and in messages (entry en name or the text).</param>
/// <param name="Code">Entry code (SSRS / audit) or the text.</param>
internal sealed record AirScopeValue(long? EntryId, string? Text, string Label, string Code)
{
    /// <summary>What the source row stores in the scope column.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public object Stored => EntryId is { } id ? id : Text!;

    /// <summary>Whether a stored source row belongs to this value.</summary>
    public bool Holds(AirRow row, string column)
        => EntryId is { } id
            ? row.Ref(column) == id
            : string.Equals(AirLimitsSchema.NormalizeScope(row.Text(column), ""), AirLimitsSchema.NormalizeScope(Text, ""), StringComparison.Ordinal);
}

/// <summary>
/// ✎ AIR-ON-AIR: the scope of one import — the values stored on the source rows (region, optional area) and the NAE tree scope they
/// map to through <see cref="AirScopeMatcher"/>. <paramref name="NaeArea"/> <c>null</c> = any area of the region (no area chosen, or
/// an area matched by region only). A region NOT in the tree (<paramref name="InTree"/> = false) gets its own NAE region code from
/// its name, so its NAE are looked up and created there and never borrowed from another region.
/// </summary>
internal sealed record AirImportScope(string NaeRegion, string? NaeArea, bool InTree, AirScopeValue Region, AirScopeValue? Area)
{
    /// <summary>Builds the scope from the chosen values and the matcher's answer.</summary>
    public static AirImportScope From(AirScopeMatch match, AirScopeValue region, AirScopeValue? area)
        => match.InTree
            ? new AirImportScope(match.NaeRegion!, match.NaeArea, true, region, area)
            : new AirImportScope(
                AirLimitsSchema.NormalizeScope(region.Label, "R"),
                area is null ? null : AirScopeMatcher.AreaKey(area.Label),
                false,
                region,
                area);

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Opaque scope key (also part of the plan fingerprint).</summary>
    public string Key() => System.Text.Json.JsonSerializer.Serialize(this, Json);

    /// <summary>Scope of a key built by <see cref="Key"/>.</summary>
    public static AirImportScope Parse(string scopeKey)
        => System.Text.Json.JsonSerializer.Deserialize<AirImportScope>(scopeKey, Json)
           ?? throw new InvalidOperationException("Empty air import scope.");

    /// <summary>A stored source row of the permit lies inside this scope (same region value, same area value or both empty).</summary>
    public bool Holds(AirRow row)
        => Region.Holds(row, AirLimitsSchema.Region)
           && (Area is { } area
               ? area.Holds(row, AirLimitsSchema.Area)
               : row.Ref(AirLimitsSchema.Area) is null && string.IsNullOrWhiteSpace(row.Text(AirLimitsSchema.Area)));

    /// <summary>Region code written on NAE nodes created by the import.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string NaeRegionForCreate => NaeRegion;

    /// <summary>Area code written on NAE nodes created by the import.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string NaeAreaForCreate => NaeArea ?? AirLimitsSchema.NoArea;
}

/// <summary>NAE codes against the NAE tree, inside the region and area of the import.</summary>
public sealed class NaeReferenceResolver(AirLimitsSettingsAccessor data) : INaeResolver
{
    /// <inheritdoc />
    public string Kind => INaeResolver.NaeKind;

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> FindExistingAsync(
        IReadOnlyCollection<string> codes, ImportScope scope, CancellationToken ct)
    {
        var air = AirImportScope.Parse(scope.ScopeKey);
        var tables = await data.Data.TablesAsync(ct).ConfigureAwait(false);
        var found = await data.Data.NaeByCodeAsync(tables, codes, air.NaeRegion, air.NaeArea, ct).ConfigureAwait(false);
        return found.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Substance codes against the substance registry.</summary>
public sealed class SubstanceReferenceResolver(AirLimitsSettingsAccessor data) : ISubstanceResolver
{
    /// <inheritdoc />
    public string Kind => ISubstanceResolver.SubstanceKind;

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> FindExistingAsync(
        IReadOnlyCollection<string> codes, ImportScope scope, CancellationToken ct)
    {
        var tables = await data.Data.TablesAsync(ct).ConfigureAwait(false);
        var found = await data.Data.SubstancesByCodeAsync(tables, codes, ct).ConfigureAwait(false);
        return found.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Public carrier of the internal reader, so DI-registered public classes can take it.</summary>
public sealed class AirLimitsSettingsAccessor(Ports.IRegistryStore registries, Ports.IRegistryRowsQuery rows, AirLimitsSettings settings)
{
    internal AirLimitsData Data { get; } = new(registries, rows, settings);
}
