using Ecr.Application.Common;
using Ecr.Application.Permits;
using Ecr.Application.Permits.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Типи дозволів: перелік, визначення, чернетка складу, нова версія, атомарна публікація (Permits, PM-03).
/// Права: читання <c>Registry.View</c>, зміна <c>Permit.EditType</c>, публікація <c>Permit.PublishType</c>.
/// </summary>
[ApiController]
[Route("api/v1/permit-types")]
[Authorize]
public sealed class PermitTypesController(
    ListPermitTypesHandler list,
    GetPermitTypeHandler get,
    CreatePermitTypeHandler create,
    ReplacePermitTypeHandler replace,
    DeletePermitTypeHandler delete,
    PublishPermitTypeHandler publish,
    OpenPermitTypeVersionHandler openVersion) : ControllerBase
{
    /// <summary>Перелік типів зі сторінками. Право <c>Registry.View</c>.</summary>
    /// <param name="category"><c>Air</c>, <c>Water</c>, <c>Waste</c> або <c>Other</c>.</param>
    /// <param name="status"><c>Draft</c> чи <c>Published</c>.</param>
    /// <param name="q">Підрядок коду чи назви.</param>
    /// <param name="limit">Розмір сторінки 1..500 (типово 50).</param>
    /// <param name="cursor">Код останнього типу попередньої сторінки.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet]
    [ProducesResponseType<PagedResult<PermitTypeListItemDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> List(
        [FromQuery] string? category, [FromQuery] string? status, [FromQuery] string? q,
        [FromQuery] int limit, [FromQuery] string? cursor, CancellationToken ct)
        => Ok(await list.HandleAsync(new CursorRequest(limit == 0 ? 50 : limit, cursor), category, status, q, ct).ConfigureAwait(false));

    /// <summary>
    /// Повне визначення типу: чернетка, якщо вона є, інакше опубліковане. Право <c>Registry.View</c>.
    /// </summary>
    /// <param name="code">Код типу.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("{code}")]
    [ProducesResponseType<PermitTypeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PermitTypeDto>> Get(string code, CancellationToken ct)
        => Ok(await get.HandleAsync(code, ct).ConfigureAwait(false));

    /// <summary>Заводить тип чернеткою (з копією іншого типу за потреби). Право <c>Permit.EditType</c>.</summary>
    /// <param name="request">Код, назва, категорія, <c>copyFromCode</c>.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost]
    [RequestSizeLimit(PermitTypeLimits.MaxRequestBodyBytes)]
    [ProducesResponseType<PermitTypeDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] CreatePermitTypeRequest request, CancellationToken ct)
    {
        var created = await create.HandleAsync(request, ct).ConfigureAwait(false);
        return CreatedAtAction(nameof(Get), new { code = created.Code }, created);
    }

    /// <summary>
    /// Замінює ЧЕРНЕТКУ визначення цілком (таблиці, стовпці, списки, перевірки). Право
    /// <c>Permit.EditType</c>. Токен <c>rowVersion</c> - з останнього читання.
    /// </summary>
    /// <param name="code">Код типу.</param>
    /// <param name="request">Повний склад і токен.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPut("{code}")]
    [RequestSizeLimit(PermitTypeLimits.MaxRequestBodyBytes)]
    [ProducesResponseType<PermitTypeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PermitTypeDto>> Replace(
        string code, [FromBody] ReplacePermitTypeRequest request, CancellationToken ct)
        => Ok(await replace.HandleAsync(code, request, ct).ConfigureAwait(false));

    /// <summary>
    /// Видаляє ніколи не опублікований тип без дозволів. Право <c>Permit.EditType</c>.
    /// </summary>
    /// <param name="code">Код типу.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpDelete("{code}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(string code, CancellationToken ct)
    {
        await delete.HandleAsync(code, ct).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Публікує чернетку АТОМАРНО: шапка, усі таблиці й приховані списки - однією транзакцією. Право
    /// <c>Permit.PublishType</c>.
    /// </summary>
    /// <param name="code">Код типу.</param>
    /// <param name="request">Причина (не коротша за 10 символів) і токен чернетки.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost("{code}/publish")]
    [RequestSizeLimit(PermitTypeLimits.MaxRequestBodyBytes)]
    [ProducesResponseType<PermitTypeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PermitTypeDto>> Publish(
        string code, [FromBody] PublishPermitTypeRequest request, CancellationToken ct)
        => Ok(await publish.HandleAsync(code, request, ct).ConfigureAwait(false));

    /// <summary>
    /// Відкриває чернетку нової версії опублікованого типу. Право <c>Permit.EditType</c>.
    /// </summary>
    /// <param name="code">Код типу.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost("{code}/new-version")]
    [ProducesResponseType<PermitTypeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PermitTypeDto>> NewVersion(string code, CancellationToken ct)
        => Ok(await openVersion.HandleAsync(code, ct).ConfigureAwait(false));
}
