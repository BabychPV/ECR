// src/Ecr.Application/Permits/AirLimits/AirScopeColumns.cs
using Ecr.Domain.Entities.Dictionaries;

namespace Ecr.Application.Permits.AirLimits;

/// <summary>A scope column of the AIR sources table as configured on the type.</summary>
/// <param name="ColumnCode"><c>REGION</c> or <c>AREA</c>.</param>
/// <param name="Kind"><see cref="AirLimitsSchema.KindText"/>, <see cref="AirLimitsSchema.KindRegistry"/> or <see cref="AirLimitsSchema.KindList"/>.</param>
/// <param name="RegistryCode">Registry the values come from (Registry / List); <c>null</c> for Text.</param>
public sealed record AirScopeColumn(string ColumnCode, string Kind, string? RegistryCode);

/// <summary>
/// ✎ AIR-ON-AIR: reads how REGION and AREA are configured. Defaults (no AIR type configuration yet): REGION = Registry
/// <see cref="AirLimitsSettings.RegionRegistryCode"/>, AREA = Registry <see cref="AirLimitsSettings.AreaRegistryCode"/>.
/// </summary>
public sealed class AirScopeColumns(AirLimitsSettings settings)
{
    /// <summary>Both scope columns.</summary>
    /// <param name="ct">Cancellation token.</param>
    public Task<(AirScopeColumn Region, AirScopeColumn Area)> ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult((
            new AirScopeColumn(AirLimitsSchema.Region, AirLimitsSchema.KindRegistry, settings.RegionRegistryCode),
            new AirScopeColumn(AirLimitsSchema.Area, AirLimitsSchema.KindRegistry, settings.AreaRegistryCode)));
    }

    /// <summary>English name of an entry, else its code.</summary>
    public static string NameOf(RegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.DisplayL10n.Get("en") ?? entry.Code;
    }
}
