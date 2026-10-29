using Ecr.Application.Registries.Nae;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>NAE tree: one-time load from AF and the "where used" read (NAE plan N2).</summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class NaeTreeController(
    ImportNaeFromAfHandler import,
    GetRegistryEntryReferencesHandler references) : ControllerBase
{
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
}
