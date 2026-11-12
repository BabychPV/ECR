using Ecr.Application.Permits.Import;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Ecr.Api.Controllers;

/// <summary>
/// «Paste from Excel…» / «Import file…» of a permit table (<c>PRM_&lt;type&gt;_&lt;table&gt;</c>): the saved column mapping, the
/// resolution of lookup values by code or name (with Region/Area derived from the NAE source) and an <c>.xlsx</c> sheet as text.
/// Nothing here writes rows: they go through <c>POST /registries/{code}/entries/batch</c> as before.
/// </summary>
[ApiController]
[Route("api/v1/permit-tables/{code}")]
[Authorize]
public sealed class PermitTableImportController(
    PermitImportMappingHandler mapping,
    PermitImportLookupHandler lookups,
    PermitImportWorkbookHandler workbook) : ControllerBase
{
    /// <summary>Largest accepted request body of the workbook upload (the workbook itself is capped at 5 MB by the handler).</summary>
    public const long MaxWorkbookBodyBytes = 6_000_000;

    /// <summary>
    /// The saved column mapping of the table (shared by all users; empty when none) with its NAE and derivable Region/Area columns.
    /// Permission <c>Registry.View</c> or grant <c>Read</c> on the table.
    /// </summary>
    /// <param name="code">Table registry code.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("import-mapping")]
    [ProducesResponseType<PermitImportMappingDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PermitImportMappingDto>> GetMapping(string code, CancellationToken ct)
        => Ok(await mapping.GetAsync(code, ct).ConfigureAwait(false));

    /// <summary>
    /// Creates or replaces the saved column mapping. An existing mapping needs <c>If-Match</c> with its <c>rowVersion</c>
    /// (changed meanwhile — <c>409</c>). Permission <c>Permit.EditType</c>.
    /// </summary>
    /// <param name="code">Table registry code.</param>
    /// <param name="request">File header → column code.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPut("import-mapping")]
    [ProducesResponseType<PermitImportMappingDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PermitImportMappingDto>> SaveMapping(
        string code, [FromBody] SavePermitImportMappingRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ifMatch = Request.Headers[HeaderNames.IfMatch].ToString();
        return Ok(await mapping.SaveAsync(code, request, ifMatch, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Resolves the distinct values of the lookup columns by entry code or name (any language); two or more entries with the name —
    /// <c>ambiguous</c>. A NAE column takes sources only. Derives Region/Area of each resolved NAE source from the NAE tree. Nothing
    /// is written. Permission <c>Registry.View</c> or grant <c>Read</c> on the table.
    /// </summary>
    /// <param name="code">Table registry code.</param>
    /// <param name="request">Distinct texts per column.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("import/lookups")]
    [ProducesResponseType<PermitImportLookupResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PermitImportLookupResultDto>> ResolveLookups(
        string code, [FromBody] PermitImportLookupRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(await lookups.HandleAsync(code, request, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// One sheet of an <c>.xlsx</c> (at most 5 MB) as text, with the list of its visible sheets; nothing is stored. Permission
    /// <c>Registry.EditData</c> or grant <c>Write</c> on the table.
    /// </summary>
    /// <param name="code">Table registry code.</param>
    /// <param name="file">The workbook.</param>
    /// <param name="parseGate">Shared limit of concurrent workbook parses (S-3).</param>
    /// <param name="sheet">Sheet name; empty — the first visible sheet.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("import/workbook")]
    [RequestSizeLimit(MaxWorkbookBodyBytes)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<PermitImportSheetDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PermitImportSheetDto>> ReadWorkbook(
        string code, IFormFile file, [FromServices] Ecr.Api.Security.ImportPreviewGate parseGate, [FromForm] string? sheet, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(parseGate);

        // S-3: workbook parse = full ClosedXML model in process memory; same limit as the document import preview (AN-123, P1-04).
        using var parsePermit = await parseGate.EnterAsync(ct).ConfigureAwait(false);
        await using var stream = file.OpenReadStream();
        return Ok(await workbook.HandleAsync(code, stream, file.Length, sheet, ct).ConfigureAwait(false));
    }
}
