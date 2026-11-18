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
    SwitchDocumentSourceStateHandler switchState) : ControllerBase
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
}
