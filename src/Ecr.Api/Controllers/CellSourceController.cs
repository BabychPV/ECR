using Ecr.Application.Common;
using Ecr.Application.Integration.RawData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// «Звідки це значення» (<c>D-463</c>): джерело PI комірки документа. Право <c>Document.View</c> у проєкті
/// документа — доступ рівно до цієї комірки (видимість документа, Deny, колонка, Q-17).
/// </summary>
/// <remarks>
/// ⛔ Невидима комірка (документ, екземпляр, таблиця, колонка, рядок, чужий мапінг) — та сама
/// <c>404 err.ECR-DOC-0404.document</c>, що й у зрізу таблиці. Видима комірка без мапінгу — <c>200</c> з
/// порожнім <c>sources</c>: людина бачить комірку, приховувати нічого.
/// </remarks>
[ApiController]
[Route("api/v1/documents/{documentId:long}/tables/{tableInstanceId:long}/cell-source")]
[Authorize]
public sealed class CellSourceController(CellSourceHandler cellSource) : ControllerBase
{
    /// <summary>Мапінги, що живлять комірку: тег, згортка, одиниці, вікно періоду, останній збір, події покриття.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці документа.</param>
    /// <param name="rowKey">Ключ рядка.</param>
    /// <param name="columnDefId">Колонка (<c>ColumnDto.id</c> зрізу).</param>
    /// <param name="ct">Скасування.</param>
    [HttpGet]
    [ProducesResponseType<CellSourceView>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Summary(
        long documentId, long tableInstanceId, [FromQuery] string? rowKey, [FromQuery] int columnDefId,
        CancellationToken ct)
        => Ok(await cellSource.SummaryAsync(documentId, tableInstanceId, rowKey, columnDefId, ct).ConfigureAwait(false));

    /// <summary>Сторінка сирих точок тега мапінгу за період комірки, новіші першими.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці.</param>
    /// <param name="rowKey">Ключ рядка.</param>
    /// <param name="columnDefId">Колонка.</param>
    /// <param name="mapId">Мапінг із <c>sources[].mapId</c>; чужий — <c>404</c>.</param>
    /// <param name="cursor">Курсор наступної сторінки.</param>
    /// <param name="limit">Розмір сторінки 1..500; <c>0</c> — 100.</param>
    /// <param name="ct">Скасування.</param>
    [HttpGet("points")]
    [ProducesResponseType<PagedResult<RawPointView>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Points(
        long documentId,
        long tableInstanceId,
        [FromQuery] string? rowKey,
        [FromQuery] int columnDefId,
        [FromQuery] int mapId,
        [FromQuery] string? cursor,
        [FromQuery] int limit,
        CancellationToken ct)
        => Ok(await cellSource
            .PointsAsync(documentId, tableInstanceId, rowKey, columnDefId, mapId, cursor, limit, ct)
            .ConfigureAwait(false));

    /// <summary>Згорнутий ряд тега мапінгу за період комірки (≤ <paramref name="buckets"/> кошиків).</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці.</param>
    /// <param name="rowKey">Ключ рядка.</param>
    /// <param name="columnDefId">Колонка.</param>
    /// <param name="mapId">Мапінг із <c>sources[].mapId</c>.</param>
    /// <param name="buckets">Кількість кошиків 1..2000; <c>0</c> — 500.</param>
    /// <param name="ct">Скасування.</param>
    [HttpGet("series")]
    [ProducesResponseType<RawSeriesView>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Series(
        long documentId,
        long tableInstanceId,
        [FromQuery] string? rowKey,
        [FromQuery] int columnDefId,
        [FromQuery] int mapId,
        [FromQuery] int buckets,
        CancellationToken ct)
        => Ok(await cellSource
            .SeriesAsync(documentId, tableInstanceId, rowKey, columnDefId, mapId, buckets, ct)
            .ConfigureAwait(false));
}
