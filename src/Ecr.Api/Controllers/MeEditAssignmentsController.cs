using Ecr.Application.Documents.EditAssignments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>«Мої задачі» за передачами місяця: «Assigned to me» і «Awaiting my validation».</summary>
[ApiController]
[Route("api/v1/me/edit-assignments")]
[Authorize]
public sealed class MeEditAssignmentsController(ListMyEditAssignmentsHandler mine) : ControllerBase
{
    /// <summary>
    /// Мої передачі: <c>role=assignee</c> — виконати; <c>role=owner</c> — чекають моєї валідації;
    /// без <c>role</c> — обидва списки. Лише рядки самого викликача, без імен інших виконавців.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<MyEditAssignmentsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Get(
        [FromQuery] string? role, [FromQuery] long? documentId, CancellationToken ct)
        => Ok(await mine.HandleAsync(role, documentId, ct).ConfigureAwait(false));
}
