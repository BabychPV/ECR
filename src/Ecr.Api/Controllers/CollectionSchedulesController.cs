using Ecr.Application.Integration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Ecr.Api.Controllers;

/// <summary>
/// Розклад збору із зовнішніх джерел (<c>BE-21b</c>, ФВ-14.3).
/// Право на всі три дії — <c>Integration.EditSchedule</c>.
/// </summary>
/// <remarks>
/// ⛔ Зміна і видалення вимагають заголовка <c>If-Match</c> зі значенням
/// <c>rowVersion</c> із переліку. Розклад правлять із екрана, який тримають
/// відкритим; без звірки версії другий редактор мовчки затер би першого, і
/// дізналися б про це з відсутніх даних, а не з відмови.
/// </remarks>
[ApiController]
[Route("api/v1/collection-schedules")]
[Authorize]
public sealed class CollectionSchedulesController(
    ListCollectionSchedulesHandler list,
    CreateCollectionScheduleHandler create,
    SaveCollectionScheduleHandler save,
    DeleteCollectionScheduleHandler delete) : ControllerBase
{
    /// <summary>Усі розклади разом із кодом сутності джерела і станом постановки.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<CollectionScheduleView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await list.HandleAsync(ct).ConfigureAwait(false));

    /// <summary>Заводить розклад для сутності джерела, у якої його ще немає.</summary>
    /// <remarks>
    /// ⚠ <c>If-Match</c> тут не потрібен: створення нічого не перезаписує.
    /// Невалідний cron — <c>422 ECR-REQ-0422</c> ДО запису; сутності немає —
    /// <c>404 ECR-INT-0404</c>; розклад у неї вже є — <c>409 ECR-JOB-0409</c>.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<CollectionScheduleView>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create(
        [FromBody] CreateCollectionScheduleRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var created = await create
            .HandleAsync(request.SourceEntityId, request.Cron, request.IsEnabled, ct)
            .ConfigureAwait(false);

        return Created(new Uri("/api/v1/collection-schedules", UriKind.Relative), created);
    }

    /// <summary>Змінює cron і вмикає/вимикає розклад; потребує <c>If-Match</c>.</summary>
    /// <remarks>
    /// Невалідний або задовгий cron — <c>422 ECR-REQ-0422</c> ДО запису;
    /// чужа версія рядка — <c>409 ECR-JOB-0409</c>.
    /// </remarks>
    [HttpPut("{id:int}")]
    [ProducesResponseType<CollectionScheduleView>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Update(
        int id, [FromBody] UpdateCollectionScheduleRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ⚠ Заголовок читається з запиту, а не параметром із `[FromHeader]`:
        // дужки всередині списку параметрів дії ламають розбір контролерів
        // сторожами `EndpointCoverageTests`, які читають ТЕКСТ файлу.
        var ifMatch = Request.Headers[HeaderNames.IfMatch].ToString();

        return Ok(await save
            .HandleAsync(id, request.Cron, request.IsEnabled, ifMatch, ct)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Прибирає розклад і знімає задачу з планувальника; потребує <c>If-Match</c>.
    /// </summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var ifMatch = Request.Headers[HeaderNames.IfMatch].ToString();

        await delete.HandleAsync(id, ifMatch, ct).ConfigureAwait(false);

        return NoContent();
    }
}

/// <summary>Тіло створення розкладу.</summary>
/// <param name="SourceEntityId">Сутність джерела, яку збиратимуть за цим розкладом.</param>
/// <param name="Cron">Вираз cron у форматі Quartz: 6–7 полів, одне з полів дня — <c>?</c>.</param>
/// <param name="IsEnabled">Чи має розклад одразу стояти в планувальнику.</param>
public sealed record CreateCollectionScheduleRequest(int SourceEntityId, string Cron, bool IsEnabled);

/// <summary>Тіло зміни розкладу.</summary>
/// <param name="Cron">Вираз cron у форматі Quartz: 6–7 полів, одне з полів дня — <c>?</c>.</param>
/// <param name="IsEnabled">Чи має розклад стояти в планувальнику.</param>
public sealed record UpdateCollectionScheduleRequest(string Cron, bool IsEnabled);
