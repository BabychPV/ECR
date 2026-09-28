using Ecr.Application.Common;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Журнал прогонів збору (ФВ-5.23). Право <c>Integration.View</c> або <c>Integration.Manage</c>.
/// </summary>
[ApiController]
[Route("api/v1/collection-runs")]
[Authorize]
public sealed class CollectionRunsController(
    ListCollectionRunsHandler list, GetCollectionRunHandler get, ListCoverageEventsHandler events) : ControllerBase
{
    /// <summary>Прогони збору, новіші першими.</summary>
    /// <param name="dataSource">Лише прогони сутностей цього з'єднання.</param>
    /// <param name="entity">Лише прогони цієї сутності збору.</param>
    /// <param name="state"><c>Running</c>, <c>Succeeded</c>, <c>Degraded</c>, <c>Failed</c>; інше — <c>422</c>.</param>
    /// <param name="from">Початок прогону не раніше (UTC, включно).</param>
    /// <param name="to">Початок прогону раніше (UTC, виключно).</param>
    /// <param name="cursor">Курсор наступної сторінки.</param>
    /// <param name="limit">Розмір сторінки 1..200; <c>0</c> — типове 50.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet]
    [ProducesResponseType<PagedResult<CollectionRunView>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> List(
        [FromQuery] int? dataSource,
        [FromQuery] int? entity,
        [FromQuery] string? state,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? cursor,
        [FromQuery] int limit,
        CancellationToken ct)
        => Ok(await list
            .HandleAsync(
                new CollectionRunFilter(dataSource, entity, state, ToUtc(from), ToUtc(to)),
                new CursorRequest(limit == 0 ? 50 : limit, cursor),
                ct)
            .ConfigureAwait(false));

    /// <summary>Прогін із текстом помилки й покритими інтервалами.</summary>
    /// <param name="id">Ідентифікатор прогону.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("{id:long}")]
    [ProducesResponseType<CollectionRunDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(long id, CancellationToken ct)
        => Ok(await get.HandleAsync(id, ct).ConfigureAwait(false));

    /// <summary>
    /// Події журналу покриття: пропуск закритого періоду, конфлікт із ручним
    /// значенням, стеля точок (ІНТ-3.3, <c>D-118</c>). Новіші першими.
    /// </summary>
    /// <remarks>
    /// ⚠ Літеральний сегмент, а не <c>{id}</c>: обмеження <c>{id:long}</c> у
    /// деталі прогону не дає цим двом маршрутам збігтися.
    /// </remarks>
    /// <param name="dataSource">Лише події сутностей цього з'єднання.</param>
    /// <param name="entity">Лише події цієї сутності збору.</param>
    /// <param name="status"><c>SkippedPeriodClosed</c>, <c>ConflictKeptManual</c>, <c>SkippedPointCeiling</c>, <c>SkippedWriteConflict</c>, <c>SkippedNeedsConfirmation</c>; інше — <c>422</c>.</param>
    /// <param name="periodKey">Лише події цього періоду.</param>
    /// <param name="cursor">Курсор наступної сторінки.</param>
    /// <param name="limit">Розмір сторінки 1..200; <c>0</c> — типове 50.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("coverage-events")]
    [ProducesResponseType<PagedResult<CoverageEventView>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CoverageEvents(
        [FromQuery] int? dataSource,
        [FromQuery] int? entity,
        [FromQuery] string? status,
        [FromQuery] int? periodKey,
        [FromQuery] string? cursor,
        [FromQuery] int limit,
        CancellationToken ct)
        => Ok(await events
            .HandleAsync(
                new CoverageEventFilter(dataSource, entity, status, periodKey),
                new CursorRequest(limit == 0 ? 50 : limit, cursor),
                ct)
            .ConfigureAwait(false));

    /// <summary>Час без зони читається як UTC — так само, як його віддає журнал.</summary>
    private static DateTime? ToUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Unspecified } v => DateTime.SpecifyKind(v, DateTimeKind.Utc),
        { } v => v.ToUniversalTime(),
    };
}
