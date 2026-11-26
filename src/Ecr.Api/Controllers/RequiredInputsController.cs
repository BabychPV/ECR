using Ecr.Application.Documents.RequiredInputs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Ecr.Api.Controllers;

/// <summary>✎ D-577 (L6): порожні обов'язкові входи методологій таблиці — одразу при відкритті.</summary>
[ApiController]
[Route("api/v1/documents")]
[Authorize]
public sealed class RequiredInputsController(GetRequiredInputsHandler requiredInputs) : ControllerBase
{
    /// <summary>
    /// Комірки таблиці, де порожній обов'язковий вхід методології (<c>ECR-CALC-0437</c>): рядки з хоч одним введеним
    /// значенням і наперед задані рядки шаблону. <c>level = block</c> не дасть подати аркуш (Q6) і зберегти рядок. Право
    /// <c>Document.View</c>; доступ як у зрізу (схована таблиця й чужий екземпляр — 404, сховані колонки відсутні).
    /// </summary>
    /// <param name="id">Документ.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("{id:long}/tables/{tableInstanceId:long}/required-inputs")]
    [ProducesResponseType<RequiredInputsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(long id, long tableInstanceId, CancellationToken ct)
    {
        var result = await requiredInputs.HandleAsync(id, tableInstanceId, ct).ConfigureAwait(false);
        Response.Headers[HeaderNames.CacheControl] = "no-store";
        return Ok(result);
    }
}
