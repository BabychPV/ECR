using Ecr.Application.Documents.EditAssignments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Передача місяця (зрізу документа) виконавцям для заповнення: власник передає, виконавець ставить
/// «Заповнено», власник валідує або повертає з причиною (EA-C).
/// </summary>
/// <remarks>
/// ⛔ Невидимий документ, чужа передача, передача іншого документа й не-виконавець на <c>fill</c> —
/// одна відповідь <c>404</c>. Права керування — автор документа або <c>Manage</c> на проєкт.
/// </remarks>
[ApiController]
[Route("api/v1/documents/{documentId:long}/edit-assignments")]
[Authorize]
public sealed class DocumentEditAssignmentsController(
    ListEditAssignmentsHandler list,
    UpsertEditAssignmentHandler upsert,
    DeleteEditAssignmentHandler delete,
    FillEditAssignmentHandler fill,
    ValidateEditAssignmentHandler validate,
    ReturnEditAssignmentHandler returnBack,
    ListEditAssignmentCandidatesHandler candidates) : ControllerBase
{
    /// <summary>Передачі документа (для автора й менеджера проєкту).</summary>
    [HttpGet]
    [ProducesResponseType<EditAssignmentListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(long documentId, CancellationToken ct)
        => Ok(await list.HandleAsync(documentId, ct).ConfigureAwait(false));

    /// <summary>Створює або змінює передачу зрізу (upsert за зрізом; повтор ідемпотентний).</summary>
    /// <remarks>
    /// <c>422</c> — вихід за власні права, виконавець без доступу до документа, некоректний зріз;
    /// <c>409</c> — застаріла <c>baseVersion</c>.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<EditAssignmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Upsert(
        long documentId, [FromBody] UpsertEditAssignmentRequest request, CancellationToken ct)
        => Ok(await upsert.HandleAsync(documentId, request, ct).ConfigureAwait(false));

    /// <summary>Знімає передачу.</summary>
    [HttpDelete("{assignmentId:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(long documentId, long assignmentId, CancellationToken ct)
    {
        await delete.HandleAsync(documentId, assignmentId, ct).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>«Заповнено» — лише сам виконавець; повтор — 200 без змін.</summary>
    [HttpPost("{assignmentId:long}/fill")]
    [ProducesResponseType<EditAssignmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Fill(long documentId, long assignmentId, CancellationToken ct)
        => Ok(await fill.HandleAsync(documentId, assignmentId, ct).ConfigureAwait(false));

    /// <summary>Валідація власником: усі виконавці мають бути «Заповнено».</summary>
    [HttpPost("{assignmentId:long}/validate")]
    [ProducesResponseType<EditAssignmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Validate(
        long documentId, long assignmentId, [FromBody] ValidateEditAssignmentRequest request, CancellationToken ct)
        => Ok(await validate.HandleAsync(documentId, assignmentId, request, ct).ConfigureAwait(false));

    /// <summary>Повернення на доопрацювання з обов'язковою причиною (допустимо й після валідації).</summary>
    [HttpPost("{assignmentId:long}/return")]
    [ProducesResponseType<EditAssignmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Return(
        long documentId, long assignmentId, [FromBody] ReturnEditAssignmentRequest request, CancellationToken ct)
        => Ok(await returnBack.HandleAsync(documentId, assignmentId, request, ct).ConfigureAwait(false));

    /// <summary>Пошук кандидатів у виконавці (≥ 2 символи, ≤ 20; без email і SID).</summary>
    [HttpGet("candidates")]
    [ProducesResponseType<IReadOnlyList<EditAssignmentCandidateDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Candidates(long documentId, [FromQuery] string? q, CancellationToken ct)
        => Ok(await candidates.HandleAsync(documentId, q, ct).ConfigureAwait(false));
}
