using Ecr.Application.Registries.Impact;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Свіжість результатів після правки довідника (RT-25, FEATURE-REGISTRY-TABLES §5.10).
/// </summary>
/// <remarks>
/// ⚠ Окремий контролер: <see cref="RegistriesController"/> уже несе сімнадцять обробників, а тут
/// росте власний зріз — читання зачеплених документів і (наступним кроком) постановка перерахунку.
/// </remarks>
[ApiController]
[Route("api/v1/registries")]
[Authorize]
public sealed class RegistryImpactController(GetRegistryImpactHandler impact, RecalculateImpactedHandler recalculate) : ControllerBase
{
    /// <summary>
    /// Документи ВІДКРИТИХ періодів, чиї результати пораховано методологією, що читає довідник.
    /// Права <c>Registry.View</c> і <c>Calculation.View</c> (у проєкті документа).
    /// </summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>Закриті періоди не повертаються ніколи; перерахунок цей виклик не ставить.</remarks>
    [HttpGet("{code}/impact")]
    [ProducesResponseType<RegistryImpactResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RegistryImpactResponse>> Impact(string code, CancellationToken ct)
        => Ok(await impact.HandleAsync(code, ct).ConfigureAwait(false));

    /// <summary>
    /// Ставить перерахунок зачеплених документів у чергу. Права <c>Calculation.Recalculate</c> у проєкті
    /// кожного документа і доступ до довідника.
    /// </summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="request">Документи з переліку <c>impact</c> (<c>null</c> — усі доступні) і причина.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ <c>202</c>: рахує черга, а не запит. Один <c>jobId</c> на набір; стан — GET /api/v1/jobs/{jobId}.
    /// </remarks>
    [HttpPost("{code}/recalculate-impacted")]
    [ProducesResponseType<Contracts.JobAcceptedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RecalculateImpacted(
        string code, [FromBody] RecalculateImpactedRequest request, CancellationToken ct)
    {
        var jobId = await recalculate.HandleAsync(code, request, ct).ConfigureAwait(false);

        return Accepted(new Contracts.JobAcceptedResponse(jobId));
    }
}