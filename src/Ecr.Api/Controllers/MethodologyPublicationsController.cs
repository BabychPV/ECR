// src/Ecr.Api/Controllers/MethodologyPublicationsController.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Журнал публікацій версій методології (F-16, четвертий раунд UX).</summary>
/// <remarks>
/// ⚠ Окремий контролер, а не дія в <c>MethodologiesController</c>: той
/// паралельно виправляє інша лінія (маршрути <c>{id}</c>, B-07), і друга правка
/// того самого файлу означала б конфлікт злиття. Маршрут той самий простір —
/// <c>api/v1/methodologies/{id}/…</c>.
/// </remarks>
[ApiController]
[Route("api/v1/methodologies")]
[Authorize]
public sealed class MethodologyPublicationsController(ListMethodologyPublicationsHandler publications)
    : ControllerBase
{
    /// <summary>Публікації версій методології, найновіші першими. Право <c>Calculation.View</c>.</summary>
    /// <param name="id">Методологія.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("{id:int}/publications")]
    [ProducesResponseType<IReadOnlyList<MethodologyPublicationEntry>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Publications(int id, CancellationToken ct)
        => Ok(await publications.HandleAsync(id, ct).ConfigureAwait(false));
}
