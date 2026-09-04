using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Методології розрахунку: перелік, публікація версії, симуляція.</summary>
[ApiController]
[Route("api/v1/methodologies")]
[Authorize]
public sealed class MethodologiesController(
    ListMethodologiesHandler listMethodologies,
    PublishMethodologyHandler publish,
    SimulateMethodologyHandler simulate) : ControllerBase
{
    /// <summary>Перелік методологій. Право <c>Calculation.View</c>.</summary>
    /// <param name="ids">Методології, які цікавлять.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Три осі версійності не змішуються (ФВ-13.2): версія визначення, вікно
    /// дії і версія даних. Остання тут не з'являється взагалі — вона живе в
    /// довідниках.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<MethodologyDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MethodologyDto>>> List(
        [FromQuery] int[] ids, CancellationToken ct)
        => Ok(await listMethodologies.HandleAsync(ids ?? [], ct).ConfigureAwait(false));

    /// <summary>
    /// Публікує версію методології. Право <c>Calculation.Publish</c>.
    /// </summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія.</param>
    /// <param name="request">Причина зміни і дата набуття чинності.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ <b>Найнебезпечніша операція в системі</b> (ФВ-9.6): вона тихо змінює
    /// числа у вже поданих формах. Тому обов'язкові: причина зміни (ФВ-14.7),
    /// зелений тест (ФВ-9.12, інакше <c>ECR-CALC-0422</c>) і правило чотирьох
    /// очей — публікувати власну правку заборонено системно
    /// (<c>D-40</c>, <c>ECR-CALC-0409</c>).
    /// </remarks>
    [HttpPost("{id:int}/versions/{vid:int}/publish")]
    [ProducesResponseType<MethodologyPublicationDiff>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MethodologyPublicationDiff>> Publish(
        int id, int vid, [FromBody] PublishMethodologyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Diff РЕЗУЛЬТАТІВ повертається клієнтові, а не лише пишеться в журнал:
        // той, хто щойно опублікував, має побачити, що саме змінилося в числах.
        var diff = await publish
            .HandleAsync(vid, request.ChangeReason, request.EffectiveFrom, ct)
            .ConfigureAwait(false);

        return Ok(diff);
    }

    /// <summary>
    /// Прогін методології <b>без запису</b>. Право <c>Calculation.View</c>.
    /// </summary>
    /// <param name="id">Методологія.</param>
    /// <param name="request">Версія і період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Показує, що вийде, якщо опублікувати (ФВ-13.5). Нічого не зберігає:
    /// саме тому доступний із правом на перегляд, а не на публікацію.
    /// </remarks>
    [HttpPost("{id:int}/simulate")]
    [ProducesResponseType<SimulationResultDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SimulationResultDto>> Simulate(
        int id, [FromBody] SimulateMethodologyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await simulate
            .HandleAsync(request.MethodologyVersionId, request.PeriodKey, ct)
            .ConfigureAwait(false));
    }
}

/// <summary>Запит на публікацію версії методології.</summary>
/// <param name="ChangeReason">Причина зміни; обов'язкова і непорожня (ФВ-14.7).</param>
/// <param name="EffectiveFrom">
/// Дата набуття чинності. Обов'язкова: без неї невідомо, які періоди рахувати
/// цією версією, а які — попередньою.
/// </param>
public sealed record PublishMethodologyRequest(string ChangeReason, DateOnly? EffectiveFrom);

/// <summary>Запит на симуляцію.</summary>
/// <param name="MethodologyVersionId">Версія, яку проганяємо.</param>
/// <param name="PeriodKey">Період, на даних якого проганяємо.</param>
public sealed record SimulateMethodologyRequest(int MethodologyVersionId, int PeriodKey);
