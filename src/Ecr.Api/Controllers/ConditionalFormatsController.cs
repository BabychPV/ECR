using Ecr.Application.Templates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Правила умовного форматування версії шаблону (ФВ-2.6/2.7).</summary>
[ApiController]
[Route("api/v1/template-versions/{id:int}/conditional-formats")]
[Authorize]
public sealed class ConditionalFormatsController(
    GetConditionalFormatsHandler get,
    SaveConditionalFormatsHandler save) : ControllerBase
{
    /// <summary>Правила версії: колонка → порядок застосування. Право <c>Template.View</c>.</summary>
    /// <param name="id">Версія шаблону.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ConditionalFormatRuleDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<ConditionalFormatRuleDto>>> List(int id, CancellationToken ct)
        => Ok(await get.HandleAsync(id, ct).ConfigureAwait(false));

    /// <summary>
    /// Замінює набір правил версії-чернетки (порожній список — прибрати всі).
    /// Право <c>Template.Edit</c>; у замороженій версії — <c>409 ECR-TMPL-0409</c>.
    /// </summary>
    /// <param name="id">Версія-чернетка.</param>
    /// <param name="request">Повний набір правил; порядок у колонці — порядок у списку.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPut]
    [ProducesResponseType<IReadOnlyList<ConditionalFormatRuleDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IReadOnlyList<ConditionalFormatRuleDto>>> Replace(
        int id, [FromBody] SaveConditionalFormatsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await save.HandleAsync(id, request.Rules, ct).ConfigureAwait(false));
    }
}

/// <summary>Тіло <c>PUT …/conditional-formats</c>.</summary>
/// <param name="Rules">Повний набір правил версії.</param>
public sealed record SaveConditionalFormatsRequest(IReadOnlyList<ConditionalFormatRuleDto> Rules);