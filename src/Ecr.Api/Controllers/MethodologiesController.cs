using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Методології розрахунку: перелік, публікація версії, симуляція.</summary>
[ApiController]
[Route("api/v1/methodologies")]
[Authorize]
public sealed class MethodologiesController(
    PublishMethodologyHandler publish,
    SimulateMethodologyHandler simulate) : ControllerBase
{
    /// <summary>Перелік методологій. Право <c>Calculation.View</c>.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<IActionResult> List(CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: методології з їхніми версіями, вікнами дії і Status; " +
            "три осі версійності не змішувати (ФВ-13.2).");

    /// <summary>
    /// Публікує версію методології. Право <c>Calculation.Publish</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Найнебезпечніша операція в системі</b> (ФВ-9.6): вона тихо змінює
    /// числа у вже поданих формах. Тому обов'язкові: причина зміни (ФВ-14.7),
    /// зелений тест (ФВ-9.12, інакше <c>ECR-CALC-0422</c>) і правило чотирьох
    /// очей — публікувати власну останню правку заборонено системно
    /// (<c>D-40</c>, <c>ECR-CALC-0409</c>).
    /// </remarks>
    [HttpPost("{id:int}/versions/{vid:int}/publish")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> Publish(int id, int vid, [FromBody] PublishMethodologyRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перевірити Calculation.Publish; делегувати publish.HandleAsync(vid, request.ChangeReason, ct).");

    /// <summary>
    /// Прогін методології <b>без запису</b>. Право <c>Calculation.View</c>.
    /// </summary>
    /// <remarks>
    /// Показує, що вийде, якщо опублікувати (ФВ-13.5). Нічого не зберігає:
    /// саме тому доступний із правом на перегляд, а не на публікацію.
    /// </remarks>
    [HttpPost("{id:int}/simulate")]
    [ProducesResponseType<SimulationResultDto>(StatusCodes.Status200OK)]
    public Task<ActionResult<SimulationResultDto>> Simulate(
        int id, [FromBody] SimulateMethodologyRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: делегувати simulate.HandleAsync(request.MethodologyVersionId, request.PeriodKey, ct).");
}

/// <summary>Запит на публікацію версії методології.</summary>
/// <param name="ChangeReason">Причина зміни; обов'язкова і непорожня (ФВ-14.7).</param>
public sealed record PublishMethodologyRequest(string ChangeReason);

/// <summary>Запит на симуляцію.</summary>
/// <param name="MethodologyVersionId">Версія, яку проганяємо.</param>
/// <param name="PeriodKey">Період, на даних якого проганяємо.</param>
public sealed record SimulateMethodologyRequest(int MethodologyVersionId, int PeriodKey);
