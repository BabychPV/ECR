using Ecr.Application.Permits.Lists;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Списки вибору дозволів: спільні довідники, списки «лише для типу» і таблиці того самого дозволу - одним запитом
/// (Permits, PM-08e). Читання - <c>Registry.View</c>; зміна значень - <c>Registry.EditData</c> (спільний довідник) або
/// <c>Permit.EditType</c> (список «лише для типу»).
/// </summary>
[ApiController]
[Route("api/v1/permit-lists")]
[Authorize]
public sealed class PermitListsController(
    ListPermitListsHandler list,
    GetPermitListHandler get,
    ChangePermitListItemsHandler change) : ControllerBase
{
    /// <summary>
    /// Усі списки з кількістю значень і стовпцями, що їх беруть (опубліковані типи). Право <c>Registry.View</c>.
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<PermitListDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PermitListDto>>> List(CancellationToken ct)
        => Ok(await list.HandleAsync(ct).ConfigureAwait(false));

    /// <summary>Список зі значеннями. Право <c>Registry.View</c>.</summary>
    /// <param name="key">Ключ: <c>shared:{registryCode}</c>, <c>type:{type}:{table}:{field}</c> або <c>permit:{type}:{table}:{field}</c>.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("{key}")]
    [ProducesResponseType<PermitListDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PermitListDetailDto>> Get(string key, CancellationToken ct)
        => Ok(await get.HandleAsync(key, ct).ConfigureAwait(false));

    /// <summary>
    /// Додає, змінює й прибирає значення списку: усе або нічого. Спільний довідник - право <c>Registry.EditData</c> (або
    /// грант на нього), «лише для типу» - <c>Permit.EditType</c>; таблиця того самого дозволу - <c>422</c>.
    /// </summary>
    /// <param name="key">Ключ списку.</param>
    /// <param name="request">Зміни (1..200).</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPut("{key}/items")]
    [RequestSizeLimit(262_144)]
    [ProducesResponseType<PermitListDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PermitListDetailDto>> Change(
        string key, [FromBody] ChangePermitListItemsRequest request, CancellationToken ct)
        => Ok(await change.HandleAsync(key, request, ct).ConfigureAwait(false));
}
