using Ecr.Application.Workflow;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Журнал переходів стану аркушів документа (<c>BE-11b</c>).</summary>
/// <remarks>
/// ⚠ Окремий контролер під тим самим префіксом, що й <see cref="DocumentsController"/>:
/// той уже тримає шістнадцять обробників, і читання журналу до них не належить.
/// </remarks>
[ApiController]
[Route("api/v1/documents")]
[Authorize]
public sealed class DocumentWorkflowHistoryController(GetWorkflowHistoryHandler history) : ControllerBase
{
    /// <summary>
    /// Хто й коли подав, погодив, відхилив або повернув аркуші документа за
    /// період; найновіші перші, не більше 200 подій. Право <c>Document.View</c>.
    /// </summary>
    /// <param name="id">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("{id:long}/workflow/history")]
    [ProducesResponseType<IReadOnlyList<WorkflowEventDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> History(long id, [FromQuery] int periodKey, CancellationToken ct)
        => Ok(await history.HandleAsync(id, periodKey, ct).ConfigureAwait(false));
}
