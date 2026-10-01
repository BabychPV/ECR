// src/Ecr.Api/Controllers/RowWindowMapsController.cs
using Ecr.Application.Sources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Прив'язки PI «атрибут → колонка, вікно = рядок» (<c>ext.RowWindowMap</c>): налаштування (HSE301 A1,
/// FEATURE-HSE301-VIEW §4.4).
/// </summary>
/// <remarks>
/// ⚠ Права перевіряє обробник (архітектурне правило 7), а не атрибут. Підтягування значень — окрема задача;
/// цей контролер лише налаштовує.
/// </remarks>
[ApiController]
[Route("api/v1/row-window-maps")]
[Authorize]
public sealed class RowWindowMapsController(
    ListRowWindowMapsHandler maps,
    CreateRowWindowMapHandler create,
    UpdateRowWindowMapHandler update,
    DeleteRowWindowMapHandler delete) : ControllerBase
{
    /// <summary>Прив'язки; за <paramref name="tableDefId"/> і/чи <paramref name="sourceEntityId"/> — лише відповідні. Право <c>Integration.View</c> або <c>Integration.Manage</c>.</summary>
    /// <param name="tableDefId">Таблиця; <c>null</c> — усі.</param>
    /// <param name="sourceEntityId">Сутність джерела, що має джерело в прив'язці; <c>null</c> — будь-яка.</param>
    /// <param name="ct">Скасування.</param>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<RowWindowMapDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] int? tableDefId, [FromQuery] int? sourceEntityId, CancellationToken ct)
        => Ok(await maps.ListAsync(tableDefId, sourceEntityId, ct).ConfigureAwait(false));

    /// <summary>Одна прив'язка з джерелами. Право <c>Integration.View</c> або <c>Integration.Manage</c>.</summary>
    /// <param name="id">Прив'язка.</param>
    /// <param name="ct">Скасування.</param>
    [HttpGet("{id:int}")]
    [ProducesResponseType<RowWindowMapDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
        => Ok(await maps.GetAsync(id, ct).ConfigureAwait(false));

    /// <summary>
    /// Заводить прив'язку вікна рядка. Право <c>Integration.Manage</c> і грант <c>Manage</c> на кожен проєкт, що
    /// використовує колонку-ціль.
    /// </summary>
    /// <param name="request">Таблиця, колонки (ціль, початок, кінець, селектор), згортка, одиниця, пороги, джерела.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// Вікно — колонки типу <c>Date</c>, ціль — <c>Decimal</c> (<c>422 ECR-INT-0422</c>); друга прив'язка на ту саму
    /// колонку-ціль — <c>409 ECR-INT-0409</c>. Слід — у журналі структурних змін.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<RowWindowMapDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] CreateRowWindowMapCommand request, CancellationToken ct)
    {
        var created = await create.HandleAsync(request, ct).ConfigureAwait(false);

        return Created(new Uri($"/api/v1/row-window-maps/{created.Id}", UriKind.Relative), created);
    }

    /// <summary>
    /// Повна заміна налаштувань прив'язки: вікно, селектор, згортка, пороги, <c>isActive</c> (пауза й відновлення),
    /// джерела. Таблиця й колонка-ціль не змінюються. Право <c>Integration.Manage</c> і грант <c>Manage</c>.
    /// </summary>
    /// <param name="id">Прив'язка.</param>
    /// <param name="request">Нові налаштування; <c>rowVersion</c> — версія, яку бачив клієнт.</param>
    /// <param name="ct">Скасування.</param>
    [HttpPut("{id:int}")]
    [ProducesResponseType<RowWindowMapDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateRowWindowMapCommand request, CancellationToken ct)
        => Ok(await update.HandleAsync(id, request, ct).ConfigureAwait(false));

    /// <summary>
    /// Видаляє прив'язку, за якою ще нічого не підтягнуто. Право <c>Integration.Manage</c> і грант <c>Manage</c>.
    /// </summary>
    /// <param name="id">Прив'язка.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// Прив'язка із записами провенансу — <c>409 ECR-INT-0409</c>: вихід — пауза (<c>isActive = false</c>).
    /// </remarks>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await delete.HandleAsync(id, ct).ConfigureAwait(false);

        return NoContent();
    }
}
