using Ecr.Application.Periods;
using Ecr.Application.Ports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Закритий місяць, відкритий лише для ОДНОГО документа (приймальна 08.11 (i); слово людини 08.11: «якщо мені потрібно
/// відкрити тільки для одного документу?»).
/// </summary>
/// <remarks>
/// ⚠ Період лишається закритим: решта документів місяця не редагується. Право — <c>Period.Reopen</c> у проєкті
/// документа; причина обов'язкова й потрапляє в журнал; строк діє сам, а <c>PeriodStateJob</c> позначає виняток
/// закритим (≤ 10 хв) і теж пише журнал.
/// </remarks>
[ApiController]
[Route("api/v1/documents/{documentId:long}/period-reopen")]
[Authorize]
public sealed class DocumentPeriodReopensController(
    GetDocumentPeriodReopenHandler get,
    OpenDocumentPeriodReopenHandler open,
    CloseDocumentPeriodReopenHandler close) : ControllerBase
{
    /// <summary>
    /// Стан місяця для документа: діючий виняток (якщо є), чи може поточний користувач відкрити місяць і чому ні,
    /// строк за замовчуванням (кінець доби майданчика).
    /// </summary>
    [HttpGet]
    [ProducesResponseType<DocumentPeriodReopenState>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentPeriodReopenState>> Get(
        long documentId, [FromQuery] int periodKey, CancellationToken ct)
        => Ok(await get.HandleAsync(documentId, periodKey, ct).ConfigureAwait(false));

    /// <summary>
    /// Відкриває закритий місяць лише для цього документа до <c>openUntil</c> (<c>null</c> — кінець доби майданчика).
    /// Діючий виняток замінюється новим строком і причиною.
    /// </summary>
    /// <remarks>
    /// <c>403</c> — немає права <c>Period.Reopen</c> у проєкті; <c>404</c> — документ невидимий або місяця немає;
    /// <c>409 ECR-PRD-0409</c> — місяць не закритий або проєкт архівований; <c>422 ECR-PRD-0422</c> — причина порожня
    /// чи задовга, строк не в майбутньому.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<DocumentPeriodReopenView>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<DocumentPeriodReopenView>> Open(
        long documentId, [FromBody] OpenDocumentPeriodReopenRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await open
            .HandleAsync(documentId, request.PeriodKey, request.Reason, request.OpenUntil, ct)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Закриває діючий виняток до строку («Close now» на вкладці Periods, приймальна i4): місяць знову закритий і для
    /// цього документа. Нічого відкритого — нічого не робить.
    /// </summary>
    /// <remarks>
    /// <c>403</c> — немає права <c>Period.Reopen</c> у проєкті; <c>404</c> — документ невидимий. Журнал
    /// <c>DocumentPeriodReopen/Close</c>.
    /// </remarks>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Close(long documentId, [FromQuery] int periodKey, CancellationToken ct)
    {
        await close.HandleAsync(documentId, periodKey, ct).ConfigureAwait(false);

        return NoContent();
    }
}

/// <summary>Запит на відкриття закритого місяця для документа.</summary>
/// <param name="PeriodKey">Місяць (РРРРММ).</param>
/// <param name="Reason">Причина; обов'язкова, потрапляє в журнал.</param>
/// <param name="OpenUntil">До якого моменту (UTC); <c>null</c> — до кінця доби майданчика. Безстрокового немає.</param>
public sealed record OpenDocumentPeriodReopenRequest(int PeriodKey, string Reason, DateTime? OpenUntil);
