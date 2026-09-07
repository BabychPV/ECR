using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Стан фонових задач.</summary>
[ApiController]
[Route("api/v1/jobs")]
[Authorize]
public sealed class JobsController(GetJobStatusHandler status, ListJobsHandler list) : ControllerBase
{
    /// <summary>
    /// Останні фонові задачі. Право <c>System.ViewHealth</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Без цього ендпоінта збій задачі був видимий лише тому, хто вже знає
    /// її GUID: перелік — єдиний спосіб дізнатися, ЩО впало, а не лише
    /// перевірити те, про що вже здогадався (`ФВ-12.4`).
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<JobSummary>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<JobSummary>>> List(CancellationToken ct)
        => Ok(await list.HandleAsync(ct).ConfigureAwait(false));

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
    public async Task<ActionResult<JobStatus>> Get(string jobId, CancellationToken ct)
    {
        var found = await status.HandleAsync(jobId, ct).ConfigureAwait(false);

        return found is null ? NotFound() : Ok(found);
    }
}
