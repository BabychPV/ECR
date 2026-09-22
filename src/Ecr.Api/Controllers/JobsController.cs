using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Стан фонових задач.</summary>
[ApiController]
[Route("api/v1/jobs")]
[Authorize]
public sealed class JobsController(
    GetJobStatusHandler status,
    ListJobsHandler list,
    RestartJobHandler restart,
    CancelJobHandler cancel) : ControllerBase
{
    /// <summary>
    /// Останні фонові задачі. Право <c>System.ViewHealth</c> — або
    /// <c>mine=true</c> для ВЛАСНИХ задач (BE-08).
    /// </summary>
    /// <param name="state">
    /// Стан задачі: <c>Queued</c>, <c>Running</c>, <c>Succeeded</c>,
    /// <c>Failed</c>, <c>Cancelled</c>. Невідомий — <c>422</c>, а не порожній
    /// перелік.
    /// </param>
    /// <param name="code">Код (тип) задачі, як у <c>JobSummary.jobCode</c>.</param>
    /// <param name="mine">Лише власні задачі; не вимагає <c>System.ViewHealth</c>.</param>
    /// <param name="limit">Скільки повернути, 1…50.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ Без цього ендпоінта збій задачі був видимий лише тому, хто вже знає
    /// її GUID: перелік — єдиний спосіб дізнатися, ЩО впало, а не лише
    /// перевірити те, про що вже здогадався (`ФВ-12.4`).
    /// <para>
    /// ⛔ <b>Автора задачі НЕ МОЖНА назвати з запиту.</b> Дія не має і не
    /// матиме параметра з ідентифікатором користувача: <c>mine=true</c> —
    /// межа доступу (власні задачі видно без <c>System.ViewHealth</c>, Q-156),
    /// і параметр «чиї задачі» перетворив би це звільнення на спосіб читати
    /// чужу чергу. Власник береться лише з <c>ICurrentUser</c> в обробнику.
    /// </para>
    /// <para>
    /// ⛔ Без <c>mine</c> і без права — <c>403</c>, а не порожній перелік:
    /// «задач немає» і «вам їх не показують» — різні відповіді, і перша з них
    /// тут була б неправдою.
    /// </para>
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<JobSummary>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IReadOnlyList<JobSummary>>> List(
        [FromQuery] string? state,
        [FromQuery] string? code,
        [FromQuery] bool mine,
        [FromQuery] int? limit,
        CancellationToken ct)
        => Ok(await list.HandleAsync(state, code, mine, limit, ct).ConfigureAwait(false));

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

    /// <summary>
    /// Ручний перезапуск проваленої задачі. Право <c>System.ViewHealth</c> — або
    /// автор ВЛАСНОЇ задачі (директива №11, T10 #40; UX-09).
    /// </summary>
    /// <remarks>
    /// ⚠ Той самий <c>jobId</c> знову «у черзі» — не новий ідентифікатор:
    /// клієнт, що вже показує цю задачу, продовжує опитувати той самий
    /// <c>GET /jobs/{jobId}</c>.
    /// </remarks>
    [HttpPost("{jobId}/restart")]
    [ProducesResponseType<Contracts.JobAcceptedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Restart(string jobId, CancellationToken ct)
    {
        await restart.HandleAsync(jobId, ct).ConfigureAwait(false);

        return Accepted(new Contracts.JobAcceptedResponse(jobId));
    }

    /// <summary>
    /// Просить задачу завершитися. Право <c>System.ViewHealth</c> — або автор
    /// ВЛАСНОЇ задачі (Q-156).
    /// </summary>
    /// <remarks>
    /// ⚠ <c>202</c>, а не <c>204</c>: скасування — прохання, не вбивство.
    /// Задача бачить токен і закривається станом <c>Cancelled</c> на найближчій
    /// межі батчу, тож у мить відповіді вона ЩЕ ВИКОНУЄТЬСЯ. Клієнт дочитує
    /// стан тим самим <c>GET /jobs/{jobId}</c>, яким уже показує прогрес.
    /// <para>
    /// ⛔ До цього маршруту зупинити двадцятихвилинний перерахунок з інтерфейсу
    /// було нічим: <c>CancelAsync</c> кликало лише витіснення зсередини
    /// (<c>D2-64</c>).
    /// </para>
    /// <para>
    /// ⚠ <c>jobId</c> містить <c>#</c> (<c>IRecalculationJob#42</c>), а той в
    /// URL починає фрагмент — клієнт зобов'язаний кодувати сегмент
    /// (<c>encodeURIComponent</c>). Саме на цьому падав крок 17 <c>smoke.ps1</c>.
    /// </para>
    /// </remarks>
    [HttpPost("{jobId}/cancel")]
    [ProducesResponseType<Contracts.JobAcceptedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(string jobId, CancellationToken ct)
    {
        await cancel.HandleAsync(jobId, ct).ConfigureAwait(false);

        return Accepted(new Contracts.JobAcceptedResponse(jobId));
    }
}
