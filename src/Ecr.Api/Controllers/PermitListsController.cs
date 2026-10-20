using Ecr.Application.Permits.Lists;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Списки вибору дозволів: спільні довідники, списки «лише для типу» і таблиці того самого дозволу - одним запитом
/// (Permits, PM-08e). Читання - <c>Registry.View</c>.
/// </summary>
[ApiController]
[Route("api/v1/permit-lists")]
[Authorize]
public sealed class PermitListsController(
    ListPermitListsHandler list,
    GetPermitListHandler get) : ControllerBase
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
}
