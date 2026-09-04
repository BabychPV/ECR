using Ecr.Application.Units;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Одиниці вимірювання і конверсії.</summary>
[ApiController]
[Route("api/v1/units")]
[Authorize]
public sealed class UnitsController(ConvertUnitHandler convert) : ControllerBase
{
    /// <summary>Перелік одиниць із їхніми розмірностями.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<IActionResult> List(CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: повернути uom.Unit разом із Dimension — без розмірності одиниця " +
            "не дає клієнту нічого перевірити.");

    /// <summary>
    /// Конвертує значення між одиницями.
    /// </summary>
    /// <remarks>
    /// ⚠ Конверсія можлива <b>лише в межах однієї розмірності</b>. Перехід
    /// «маса ↔ об'єм» — не конверсія, а контекстний коефіцієнт (щільність), і
    /// він живе в константах методології, а не в <c>uom.Conversion</c>
    /// (ФВ-16.5, <c>D-75</c>). Спроба такої конверсії відхиляється
    /// <c>ECR-UOM-4221</c>.
    /// </remarks>
    [HttpPost("convert")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> Convert([FromBody] ConvertUnitRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: делегувати convert.HandleAsync(request.Value, request.FromUnit, request.ToUnit, ct).");
}

/// <summary>Запит на конверсію.</summary>
/// <param name="Value">Значення; <c>decimal</c>, бо <c>float</c> заборонений (<c>D-30</c>).</param>
/// <param name="FromUnit">Код вихідної одиниці.</param>
/// <param name="ToUnit">Код цільової одиниці.</param>
public sealed record ConvertUnitRequest(decimal Value, string FromUnit, string ToUnit);
