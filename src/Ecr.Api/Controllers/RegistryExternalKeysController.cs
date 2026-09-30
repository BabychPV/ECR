using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Зовнішні ідентифікатори записів довідника (<c>ФВ-8.10</c>, FEATURE-REGISTRY-SYNC S2):
/// зв'язок «запис × елемент джерела», за яким синк знаходить запис.
/// </summary>
/// <remarks>
/// ⚠ Довідник — у шляху, як у решти маршрутів довідників: право й грант
/// перевіряються саме на нього, а запис і зв'язок звіряються з ним на належність.
/// </remarks>
[ApiController]
[Route("api/v1/registries/{code}/external-keys")]
[Authorize]
public sealed class RegistryExternalKeysController(
    ListRegistryExternalKeysHandler list,
    BindRegistryExternalKeyHandler bind,
    UnbindRegistryExternalKeyHandler unbind) : ControllerBase
{
    /// <summary>
    /// Зв'язки записів довідника, за зростанням <c>id</c>. Право <c>Registry.View</c>
    /// або грант <c>Read</c> на довідник.
    /// </summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="entryId">Лише зв'язки цього запису.</param>
    /// <param name="dataSourceId">Лише зв'язки цього джерела.</param>
    /// <param name="cursor">Курсор наступної сторінки.</param>
    /// <param name="limit">Розмір сторінки 1..200; <c>0</c> — типове 50.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet]
    [ProducesResponseType<PagedResult<RegistryExternalKeyView>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> List(
        string code,
        [FromQuery] long? entryId,
        [FromQuery] int? dataSourceId,
        [FromQuery] string? cursor,
        [FromQuery] int limit,
        CancellationToken ct)
        => Ok(await list
            .HandleAsync(code, entryId, dataSourceId, new CursorRequest(limit == 0 ? 50 : limit, cursor), ct)
            .ConfigureAwait(false));

    /// <summary>
    /// Прив'язує запис до елемента джерела. Право <c>Registry.EditData</c> або грант
    /// <c>Write</c> на довідник.
    /// </summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="command">Запис, джерело, ідентифікатор у джерелі.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Пара «джерело + ідентифікатор» уже прив'язана (до будь-якого запису) —
    /// <c>409 ECR-REG-0409</c> (<c>externalKeyTaken</c>), а не другий рядок: той самий
    /// елемент джерела не може вказувати на два записи. Запис чужого довідника або
    /// видалений — <c>404</c>.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<RegistryExternalKeyView>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Bind(
        string code, [FromBody] BindRegistryExternalKeyCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var created = await bind.HandleAsync(code, command, ct).ConfigureAwait(false);

        return CreatedAtAction(nameof(List), new { code, entryId = created.RegistryEntryId }, created);
    }

    /// <summary>
    /// Відв'язує. Право <c>Registry.EditData</c> або грант <c>Write</c> на довідник.
    /// </summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="id">Зв'язок.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>Зв'язок запису іншого довідника — <c>404</c>, а не видалення «бо id збігся».</remarks>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unbind(string code, long id, CancellationToken ct)
    {
        await unbind.HandleAsync(code, id, ct).ConfigureAwait(false);

        return NoContent();
    }
}
