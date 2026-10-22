using Ecr.Application.Permits.Groups;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Групи типів дозволів («Вода» = кілька типів): ім'я, опис, склад; група є ціллю Lookup через прихований реєстр
/// <c>PRMG_&lt;КОД&gt;</c> (Permits, PRMG). Права: читання <c>Registry.View</c> (+ заборона S18 на реєстр групи),
/// зміна <c>Permit.EditType</c>.
/// </summary>
[ApiController]
[Route("api/v1/permit-groups")]
[Authorize]
public sealed class PermitGroupsController(
    ListPermitGroupsHandler list,
    GetPermitGroupHandler get,
    CreatePermitGroupHandler create,
    UpdatePermitGroupHandler update,
    ReplacePermitGroupTypesHandler replaceTypes,
    DeletePermitGroupHandler delete) : ControllerBase
{
    /// <summary>Усі групи зі складом і використанням (їх десятки: без сторінок). Право <c>Registry.View</c>.</summary>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<PermitGroupDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await list.HandleAsync(ct).ConfigureAwait(false));

    /// <summary>Група за кодом з токеном <c>rowVersion</c>. Право <c>Registry.View</c>.</summary>
    /// <param name="code">Код групи.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("{code}")]
    [ProducesResponseType<PermitGroupDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PermitGroupDto>> Get(string code, CancellationToken ct)
        => Ok(await get.HandleAsync(code, ct).ConfigureAwait(false));

    /// <summary>
    /// Створює групу (разом із прихованим реєстром <c>PRMG_&lt;КОД&gt;</c> і початковим складом). Право <c>Permit.EditType</c>.
    /// </summary>
    /// <param name="request">Код, назва, опис, склад.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost]
    [ProducesResponseType<PermitGroupDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] CreatePermitGroupRequest request, CancellationToken ct)
    {
        var created = await create.HandleAsync(request, ct).ConfigureAwait(false);
        return CreatedAtAction(nameof(Get), new { code = created.Code }, created);
    }

    /// <summary>
    /// Змінює назву, опис і активність групи (деактивація - <c>isActive = false</c>; використану групу видаляти не можна).
    /// Право <c>Permit.EditType</c>. Токен <c>rowVersion</c> - з останнього читання.
    /// </summary>
    /// <param name="code">Код групи.</param>
    /// <param name="request">Назва, опис, активність, токен.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPut("{code}")]
    [ProducesResponseType<PermitGroupDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PermitGroupDto>> Update(
        string code, [FromBody] UpdatePermitGroupRequest request, CancellationToken ct)
        => Ok(await update.HandleAsync(code, request, ct).ConfigureAwait(false));

    /// <summary>
    /// Замінює склад групи цілком (єдиний шлях зміни складу): коди ОПУБЛІКОВАНИХ типів у порядку показу. Тип може бути
    /// у кількох групах. Право <c>Permit.EditType</c>. Токен <c>rowVersion</c> - з останнього читання.
    /// </summary>
    /// <param name="code">Код групи.</param>
    /// <param name="request">Коди типів і токен.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPut("{code}/types")]
    [ProducesResponseType<PermitGroupDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PermitGroupDto>> ReplaceTypes(
        string code, [FromBody] ReplacePermitGroupTypesRequest request, CancellationToken ct)
        => Ok(await replaceTypes.HandleAsync(code, request, ct).ConfigureAwait(false));

    /// <summary>
    /// Видаляє групу, яку не використовує жоден шаблон (інакше <c>409 ECR-PRM-4098</c>: лише деактивація).
    /// Право <c>Permit.EditType</c>.
    /// </summary>
    /// <param name="code">Код групи.</param>
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
}
