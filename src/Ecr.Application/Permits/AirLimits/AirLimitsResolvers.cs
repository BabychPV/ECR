// src/Ecr.Application/Permits/AirLimits/AirLimitsResolvers.cs
using Ecr.Application.RegistryImport;

namespace Ecr.Application.Permits.AirLimits;

/// <summary>Splits an import scope key (<c>REGION/AREA</c>).</summary>
internal static class AirScope
{
    /// <summary>Scope key of a region and area.</summary>
    public static string Key(string region, string area) => $"{region}/{area}";

    /// <summary>Region and area of a scope key.</summary>
    public static (string Region, string Area) Parse(string scopeKey)
    {
        var parts = scopeKey.Split('/', 2);
        return (parts[0], parts.Length > 1 && parts[1].Length > 0 ? parts[1] : AirLimitsSchema.NoArea);
    }
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
        var (region, area) = AirScope.Parse(scope.ScopeKey);
        var tables = await data.Data.TablesAsync(ct).ConfigureAwait(false);
        var found = await data.Data.NaeByCodeAsync(tables, codes, region, area, ct).ConfigureAwait(false);
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
