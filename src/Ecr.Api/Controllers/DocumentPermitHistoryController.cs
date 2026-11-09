using Ecr.Application.Permits.History;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Feature (j), human 08.11: a permit changes mid-month — the row (or the document header) keeps its permit history
/// «KZ…A until 14.02 · KZ…B from 15.02» in the same document; the month shared by two permits is split by the user.
/// </summary>
/// <remarks>
/// ⚠ Who may add: anyone who may edit the row's permit cell (a header: the header write right); the permit registry
/// itself stays admin-only. Ship 1: a header change only on the 1st — mid-month is refused naming the rows to change
/// instead.
/// </remarks>
[ApiController]
[Route("api/v1/documents/{documentId:long}/permit-history")]
[Authorize]
public sealed class DocumentPermitHistoryController(
    GetPermitHistoryHandler get,
    AddRowPermitHandler addRow,
    UndoRowPermitHandler undoRow,
    AddHeaderPermitHandler addHeader,
    UndoHeaderPermitHandler undoHeader,
    SaveCellSplitsHandler splits) : ControllerBase
{
    /// <summary>
    /// j2/jh3: saves the parts of month cells shared by two permits — typed by the user, never divided by days. A cell with
    /// every part entered gets the total (sum of the parts) through the ordinary cell write; partial save keeps the old value.
    /// </summary>
    /// <remarks>
    /// <c>403 ECR-ACCS-0403</c> — a month cell not editable; <c>409 ECR-CELL-0409</c> — the row changed (<c>baseVersion</c>);
    /// <c>422 ECR-REQ-0422 permitSplitInvalid</c> — no history, the month is not shared, wrong number of parts.
    /// </remarks>
    [HttpPut("splits")]
    [ProducesResponseType<DocumentPermitHistoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<DocumentPermitHistoryDto>> SaveSplits(
        long documentId, [FromBody] SaveCellSplitsRequest request, CancellationToken ct)
        => Ok(await splits.HandleAsync(documentId, request, ct).ConfigureAwait(false));

    /// <summary>
    /// The document's permit history (header fields and visible rows that have one) and the cells that still need to be
    /// split between two permits, read in <paramref name="periodKey"/>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<DocumentPermitHistoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentPermitHistoryDto>> Get(long documentId, [FromQuery] int periodKey, CancellationToken ct)
        => Ok(await get.HandleAsync(documentId, periodKey, ct).ConfigureAwait(false));

    /// <summary>j1: adds a permit to a row's permit cell, in force from <c>validFrom</c>.</summary>
    /// <remarks>
    /// <c>403 ECR-ACCS-0403</c> — the permit cell is not editable for the user; <c>404</c> — invisible document/table;
    /// <c>422 ECR-CELL-4223 permitGap</c> — days between the permits without a valid permit (named); <c>422
    /// ECR-CELL-4223 missingEntry</c> — the permit cannot be chosen here; <c>422 ECR-REQ-0422</c> — not a permit column,
    /// no row/base permit, or the date is outside the window or not after the latest change.
    /// </remarks>
    [HttpPost("rows")]
    [ProducesResponseType<RowPermitHistoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RowPermitHistoryDto>> AddRow(
        long documentId, [FromBody] AddRowPermitRequest request, CancellationToken ct)
        => Ok(await addRow.HandleAsync(documentId, request, ct).ConfigureAwait(false));

    /// <summary>Undoes the latest permit added to a row's permit cell.</summary>
    /// <remarks><c>422 ECR-REQ-0422 permitHistoryNothingToUndo</c> — nothing added, or its months are already split.</remarks>
    [HttpPost("rows/undo")]
    [ProducesResponseType<RowPermitHistoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RowPermitHistoryDto>> UndoRow(
        long documentId, [FromBody] UndoRowPermitRequest request, CancellationToken ct)
        => Ok(await undoRow.HandleAsync(documentId, request, ct).ConfigureAwait(false));

    /// <summary>jh1: adds a permit to a header field, in force from <c>validFrom</c> (Ship 1: the 1st of a month).</summary>
    /// <remarks>
    /// <c>422 ECR-HDR-4223 permitChangeMidMonth</c> — a date not on the 1st: add the permit per row (the rows relying on
    /// the header permit with data in that month are named); <c>422 ECR-HDR-4223 permitGap</c> — uncovered days named.
    /// </remarks>
    [HttpPost("header")]
    [ProducesResponseType<HeaderPermitHistoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<HeaderPermitHistoryDto>> AddHeader(
        long documentId, [FromBody] AddHeaderPermitRequest request, CancellationToken ct)
        => Ok(await addHeader.HandleAsync(documentId, request, ct).ConfigureAwait(false));

    /// <summary>Undoes the latest permit added to a header field.</summary>
    [HttpPost("header/undo")]
    [ProducesResponseType<HeaderPermitHistoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<HeaderPermitHistoryDto>> UndoHeader(
        long documentId, [FromBody] UndoHeaderPermitRequest request, CancellationToken ct)
        => Ok(await undoHeader.HandleAsync(documentId, request, ct).ConfigureAwait(false));
}
