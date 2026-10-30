// src/Ecr.Application/Permits/AirLimits/AirImportProfileHandler.cs
namespace Ecr.Application.Permits.AirLimits;

/// <summary>Stored import profiles of the AIR_SOURCES table. SKELETON of the contract commit.</summary>
public sealed class AirImportProfileHandler
{
    /// <summary>Permission to read profiles.</summary>
    public const string ViewPermission = "Registry.View";

    /// <summary>Permission to save profiles.</summary>
    public const string EditPermission = "Registry.EditDefinition";

    /// <summary>Profiles of the table, by code. Permission <c>Registry.View</c>.</summary>
    /// <param name="ct">Cancellation token.</param>
    public Task<IReadOnlyList<ImportProfileSummaryDto>> ListAsync(CancellationToken ct)
        => throw new NotImplementedException("Contract commit.");

    /// <summary>One profile with its specification. Permission <c>Registry.View</c>.</summary>
    /// <param name="code">Profile code.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<ImportProfileDto> GetAsync(string code, CancellationToken ct)
        => throw new NotImplementedException("Contract commit.");

    /// <summary>Creates or replaces a profile. Permission <c>Registry.EditDefinition</c>.</summary>
    /// <param name="code">Profile code.</param>
    /// <param name="request">Name, specification, token.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<ImportProfileDto> SaveAsync(string code, SaveImportProfileRequest request, CancellationToken ct)
        => throw new NotImplementedException("Contract commit.");
}