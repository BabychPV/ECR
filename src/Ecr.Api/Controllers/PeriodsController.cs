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
    public Task<IActionResult> Reopen(int id, [FromBody] ReopenPeriodRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перевірити Period.Reopen через IAccessDecisionService; делегувати " +
            "reopen.HandleAsync(id, request.Reason, request.Until, ct); повернути 204.");
}

/// <summary>Запит на відкриття періоду.</summary>
/// <param name="Reason">Причина; обов'язкова, потрапляє в аудит.</param>
/// <param name="Until">До якого моменту період лишається відкритим; <c>null</c> — безстроково.</param>
public sealed record ReopenPeriodRequest(string Reason, DateTime? Until);
