using System.Text.Json;
using System.Text.Json.Serialization;
using Ecr.Application.Errors;
using Ecr.Application.Permits.AirLimits;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// "Air Limits Import" permits: provisioning of the type, import profiles, the Excel import wizard backend (analyse, preview,
/// apply) and the permit card content (NAE plan N3).
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class AirLimitsController(
    ProvisionAirLimitsHandler provision,
    AirImportProfileHandler profiles,
    AirLimitsImportHandler import,
    GetAirLimitsPermitHandler permit) : ControllerBase
{
    /// <summary>Largest accepted request body (the workbook is capped separately by the safety gate).</summary>
    public const long MaxImportBodyBytes = 30_000_000;

    private static readonly JsonSerializerOptions OptionsJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Makes sure the permit type <c>AIR_LIMITS</c> ("Air Limits Import") is published with its two tables and that the built-in
    /// import profile <c>ERA_TABLE_33</c> exists. Needs <c>Permit.EditType</c> and <c>Permit.PublishType</c>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Idempotent: a published type is left as it is (it is never edited by this call). The substance registry the type points at
    /// (<c>LAND_AIR_SUBSTANCE</c> by default) must exist, otherwise <c>422 ECR-PRM-0422</c>.
    /// </remarks>
    [HttpPost("air-limits/provision")]
    [ProducesResponseType<AirLimitsProvisionReport>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AirLimitsProvisionReport>> Provision(CancellationToken ct)
        => Ok(await provision.HandleAsync(ct).ConfigureAwait(false));

    /// <summary>Import profiles of the sources table. Permission <c>Registry.View</c>.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("air-limits/import-profiles")]
    [ProducesResponseType<IReadOnlyList<ImportProfileSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ImportProfileSummaryDto>>> ListProfiles(CancellationToken ct)
        => Ok(await profiles.ListAsync(ct).ConfigureAwait(false));

    /// <summary>One import profile with its specification. Permission <c>Registry.View</c>.</summary>
    /// <param name="code">Profile code.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("air-limits/import-profiles/{code}")]
    [ProducesResponseType<ImportProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ImportProfileDto>> GetProfile(string code, CancellationToken ct)
        => Ok(await profiles.GetAsync(code, ct).ConfigureAwait(false));

    /// <summary>Creates or replaces an import profile. Permission <c>Registry.EditDefinition</c>.</summary>
    /// <param name="code">Profile code.</param>
    /// <param name="request">Name, specification, token.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPut("air-limits/import-profiles/{code}")]
    [ProducesResponseType<ImportProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ImportProfileDto>> SaveProfile(
        string code, [FromBody] SaveImportProfileRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(await profiles.SaveAsync(code, request, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Wizard steps 1-3: reads the workbook with a profile (or an unsaved specification) and reports the header block, the
    /// mapped and unmapped columns, counts and findings. Stateless: nothing is stored. Permission <c>Registry.EditData</c>.
    /// </summary>
    /// <param name="file">The <c>.xlsx</c> workbook.</param>
    /// <param name="request">JSON <see cref="AirLimitsImportOptions"/> (profile or specification).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("air-limits/import/analyse")]
    [RequestSizeLimit(MaxImportBodyBytes)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<AirLimitsAnalysisDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AirLimitsAnalysisDto>> Analyse(
        IFormFile file, [FromForm] string? request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        var options = ParseOptions(request);
        await using var stream = file.OpenReadStream();
        return Ok(await import.AnalyseAsync(stream, options, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Wizard step 4: preview of the import into a permit. Nothing is written. Permission <c>Registry.EditData</c> on the permit's
    /// tables.
    /// </summary>
    /// <param name="permitId">Permit (header registry entry id).</param>
    /// <param name="file">The <c>.xlsx</c> workbook.</param>
    /// <param name="request">JSON <see cref="AirLimitsImportOptions"/>: profile, region, area, decisions.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Errors block the apply (<c>canApply = false</c>); warnings (for example an empty <c>tonsPerYear</c>) are listed per row and do
    /// not. <c>replace</c> lists the permit's own rows and the rows of OTHER permits for the same NAE that the apply would replace.
    /// </remarks>
    [HttpPost("permits/{permitId:long}/air-limits/import/preview")]
    [RequestSizeLimit(MaxImportBodyBytes)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<AirLimitsPreviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AirLimitsPreviewDto>> Preview(
        long permitId, IFormFile file, [FromForm] string? request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        var options = ParseOptions(request);
        await using var stream = file.OpenReadStream();
        return Ok(await import.PreviewAsync(permitId, stream, options, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Wizard step 5: applies the import in ONE transaction (delete the replaced rows, create the missing NAE nodes, insert).
    /// Permission <c>Registry.EditData</c>.
    /// </summary>
    /// <param name="permitId">Permit (header registry entry id).</param>
    /// <param name="file">The same <c>.xlsx</c> workbook as in the preview.</param>
    /// <param name="request">JSON <see cref="AirLimitsImportOptions"/> with <c>confirmedOtherOwners</c> and <c>expectedFingerprint</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// All or nothing: an error row, an unconfirmed replacement of another permit (<c>regReplaceNotConfirmed</c>) or a plan that
    /// differs from the previewed one (<c>regPlanChanged</c>) writes nothing.
    /// </remarks>
    [HttpPost("permits/{permitId:long}/air-limits/import/apply")]
    [RequestSizeLimit(MaxImportBodyBytes)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<AirLimitsImportResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AirLimitsImportResultDto>> Apply(
        long permitId, IFormFile file, [FromForm] string? request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        var options = ParseOptions(request);
        await using var stream = file.OpenReadStream();
        return Ok(await import.ApplyAsync(permitId, stream, options, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Permit card content: sources (NAE) with their substances and totals per substance. Permission <c>Registry.View</c> or grant
    /// <c>Read</c> on the permit's registry.
    /// </summary>
    /// <param name="permitId">Permit (header registry entry id).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("permits/{permitId:long}/air-limits")]
    [ProducesResponseType<AirLimitsPermitDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AirLimitsPermitDto>> GetPermit(long permitId, CancellationToken ct)
        => Ok(await permit.HandleAsync(permitId, ct).ConfigureAwait(false));

    private static AirLimitsImportOptions ParseOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AirLimitsImportOptions(null, null, null, null, null, null, null);
        }

        try
        {
            return JsonSerializer.Deserialize<AirLimitsImportOptions>(json, OptionsJson)
                   ?? new AirLimitsImportOptions(null, null, null, null, null, null, null);
        }
        catch (JsonException ex)
        {
            throw new BusinessRuleException(
                "ECR-IMP-0422",
                $"The import request is not valid JSON: {ex.Message}",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-IMP-0422.airRequestInvalid" });
        }
    }
}
