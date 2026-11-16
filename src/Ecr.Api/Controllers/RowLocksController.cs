using Ecr.Application.Documents.RowLocks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// ✎ B75 (рішення людини 13.11): блокування рядків таблиці документа на проміжок дат — поки блокування діє, рядок не
/// змінює ніхто, крім того, хто заблокував; знімається само після кінцевої дати або власником чи носієм права.
/// </summary>
/// <remarks>
/// ⚠ Дати — у часовому поясі проєкту, кінцева включно. Правка заблокованого рядка відхиляється причиною
/// <c>RowLocked</c> (<c>403</c>, <c>deny.RowLocked</c>) на всіх шляхах запису: комірки, книга, створення й видалення
/// рядка. Перерахунок і збір (інтеграція) блокування не бачать.
/// </remarks>
[ApiController]
[Route("api/v1/documents/{documentId:long}/row-locks")]
[Authorize]
public sealed class RowLocksController(
    GetRowLocksHandler get,
    CreateRowLocksHandler create,
    ReleaseRowLockHandler release) : ControllerBase
{
    /// <summary>Незняті й не прострочені блокування документа за період (лише таблиці, які читач бачить).</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період екземпляра таблиці.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<RowLockDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<RowLockDto>>> List(
        long documentId, [FromQuery] int periodKey, CancellationToken ct)
        => Ok(await get.HandleAsync(documentId, periodKey, ct).ConfigureAwait(false));

    /// <summary>
    /// Блокує рядки таблиці (усе або нічого). Власник документа з рівнем Write на таблицю або носій права
    /// <c>Document.RowLock</c>; той, хто блокує, стає власником блокування.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="request">Рядки, дати, примітка.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// <c>403</c> — немає права, проєкт в архіві чи архівується, період закрито; <c>404</c> — документ, таблиця чи
    /// рядок відсутні або невидимі; <c>409 rowAlreadyLocked</c> — рядок уже заблоковано на цей час;
    /// <c>422 lockRangeInvalid</c> — проміжок порожній або минув.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<IReadOnlyList<RowLockDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IReadOnlyList<RowLockDto>>> Create(
        long documentId, [FromBody] CreateRowLocksRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await create.HandleAsync(documentId, request, ct).ConfigureAwait(false));
    }

    /// <summary>Знімає блокування (власник блокування або носій права <c>Document.RowLock</c>); історія лишається.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="lockId">Блокування.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpDelete("{lockId:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Release(long documentId, long lockId, CancellationToken ct)
    {
        await release.HandleAsync(documentId, lockId, ct).ConfigureAwait(false);

        return NoContent();
    }
}
