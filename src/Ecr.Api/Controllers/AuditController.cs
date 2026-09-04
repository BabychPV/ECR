using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Читання аудиту.</summary>
/// <remarks>
/// Аудит **тільки читається**: записів на зміну чи видалення тут немає і не
/// буде — журнал, який можна відредагувати, не є доказом.
/// </remarks>
[ApiController]
[Route("api/v1/audit")]
[Authorize]
public sealed class AuditController : ControllerBase
{
    /// <summary>
    /// Історія змін комірок. Право <c>Security.ViewAudit</c>.
    /// </summary>
    /// <remarks>
    /// Таблиця партиційована за <c>ChangedAt</c>, а не за періодом: місяць
    /// зміни і звітний період — різні осі (зміна за січень може статися в
    /// березні). Тому запит **обов'язково** обмежений вікном часу — інакше він
    /// піде по всіх партиціях.
    /// </remarks>
    [HttpGet("cells")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<IActionResult> Cells(
        [FromQuery] DateTime from, [FromQuery] DateTime to,
        [FromQuery] long? documentId, [FromQuery] int limit, [FromQuery] string? cursor,
        CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перевірити Security.ViewAudit; вікно (from, to) обов'язкове і обмежене згори — " +
            "без нього запит іде по всіх партиціях aud.CellChange; курсорна пагінація; " +
            "автором зміни показувати UserId, а не SID.");
}
