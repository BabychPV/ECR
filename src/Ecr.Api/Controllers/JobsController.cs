using Ecr.Application.Ports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Стан фонових задач.</summary>
[ApiController]
[Route("api/v1/jobs")]
[Authorize]
public sealed class JobsController(IBackgroundJobScheduler scheduler) : ControllerBase
{
    /// <summary>
    /// Стан задачі за її ідентифікатором. Право <c>System.ViewHealth</c>.
    /// </summary>
    /// <remarks>
    /// Усе, що довше за ~5 с, іде у фон і повертає <c>jobId</c>; саме через цей
    /// ендпоінт UI показує прогрес. Задача, якої немає, — це 404, а не порожній
    /// стан: інакше клієнт нескінченно опитував би неіснуючий ідентифікатор.
    /// </remarks>
    [HttpGet("{jobId}")]
    [ProducesResponseType<JobStatus>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult<JobStatus>> Get(string jobId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перевірити System.ViewHealth; делегувати scheduler.GetStatusAsync(jobId, ct); " +
            "невідомий jobId → 404.");
}
