using Ecr.Application.Documents;
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
    Ecr.Application.Audit.GetSheetActivityHandler sheetActivity,
    RecallSheetHandler recall,
    GetSubmissionTargetHandler submissionTarget,
    GetApproverEditAvailabilityHandler approverEdit) : ControllerBase
{
    /// <summary>Що подасть «Подати» на аркуші (D-441): найраніший неподаний період у стані <c>Open</c>/<c>Grace</c>, наступний неподаний, відкриті періоди для вибору і статуси місячних колонок. Право <c>Document.View</c>.</summary>
    /// <param name="id">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період, відкритий на екрані; його рік задає місяці колонок.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("{id:long}/submission-target")]
    [ProducesResponseType<SubmissionTargetDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SubmissionTarget(
        long id, [FromQuery] int sheetDefId, [FromQuery] int periodKey, CancellationToken ct)
        => Ok(await submissionTarget.HandleAsync(id, sheetDefId, periodKey, ct).ConfigureAwait(false));

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
    /// Журнал дій на аркуші за період (панель History): правки значень людьми (сітка, імпорт, правка погоджувача)
    /// і дії процесу з коментарем; найновіші перші, keyset (50 за замовчуванням, до 100). Право <c>Document.View</c>;
    /// аркуш, закритий читачеві, - <c>404</c>, як неіснуючий документ; закриті таблиці в журнал не потрапляють.
    /// </summary>
    /// <param name="id">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="author">Лише дії цього користувача (<c>UserId</c>); без нього - усі.</param>
    /// <param name="limit">Розмір сторінки; <c>0</c> - 50.</param>
    /// <param name="cursor">Курсор наступної сторінки.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("{id:long}/sheets/{sheetDefId:int}/activity")]
    [ProducesResponseType<Ecr.Application.Audit.SheetActivityPage>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SheetActivity(
        long id, int sheetDefId, [FromQuery] int periodKey, [FromQuery] int? author,
        [FromQuery] int limit, [FromQuery] string? cursor, CancellationToken ct)
        => Ok(await sheetActivity.HandleAsync(id, sheetDefId, periodKey, author, limit, cursor, ct).ConfigureAwait(false));

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

    /// <summary>
    /// Чи може поточний користувач змінювати значення поданого аркуша (D-442/D-445): лише погоджувач поточного
    /// кроку маршруту. Рішення сервера для сітки й пункту «Імпорт…»; запис перевіряє те саме правило. Право
    /// <c>Document.View</c>.
    /// </summary>
    /// <param name="id">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("{id:long}/approver-edit")]
    [ProducesResponseType<ApproverEditAvailabilityDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ApproverEdit(
        long id, [FromQuery] int sheetDefId, [FromQuery] int periodKey, CancellationToken ct)
        => Ok(await approverEdit.HandleAsync(id, sheetDefId, periodKey, ct).ConfigureAwait(false));
}

/// <summary>Тіло відкликання аркуша.</summary>
/// <param name="SheetDefId">Аркуш.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="Reason">Причина; обов'язкова.</param>
public sealed record RecallSheetRequest(int SheetDefId, int PeriodKey, string Reason);
