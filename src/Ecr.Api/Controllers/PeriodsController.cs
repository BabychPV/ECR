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
    /// </remarks>
    [HttpPost("{id:int}/reopen")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
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
/// <param name="Until">До якого моменту період лишається відкритим; <c>null</c> — безстроково.</param>
public sealed record ReopenPeriodRequest(string Reason, DateTime? Until);
