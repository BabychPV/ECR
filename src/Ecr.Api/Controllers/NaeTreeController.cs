using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Application.Registries.Nae;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// NAE tree: one-time load from AF, manual roots, Excel import (no AF) and the "where used" read (NAE plan N2).
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class NaeTreeController(
    ImportNaeFromAfHandler import,
    NaeTreeManualHandler manual,
    GetRegistryEntryReferencesHandler references) : ControllerBase
{
    /// <summary>Largest accepted request body (the workbook is capped separately by the safety gate).</summary>
    public const long MaxImportBodyBytes = 30_000_000;

    private static readonly JsonSerializerOptions OptionsJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Loads the NAE tree from the PI AF equipment tree (elements with <c>Region</c> / <c>On_Offshore</c> attributes). Needs
    /// <c>Registry.EditDefinition</c> and <c>Registry.EditData</c>.
    /// </summary>
    /// <param name="request">Source, root element, target registry, dry-run flag.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Idempotent and create-only: after the first load ECR owns the tree, so existing nodes are never changed or moved.
    /// <c>dryRun</c> builds the report without writing anything (not even the registry). The first applied call also creates
    /// the registry <c>NAE_TREE</c> (levels, fields, composite key) when it does not exist.
    /// </remarks>
    [HttpPost("nae-tree/af-import")]
    [ProducesResponseType<NaeAfImportReport>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<NaeAfImportReport>> ImportFromAf(
        [FromBody] ImportNaeFromAfRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(await import.HandleAsync(request, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Creates a region root (and, optionally, one area under it) of the NAE tree by hand — the first node of an empty tree
    /// without AF. Needs <c>Registry.EditData</c>; the call that provisions the registry also needs
    /// <c>Registry.EditDefinition</c>.
    /// </summary>
    /// <param name="request">Region, optional area, names.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Idempotent and create-only: a region or area that already exists (technical code <c>R_&lt;REGION&gt;</c> /
    /// <c>A_&lt;REGION&gt;_&lt;AREA&gt;</c>, the same as the AF load) is returned as it is, never renamed.
    /// </remarks>
    [HttpPost("nae-tree/roots")]
    [ProducesResponseType<NaeRootResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<NaeRootResultDto>> CreateRoot([FromBody] CreateNaeRootRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(await manual.CreateRootAsync(request, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Creates one region root per live entry of the <c>LAND_REGION</c> registry. Only for an EMPTY tree (otherwise
    /// <c>422 naeTreeNotEmpty</c>); an explicit action, never automatic. Same permissions as <c>POST nae-tree/roots</c>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("nae-tree/seed-roots")]
    [ProducesResponseType<NaeSeedRootsResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<NaeSeedRootsResultDto>> SeedRoots(CancellationToken ct)
        => Ok(await manual.SeedRootsAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// Empty Excel template of the NAE tree import: <c>Region | Area | Site | Equipment | NAE code | Name_En | Name_Ru |
    /// Name_Kz | On_Offshore</c>. Permission <c>Registry.View</c>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("nae-tree/import/template")]
    // ⚠ The response is a FILE, not JSON.
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(FileResult))]
    [Produces(NaeTreeImportLayout.ContentType)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Template(CancellationToken ct)
    {
        var bytes = await manual.TemplateAsync(ct).ConfigureAwait(false);
        return File(bytes, NaeTreeImportLayout.ContentType, NaeTreeImportLayout.FileName);
    }

    /// <summary>
    /// Reads an NAE tree workbook (the template layout) and reports rows, planned nodes and file findings. The database is not
    /// consulted and nothing is written. Permission <c>Registry.EditData</c>.
    /// </summary>
    /// <param name="file">The <c>.xlsx</c> workbook.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("nae-tree/import/analyse")]
    [RequestSizeLimit(MaxImportBodyBytes)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<NaeTreeImportReportDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<NaeTreeImportReportDto>> AnalyseImport(IFormFile file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        await using var stream = file.OpenReadStream();
        return Ok(await manual.AnalyseAsync(stream, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Preview of the NAE tree import over the stored tree: nodes to create, nodes already there (left untouched; a differing
    /// stored name is a warning), conflicts by business key (NAE code + region + area). Nothing is written. Permission
    /// <c>Registry.EditData</c>.
    /// </summary>
    /// <param name="file">The <c>.xlsx</c> workbook.</param>
    /// <param name="request">JSON <see cref="NaeTreeImportOptions"/> (the preview ignores the fingerprint).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("nae-tree/import/preview")]
    [RequestSizeLimit(MaxImportBodyBytes)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<NaeTreeImportReportDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<NaeTreeImportReportDto>> PreviewImport(
        IFormFile file, [FromForm] string? request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        var options = ParseOptions(request);
        await using var stream = file.OpenReadStream();
        return Ok(await manual.PreviewAsync(stream, options, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Applies the NAE tree import in ONE transaction: all planned nodes or none. Permission <c>Registry.EditData</c> (and
    /// <c>Registry.EditDefinition</c> when the registry does not exist yet).
    /// </summary>
    /// <param name="file">The same <c>.xlsx</c> workbook as in the preview.</param>
    /// <param name="request">JSON <see cref="NaeTreeImportOptions"/> with <c>expectedFingerprint</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// An error finding (<c>422 naeImportHasErrors</c>) or a plan that differs from the previewed one (<c>422 regPlanChanged</c>)
    /// writes nothing. Existing nodes are never changed: a second apply of the same file is a no-op (<c>applied = false</c>).
    /// </remarks>
    [HttpPost("nae-tree/import/apply")]
    [RequestSizeLimit(MaxImportBodyBytes)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<NaeTreeImportReportDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<NaeTreeImportReportDto>> ApplyImport(
        IFormFile file, [FromForm] string? request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        var options = ParseOptions(request);
        await using var stream = file.OpenReadStream();
        return Ok(await manual.ApplyAsync(stream, options, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Where a registry entry is used (permits, type, validity, substance count). Permission <c>Registry.View</c> or grant
    /// <c>Read</c>.
    /// </summary>
    /// <param name="code">Registry code.</param>
    /// <param name="id">Entry.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Empty until the permit tables point at registry entries (plan N3). An entry of another registry is the same 404 as a
    /// missing one.
    /// </remarks>
    [HttpGet("registries/{code}/entries/{id:long}/references")]
    [ProducesResponseType<RegistryEntryReferencesDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RegistryEntryReferencesDto>> References(string code, long id, CancellationToken ct)
        => Ok(await references.HandleAsync(code, id, ct).ConfigureAwait(false));

    private static NaeTreeImportOptions ParseOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new NaeTreeImportOptions(null);
        }

        try
        {
            return JsonSerializer.Deserialize<NaeTreeImportOptions>(json, OptionsJson) ?? new NaeTreeImportOptions(null);
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
