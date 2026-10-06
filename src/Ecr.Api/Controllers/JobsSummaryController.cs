using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Лічильники черги фонових задач.</summary>
/// <remarks>
/// ⚠ Окремий контролер на тому самому префіксі <c>api/v1/jobs</c>, а не дія в
/// <see cref="JobsController"/>: той створюється вручну в кількох тестах, і
/// кожен новий параметр конструктора ламав би їх без жодного зв'язку із
/// предметом. Літерал <c>summary</c> має пріоритет над <c>{jobId}</c> — маршрут
/// <c>GET /jobs/{jobId}</c> його не перехоплює (перевіряється тестом).
/// </remarks>
[ApiController]
[Route("api/v1/jobs")]
[Authorize]
public sealed class JobsSummaryController(GetJobsSummaryHandler summary) : ControllerBase
{
    /// <summary>
    /// Лічильники черги для смуги показників: виконуються, у черзі, провали й
    /// успіхи за добу, середня затримка старту. Право <c>System.ViewHealth</c>
    /// — або <c>mine=true</c> для ВЛАСНИХ задач.
    /// </summary>
    /// <param name="mine">Лише власні задачі; не вимагає <c>System.ViewHealth</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ Та сама межа, що в переліку: без <c>mine</c> і без права — <c>403</c>,
    /// не нулі; параметра з ідентифікатором автора немає. Віддаються лише
    /// числа — без тексту провалів і без посилань на документи.
    /// </remarks>
    [HttpGet("summary")]
    [ProducesResponseType<JobsSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<JobsSummary>> Summary([FromQuery] bool mine, CancellationToken ct)
        => Ok(await summary.HandleAsync(mine, ct).ConfigureAwait(false));
}
