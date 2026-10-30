// src/Ecr.Application/Permits/AirLimits/GetAirLimitsPermitHandler.cs
namespace Ecr.Application.Permits.AirLimits;

/// <summary>Permit card content: sources with substances and totals. SKELETON of the contract commit.</summary>
public sealed class GetAirLimitsPermitHandler
{
    /// <summary>Reads the card. Permission <c>Registry.View</c> or grant <c>Read</c> on the permit's registry.</summary>
    /// <param name="permitId">Permit.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<AirLimitsPermitDto> HandleAsync(long permitId, CancellationToken ct)
        => throw new NotImplementedException("Contract commit.");
}