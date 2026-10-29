using Ecr.Application.Registries.Hierarchy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Hierarchical registries: levels, tree, move, soft retire (NAE plan N1).</summary>
/// <remarks>
/// The registry is in the route as in the other registry routes: the permission (or the resource grant) is checked
/// on exactly that registry, and entries are checked to belong to it (a foreign entry is the same 404 as a missing one).
/// </remarks>
[ApiController]
[Route("api/v1/registries/{code}")]
[Authorize]
public sealed class RegistryHierarchyController(
    GetRegistryHierarchyHandler getHierarchy,
    SaveRegistryHierarchyHandler saveHierarchy,
    GetRegistryTreeHandler getTree,
    MoveRegistryEntryHandler move,
    RetireRegistryEntryHandler retire) : ControllerBase
{
    /// <summary>Hierarchy settings and levels. Permission <c>Registry.View</c> or grant <c>Read</c>.</summary>
    /// <param name="code">Registry code.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("hierarchy")]
    [ProducesResponseType<RegistryHierarchyDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RegistryHierarchyDto>> Hierarchy(string code, CancellationToken ct)
        => Ok(await getHierarchy.HandleAsync(code, ct).ConfigureAwait(false));

    /// <summary>
    /// Saves hierarchy settings (all-or-nothing replacement of the level list). Permission <c>Registry.EditDefinition</c>
    /// or grant <c>Write</c>.
    /// </summary>
    /// <param name="code">Registry code.</param>
    /// <param name="request">Settings.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// A level in use cannot be removed (<c>409 ECR-REG-0409 levelInUse</c>); a change that would leave an entry in an
    /// invalid place is <c>422 ECR-REG-0422</c>. At most <c>MaxDepth</c> levels.
    /// </remarks>
    [HttpPut("hierarchy")]
    [ProducesResponseType<RegistryHierarchyDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RegistryHierarchyDto>> SaveHierarchy(
        string code, [FromBody] SaveRegistryHierarchyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(await saveHierarchy.HandleAsync(code, request, ct).ConfigureAwait(false));
    }

    /// <summary>The tree as a flat node list. Permission <c>Registry.View</c> or grant <c>Read</c>.</summary>
    /// <param name="code">Registry code.</param>
    /// <param name="asOf">Only nodes valid on this day; default — today.</param>
    /// <param name="includeRetired">Also nodes outside the window and switched-off nodes.</param>
    /// <param name="q">Case-insensitive substring of code or name; matches are returned with their ancestors.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("tree")]
    [ProducesResponseType<RegistryTreeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RegistryTreeDto>> Tree(
        string code,
        [FromQuery] DateOnly? asOf,
        [FromQuery] bool includeRetired,
        [FromQuery] string? q,
        CancellationToken ct)
        => Ok(await getTree.HandleAsync(code, asOf, includeRetired, q, ct).ConfigureAwait(false));

    /// <summary>Moves an entry within the tree. Permission <c>Registry.EditData</c> or grant <c>Write</c>.</summary>
    /// <param name="code">Registry code.</param>
    /// <param name="id">Entry.</param>
    /// <param name="request">Target place.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// <c>422 ECR-REG-0422</c>: level order violated (a node sits only on a deeper level than its parent, skipped levels
    /// must be optional), cycle, depth. One audit record (<c>Move</c>) per move.
    /// </remarks>
    [HttpPost("entries/{id:long}/move")]
    [ProducesResponseType<MoveRegistryEntryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MoveRegistryEntryResponse>> Move(
        string code, long id, [FromBody] MoveRegistryEntryRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(await move.HandleAsync(code, id, request, ct).ConfigureAwait(false));
    }

    /// <summary>Retires a node softly. Permission <c>Registry.EditData</c> or grant <c>Write</c>.</summary>
    /// <param name="code">Registry code.</param>
    /// <param name="id">Entry.</param>
    /// <param name="request">Retire date and cascade flag.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Never deletes: the validity window is closed (documents keep reading the node through <c>asOf</c>).
    /// A node with live children is <c>409 ECR-REG-0409 nodeHasLiveChildren</c> unless <c>cascade</c>.
    /// </remarks>
    [HttpPost("entries/{id:long}/retire")]
    [ProducesResponseType<RetireRegistryEntryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RetireRegistryEntryResponse>> Retire(
        string code, long id, [FromBody] RetireRegistryEntryRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(await retire.HandleAsync(code, id, request, ct).ConfigureAwait(false));
    }
}
