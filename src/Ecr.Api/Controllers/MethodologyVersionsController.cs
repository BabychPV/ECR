using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Версія методології як ціле: покриття, порівняння, видалення чернетки (<c>BE-25</c>).
/// </summary>
/// <remarks>
/// Окремий контролер на тому самому префіксі, що й <see cref="MethodologiesController"/>:
/// той уже тримає двадцять дві дії, а нова функціональність іде в нові файли.
/// </remarks>
[ApiController]
[Route("api/v1/methodologies")]
[Authorize]
public sealed class MethodologyVersionsController(
    MethodologyCoverageHandler coverage,
    CompareMethodologyVersionsHandler compare,
    DeleteMethodologyVersionHandler delete) : ControllerBase
{
    /// <summary>
    /// Матриця покриття версії: вихід → колонки. Право <c>Calculation.View</c>.
    /// </summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("{id:int}/versions/{vid:int}/coverage")]
    [ProducesResponseType<MethodologyCoverageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MethodologyCoverageDto>> Coverage(int id, int vid, CancellationToken ct)
        => Ok(await coverage.HandleAsync(id, vid, ct).ConfigureAwait(false));

    /// <summary>
    /// Різниця версії з базовою: формули, константи, тести. Право <c>Calculation.View</c>.
    /// </summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія «стало».</param>
    /// <param name="baseVersionId">Версія «було» тієї самої методології.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("{id:int}/versions/{vid:int}/diff")]
    [ProducesResponseType<MethodologyVersionDiffDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MethodologyVersionDiffDto>> Diff(
        int id, int vid, [FromQuery] int baseVersionId, CancellationToken ct)
        => Ok(await compare.HandleAsync(id, vid, baseVersionId, ct).ConfigureAwait(false));

    /// <summary>
    /// Видаляє версію-чернетку. Право <c>Calculation.EditFormula</c>.
    /// </summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Опублікована, виведена з обігу або вже використана в розрахунку версія —
    /// <c>409 ECR-CALC-0409</c> із причиною в <c>reason</c> (рішення домену).
    /// </remarks>
    [HttpDelete("{id:int}/versions/{vid:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(int id, int vid, CancellationToken ct)
    {
        await delete.HandleAsync(id, vid, ct).ConfigureAwait(false);

        return NoContent();
    }
}
