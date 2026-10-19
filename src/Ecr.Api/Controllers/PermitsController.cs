using Ecr.Application.Common;
using Ecr.Application.Permits;
using Ecr.Application.Permits.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Дозволи (екземпляри типів): перелік, шапка, життєвий цикл <c>Draft → Active → Expired</c>, поновлення (Permits, PM-04).
/// Читання - <c>Registry.View</c>, запис - <c>Registry.EditData</c> (або ресурсний грант на реєстр шапки типу).
/// Рядки таблиць дозволу читаються й пишуться наявними <c>/registries/{PRM_…}/rows?parentEntryId=</c> та
/// <c>entries/batch</c> - власних ендпоінтів рядків немає.
/// </summary>
[ApiController]
[Route("api/v1/permits")]
[Authorize]
public sealed class PermitsController(
    ListPermitsHandler list,
    GetPermitHandler get,
    CreatePermitHandler create,
    UpdatePermitHandler update,
    DeletePermitHandler delete,
    ActivatePermitHandler activate,
    EndPermitHandler end,
    RenewPermitHandler renew) : ControllerBase
{
    /// <summary>Перелік дозволів (усі доступні типи). Право <c>Registry.View</c>.</summary>
    /// <param name="type">Код типу.</param>
    /// <param name="status"><c>Draft</c>, <c>Active</c> або <c>Expired</c> на <paramref name="asOf"/>.</param>
    /// <param name="q">Підрядок номера, розташування чи назви типу.</param>
    /// <param name="asOf">Дата, на яку рахується стан (типово сьогодні, UTC).</param>
    /// <param name="limit">Розмір сторінки 1..500 (типово 500: екран рахує підсумки з повного переліку).</param>
    /// <param name="cursor">Id останнього дозволу попередньої сторінки.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet]
    [ProducesResponseType<PagedResult<PermitListItemDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> List(
        [FromQuery] string? type, [FromQuery] string? status, [FromQuery] string? q, [FromQuery] DateOnly? asOf,
        [FromQuery] int limit, [FromQuery] string? cursor, CancellationToken ct)
        => Ok(await list.HandleAsync(new CursorRequest(limit == 0 ? CursorRequest.MaxLimit : limit, cursor), type, status, q, asOf, ct).ConfigureAwait(false));

    /// <summary>Заводить дозвіл ЧЕРНЕТКОЮ за опублікованим типом. Право <c>Registry.EditData</c>.</summary>
    /// <param name="request">Тип, номер, розташування, дати.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost]
    [ProducesResponseType<PermitDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] CreatePermitRequest request, CancellationToken ct)
    {
        var created = await create.HandleAsync(request, ct).ConfigureAwait(false);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    /// <summary>Дозвіл: шапка, стан, таблиці з підсумками. Право <c>Registry.View</c>.</summary>
    /// <param name="id">Ідентифікатор дозволу.</param>
    /// <param name="asOf">Дата, на яку рахується стан (типово сьогодні, UTC).</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("{id:long}")]
    [ProducesResponseType<PermitDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PermitDto>> Get(long id, [FromQuery] DateOnly? asOf, CancellationToken ct)
        => Ok(await get.HandleAsync(id, asOf, ct).ConfigureAwait(false));

    /// <summary>
    /// Змінює шапку чернетки чи чинного дозволу (не закінченого). Право <c>Registry.EditData</c>. Токен
    /// <c>rowVersion</c> - з останнього читання.
    /// </summary>
    /// <param name="id">Ідентифікатор дозволу.</param>
    /// <param name="request">Нові значення шапки й токен.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPut("{id:long}")]
    [ProducesResponseType<PermitDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PermitDto>> Update(long id, [FromBody] UpdatePermitRequest request, CancellationToken ct)
        => Ok(await update.HandleAsync(id, request, ct).ConfigureAwait(false));

    /// <summary>Видаляє чернетку разом із рядками її таблиць. Право <c>Registry.EditData</c>.</summary>
    /// <param name="id">Ідентифікатор дозволу.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        await delete.HandleAsync(id, ct).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Активує чернетку (<c>Draft → Active</c>) з перевіркою перетину чинних. Право <c>Registry.EditData</c>.</summary>
    /// <param name="id">Ідентифікатор дозволу.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost("{id:long}/activate")]
    [ProducesResponseType<PermitDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PermitDto>> Activate(long id, CancellationToken ct)
        => Ok(await activate.HandleAsync(id, ct).ConfigureAwait(false));

    /// <summary>Достроково завершує чинний дозвіл. Право <c>Registry.EditData</c>.</summary>
    /// <param name="id">Ідентифікатор дозволу.</param>
    /// <param name="request">Причина (від 10 символів) і дата завершення.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost("{id:long}/end")]
    [ProducesResponseType<PermitDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PermitDto>> End(long id, [FromBody] EndPermitRequest request, CancellationToken ct)
        => Ok(await end.HandleAsync(id, request, ct).ConfigureAwait(false));

    /// <summary>
    /// Поновлює дозвіл: нова чернетка з копією шапки й усіх рядків; початок = кінець поточного. Право
    /// <c>Registry.EditData</c>. Тіло необов'язкове.
    /// </summary>
    /// <param name="id">Ідентифікатор дозволу.</param>
    /// <param name="request">Необов'язковий кінець нового дозволу.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost("{id:long}/renew")]
    [ProducesResponseType<PermitDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Renew(long id, [FromBody] RenewPermitRequest? request, CancellationToken ct)
    {
        var renewed = await renew.HandleAsync(id, request, ct).ConfigureAwait(false);
        return CreatedAtAction(nameof(Get), new { id = renewed.Id }, renewed);
    }
}
