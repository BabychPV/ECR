using Ecr.Application.Periods;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Операції над періодом.</summary>
[ApiController]
[Route("api/v1/periods")]
[Authorize]
public sealed class PeriodsController(ReopenPeriodHandler reopen) : ControllerBase
{
    /// <summary>
    /// Відкриває закритий період. Право <c>Period.Reopen</c>.
    /// </summary>
    /// <remarks>
    /// Відкриття періоду і відкриття документа — <b>різні</b> операції з
    /// різними правами (<c>D-67</c>). Причина обов'язкова: закритий період —
    /// це поданий стан звітності, і його зміна має бути пояснена в аудиті.
    /// <para>
    /// ⚠ Це <b>єдина</b> ручна зміна стану періоду (рішення людини
    /// <c>Q15-02</c>, директива №15 <c>BE-29</c>). <c>Open</c>, <c>Close</c> і
    /// <c>Archive</c> лишаються за <c>PeriodStateJob</c> — стан періоду має
    /// один календар, а не два джерела.
    /// </para>
    /// <para>
    /// ⛔ Відкриття не закритого періоду — <c>409 ECR-PRD-0409</c>, причина з
    /// самих пробілів — <c>422 ECR-PRD-0422</c>. Обидві відмови ухвалює домен
    /// (<c>Period.Reopen</c>), а не цей контролер.
    /// </para>
    /// </remarks>
    [HttpPost("{id:int}/reopen")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Reopen(
        int id, [FromBody] ReopenPeriodRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Право Period.Reopen перевіряє обробник: воно небезпечне, і рішення
        // має ухвалюватися там само, де виконується дія.
        await reopen.HandleAsync(id, request.Reason, request.Until, ct).ConfigureAwait(false);
        return NoContent();
    }
}

/// <summary>Запит на відкриття періоду.</summary>
/// <param name="Reason">Причина; обов'язкова, потрапляє в аудит.</param>
/// <param name="Until">
/// До якого моменту період лишається відкритим; <c>null</c> — до кінця
/// поточної доби в поясі майданчика.
/// </param>
/// <remarks>
/// ⛔ <c>null</c> — НЕ «безстроково», хоч тут і стояло саме це слово.
/// <c>ReopenPeriodHandler</c> підставляє кінець доби майданчика
/// (<c>EndOfSiteDay</c>, <c>D-68</c>), і безстрокового відкриття не існує
/// взагалі: <c>PeriodStateJob</c> повертає період у <c>Closed</c>, щойно
/// <c>ReopenedUntil</c> минає. Опис, який обіцяє інше, доїжджає клієнтові
/// в <c>openapi.snapshot.json</c> і <c>schema.d.ts</c> — тобто вводить в
/// оману рівно того, хто будує кнопку.
/// </remarks>
public sealed record ReopenPeriodRequest(string Reason, DateTime? Until);
