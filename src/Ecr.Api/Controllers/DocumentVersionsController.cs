using Ecr.Application.Documents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Версії документа (зрізи подання) і їх порівняння (ФВ-5.22).</summary>
[ApiController]
[Route("api/v1/documents")]
[Authorize]
public sealed class DocumentVersionsController(
    ListDocumentVersionsHandler list,
    CompareDocumentVersionsHandler compare) : ControllerBase
{
    /// <summary>Зрізи подання документа за період, найновіші перші, не більше 200. Право <c>Document.View</c>.</summary>
    [HttpGet("{id:long}/versions")]
    [ProducesResponseType<IReadOnlyList<DocumentVersionDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Versions(long id, [FromQuery] int periodKey, CancellationToken ct)
        => Ok(await list.HandleAsync(id, periodKey, ct).ConfigureAwait(false));

    /// <summary>
    /// Різниця версії <c>from</c> і версії <c>to</c> (або поточного стану, <c>to=current</c>):
    /// змінені комірки, додані й видалені рядки; кожен перелік — не більше 1000, решта — <c>truncated</c>.
    /// </summary>
    [HttpGet("{id:long}/compare")]
    [ProducesResponseType<DocumentCompareDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Compare(
        long id, [FromQuery] long from, [FromQuery] string? to, CancellationToken ct)
        => Ok(await compare.HandleAsync(id, from, to, ct).ConfigureAwait(false));
}
