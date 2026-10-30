// src/Ecr.Application/Permits/AirLimits/ProvisionAirLimitsHandler.cs
namespace Ecr.Application.Permits.AirLimits;

/// <summary>
/// Makes sure the "Air Limits Import" permit type (<c>AIR_LIMITS</c>, tables <c>AIR_SOURCES</c> and <c>AIR_SOURCE_SUBSTANCES</c>) is
/// published and the built-in import profile exists. Idempotent. SKELETON of the contract commit.
/// </summary>
public sealed class ProvisionAirLimitsHandler
{
    /// <summary>Provisions the type and the profile.</summary>
    /// <param name="ct">Cancellation token.</param>
    public Task<AirLimitsProvisionReport> HandleAsync(CancellationToken ct)
        => throw new NotImplementedException("Contract commit: implemented in the next commit of lane integ-nae-b.");
}