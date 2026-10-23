using Ecr.Application.Documents.EditAssignments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Власник документа (D-443): власник або менеджер проєкту передає власність іншому користувачеві з правом
/// запису в проєкті. Подія <c>DocumentOwnerTransferred</c> — у журналі безпеки.
/// </summary>
/// <remarks>
/// ⛔ Невидимий документ — <c>404</c>, як і решта ендпоінтів документа; новий власник, якого не можна призначити
/// (немає, вимкнений, без права запису в проєкті), — одна відповідь <c>422 documentOwnerInvalid</c>.
/// </remarks>
[ApiController]
[Route("api/v1/documents/{documentId:long}/owner")]
[Authorize]
public sealed class DocumentOwnerController(TransferDocumentOwnerHandler transfer) : ControllerBase
{
    /// <summary>Передає власність документа.</summary>
    /// <remarks>
    /// <c>403</c> — викликач не власник і не менеджер проєкту; <c>409</c> — власника змінили одночасно;
    /// <c>422</c> — нового власника не можна призначити.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<DocumentOwnerDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Transfer(
        long documentId, [FromBody] TransferDocumentOwnerRequest request, CancellationToken ct)
        => Ok(await transfer.HandleAsync(documentId, request, ct).ConfigureAwait(false));
}
