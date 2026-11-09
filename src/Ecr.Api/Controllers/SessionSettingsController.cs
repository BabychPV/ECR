// src/Ecr.Api/Controllers/SessionSettingsController.cs
using Ecr.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Налаштування сеансу (<c>D-471</c>): вихід після N хвилин без дій. Право <c>Security.ManageUsers</c>.
/// </summary>
[ApiController]
[Route("api/v1/security/session-settings")]
[Authorize]
public sealed class SessionSettingsController(
    GetSessionSettingsHandler get, SaveSessionSettingsHandler save) : ControllerBase
{
    /// <summary>Поточне значення й межі.</summary>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet]
    [ProducesResponseType<SessionSettingsView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct)
        => Ok(await get.HandleAsync(ct).ConfigureAwait(false));

    /// <summary>Змінює межу бездіяльності; діє без перезапуску і для чинних сеансів.</summary>
    /// <param name="request">Нове значення.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPut]
    [ProducesResponseType<SessionSettingsView>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Put([FromBody] SessionSettingsInput request, CancellationToken ct)
        => Ok(await save.HandleAsync(request, ct).ConfigureAwait(false));
}
