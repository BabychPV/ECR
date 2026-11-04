using Ecr.Application.Documents.SheetChanges;
using Ecr.Application.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Ecr.Api.Controllers;

/// <summary>Живе оновлення сітки: дешеве опитування «чи змінив хтось дані аркуша, відколи я читав».</summary>
[ApiController]
[Route("api/v1/documents")]
[Authorize]
public sealed class SheetChangesController(GetSheetChangesHandler changes) : ControllerBase
{
    /// <summary>
    /// Маркер змін даних аркуша за період. Право <c>Document.View</c>; доступ — як на читання аркуша.
    /// </summary>
    /// <param name="id">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період (рік*100+номер).</param>
    /// <param name="since">Маркер попереднього опитування; без нього — лише поточний маркер (<c>changed = false</c>).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="sheetCopyId">RS: копія аркуша (<c>sheetCopyId</c> з <c>GET …/tables</c>); без нього — оригінал.</param>
    /// <remarks>
    /// <c>changed = true</c> — після <c>since</c> дані видимих таблиць аркуша змінив хтось інший; власні записи
    /// викликача лише пересувають маркер. Схована таблиця не потрапляє в <c>changedTables</c>; невидимий документ і
    /// схований аркуш — <c>404 ECR-DOC-0404</c>. Зіпсований маркер — як перше опитування. <c>Cache-Control: no-store</c>.
    /// ✎ LIVE-STATUS: <c>sheetStatus</c>/<c>statusRevision</c> — стан погодження аркуша; <c>statusChanged</c> — він
    /// змінився після <c>since</c> (будь-який перехід). Відповідь несе <c>ETag</c> (усе, крім маркера); <c>If-None-Match</c>
    /// зі збігом — <c>304</c> без тіла (стан і дані аркуша для читача ті самі; маркер лишається попередній).
    /// </remarks>
    [HttpGet("{id:long}/sheets/{sheetDefId:int}/changes")]
    [ProducesResponseType<SheetChangesDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Changes(
        long id, int sheetDefId, [FromQuery] int periodKey, [FromQuery] string? since, CancellationToken ct,
        [FromQuery] long? sheetCopyId = null)
    {
        var result = await changes.HandleAsync(id, sheetDefId, periodKey, since, ct, sheetCopyId).ConfigureAwait(false);
        var etag = result.ETag();
        Response.Headers[HeaderNames.CacheControl] = "no-store";
        Response.Headers[HeaderNames.ETag] = etag;
        if (UiStringResolver.IsNotModified(Request.Headers[HeaderNames.IfNoneMatch], etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(result);
    }
}
