using Ecr.Application.Documents.CalcStatus;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Ecr.Api.Controllers;

/// <summary>✎ D-577 (L3): стан розрахунку документа для чипа рядка стану.</summary>
[ApiController]
[Route("api/v1/documents")]
[Authorize]
public sealed class DocumentCalcStatusController(GetDocumentCalcStatusHandler status) : ControllerBase
{
    /// <summary>
    /// Стан розрахунку документа за період: застарілі місяці (і які з них перерахунок перерахує), перерахунки в черзі й у
    /// роботі (місце, орієнтовний старт, «місяць k з N»), останній актуальний прогін і останній провал. Право
    /// <c>Document.View</c> у проєкті документа; невидимий документ — 404.
    /// </summary>
    /// <param name="id">Документ.</param>
    /// <param name="periodKey">Період на екрані (рік*100+номер).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// <c>resultsStale</c> — лише місяці, які можна перерахувати (Q3): закриті, ще не відкриті, затверджені місяці й
    /// архівований проєкт ідуть у <c>staleMonths</c> з <c>recalculable = false</c> і <c>lockedReason</c>. Документ річної
    /// розкладки — <c>year</c> заповнено, <c>staleMonths</c> — по місяцях року. <c>Cache-Control: no-store</c>: клієнт опитує,
    /// поки <c>activeJobs</c> не порожній.
    /// </remarks>
    [HttpGet("{id:long}/calc-status")]
    [ProducesResponseType<DocumentCalcStatusDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Get(long id, [FromQuery] int periodKey, CancellationToken ct)
    {
        var result = await status.HandleAsync(id, periodKey, ct).ConfigureAwait(false);
        Response.Headers[HeaderNames.CacheControl] = "no-store";
        return Ok(result);
    }
}
