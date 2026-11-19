using Ecr.Application.Calculations.SourceBindings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// B80 (N1): зв'язки «документ-джерело → довідник» і стан-ворота читання (HSE400 → Land). Право
/// <c>Calculation.ManageSourceBinding</c> (з вбудованих ролей — лише <c>SystemAdministrator</c>).
/// </summary>
[ApiController]
[Route("api/v1/document-source-bindings")]
[Authorize]
public sealed class DocumentSourceBindingsController(
    ListDocumentSourceBindingsHandler list,
    SwitchDocumentSourceStateHandler switchState,
    SwitchDocumentSourceFlagsHandler switchFlags) : ControllerBase
{
    /// <summary>Перелік зв'язок зі станом-воротами і дієвим читанням. Право <c>Calculation.ManageSourceBinding</c>.</summary>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<DocumentSourceBindingDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<DocumentSourceBindingDto>>> List(CancellationToken ct)
        => Ok(await list.HandleAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// Явне перемикання стану-воріт: <c>any</c> (будь-який стан джерела) або <c>approvedOnly</c> (лише затверджений
    /// місяць). Право <c>Calculation.ManageSourceBinding</c>; подія <c>DocumentSourceBindingChanged</c> у журналі безпеки.
    /// </summary>
    /// <remarks>
    /// <c>approvedOnly</c> — <c>409 ECR-CALC-0409</c>, доки документ-джерело поточного року не лежить у місячному
    /// проєкті з погодженням (<c>sourceBindingNoDocument</c>, <c>sourceBindingNotMonthly</c>,
    /// <c>sourceBindingWorkflowNone</c>). Справжня зміна ставить злитий перерахунок залежних за кожен рік джерела.
    /// </remarks>
    /// <param name="id">Зв'язка.</param>
    /// <param name="request">Новий стан.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPut("{id:int}/source-state")]
    [ProducesResponseType<SwitchDocumentSourceStateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SwitchDocumentSourceStateResponse>> SwitchState(
        int id, [FromBody] SwitchDocumentSourceStateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(await switchState.HandleAsync(id, request, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Перемикання бітів зв'язки: <c>readFromDocument</c> (читати склад з документа-джерела) і
    /// <c>recalculateDependents</c> (аварійний вимикач автоматичних залежних). Право
    /// <c>Calculation.ManageSourceBinding</c>; подія <c>DocumentSourceBindingChanged</c> (<c>kind = flags</c>) у журналі безпеки.
    /// </summary>
    /// <remarks>
    /// Хоч один біт має бути заданий (інакше 422 <c>ECR-REQ-0422</c> <c>sourceFlagsInvalid</c>). Зміна
    /// <c>readFromDocument</c> за ввімкненого <c>recalculateDependents</c> ставить злитий перерахунок залежних за кожен
    /// рік джерела.
    /// </remarks>
    /// <param name="id">Зв'язка.</param>
    /// <param name="request">Нові біти.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPut("{id:int}/flags")]
    [ProducesResponseType<SwitchDocumentSourceFlagsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SwitchDocumentSourceFlagsResponse>> SwitchFlags(
        int id, [FromBody] SwitchDocumentSourceFlagsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(await switchFlags.HandleAsync(id, request, ct).ConfigureAwait(false));
    }
}
