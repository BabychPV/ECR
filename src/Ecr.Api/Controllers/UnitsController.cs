using Ecr.Application.Ports;
using Ecr.Application.Units;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Одиниці вимірювання і конверсії.</summary>
[ApiController]
[Route("api/v1/units")]
[Authorize]
public sealed class UnitsController(ListUnitsHandler list, ConvertUnitHandler convert) : ControllerBase
{
    /// <summary>Перелік одиниць із їхніми розмірностями.</summary>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Розмірність віддається разом із одиницею: без неї клієнт не може
    /// перевірити нічого — ні того, що конверсія можлива, ні того, що
    /// величини сумісні (ФВ-16.2).
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<UnitRef>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<UnitRef>>> List(CancellationToken ct)
        => Ok(await list.HandleAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// Конвертує значення між одиницями.
    /// </summary>
    /// <param name="request">Значення і коди одиниць.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Конверсія можлива <b>лише в межах однієї розмірності</b>. Перехід
    /// «маса ↔ об'єм» — не конверсія, а контекстний коефіцієнт (щільність), і
    /// він живе в константах методології, а не в <c>uom.Conversion</c>
    /// (ФВ-16.5, <c>D-75</c>).
    /// </remarks>
    [HttpPost("convert")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Convert(
        [FromBody] ConvertUnitRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var value = await convert
            .HandleAsync(request.Value, request.FromUnit, request.ToUnit, ct)
            .ConfigureAwait(false);

        return Ok(new { value, unit = request.ToUnit });
    }
}

/// <summary>Запит на конверсію.</summary>
/// <param name="Value">Значення; <c>decimal</c>, бо <c>float</c> заборонений (<c>D-30</c>).</param>
/// <param name="FromUnit">Код вихідної одиниці.</param>
/// <param name="ToUnit">Код цільової одиниці.</param>
public sealed record ConvertUnitRequest(decimal Value, string FromUnit, string ToUnit);
