using Ecr.Application.Workflow;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Журнал переходів стану аркушів документа (<c>BE-11b</c>) і відкликання подання (<c>BE-31</c>).</summary>
/// <remarks>
/// ⚠ Окремий контролер під тим самим префіксом, що й <see cref="DocumentsController"/>:
/// той уже тримає шістнадцять обробників, і читання журналу до них не належить.
/// </remarks>
[ApiController]
[Route("api/v1/documents")]
[Authorize]
public sealed class DocumentWorkflowHistoryController(
    GetWorkflowHistoryHandler history,
    RecallSheetHandler recall) : ControllerBase
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

    /// <summary>
    /// Відкликання поданого аркуша автором: <c>Submitted → Draft</c> з причиною.
    /// Рівень гранта — той самий, що для подання.
    /// </summary>
    /// <remarks><c>409</c> — аркуш не поданий або перший крок маршруту вже підписано.</remarks>
    [HttpPost("{id:long}/recall")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Recall(long id, [FromBody] RecallSheetRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await recall
            .HandleAsync(id, request.SheetDefId, request.PeriodKey, request.Reason, ct)
            .ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>Чи може поточний користувач відкликати аркуш саме зараз — рішення сервера для кнопки.</summary>
    [HttpGet("{id:long}/recall")]
    [ProducesResponseType<RecallAvailabilityDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CanRecall(
        long id, [FromQuery] int sheetDefId, [FromQuery] int periodKey, CancellationToken ct)
        => Ok(await recall.CanRecallAsync(id, sheetDefId, periodKey, ct).ConfigureAwait(false));
}

/// <summary>Тіло відкликання аркуша.</summary>
/// <param name="SheetDefId">Аркуш.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="Reason">Причина; обов'язкова.</param>
public sealed record RecallSheetRequest(int SheetDefId, int PeriodKey, string Reason);
