using Ecr.Application.Common;
using Ecr.Application.Integration.RawData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// «Дані з PI» (<c>D-463</c>): уже зібрані точки сутності збору (<c>ext.RawDataPoint</c>).
/// Право <c>Integration.View</c> або <c>Integration.Manage</c>.
/// </summary>
/// <remarks>
/// ⚠ Лише читання зібраного: у джерело наживо цей контролер не ходить і нічого не пише (PI AF —
/// виключно джерело, <c>D-44</c>), крім події безпеки про експорт.
/// </remarks>
[ApiController]
[Route("api/v1/sources/{id:int}/raw-data")]
[Authorize]
public sealed class PiRawDataController(RawDataHandler rawData, IConfiguration configuration) : ControllerBase
{
    /// <summary>Сутність і її теги (поля мапінгів; без мапінгів — шлях самої сутності).</summary>
    /// <param name="id">Сутність збору.</param>
    /// <param name="ct">Скасування.</param>
    [HttpGet("tags")]
    [ProducesResponseType<RawDataTagsView>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Tags(int id, CancellationToken ct)
        => Ok(await rawData.TagsAsync(id, ct).ConfigureAwait(false));

    /// <summary>Сторінка точок тега, новіші першими (курсор за міткою часу).</summary>
    /// <param name="id">Сутність збору.</param>
    /// <param name="path">Тег — шлях атрибута в джерелі (1..400 символів, дослівно).</param>
    /// <param name="fromUtc">Початок вікна (UTC, включно); <c>null</c> — 7 діб до кінця.</param>
    /// <param name="toUtc">Кінець вікна (UTC, виключно); <c>null</c> — «зараз». Вікно ≤ 400 діб.</param>
    /// <param name="cursor">Курсор наступної сторінки.</param>
    /// <param name="limit">Розмір сторінки 1..500; <c>0</c> — 100.</param>
    /// <param name="ct">Скасування.</param>
    [HttpGet("points")]
    [ProducesResponseType<PagedResult<RawPointView>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Points(
        int id,
        [FromQuery] string? path,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] string? cursor,
        [FromQuery] int limit,
        CancellationToken ct)
        => Ok(await rawData.PointsAsync(id, path, fromUtc, toUtc, cursor, limit, ct).ConfigureAwait(false));

    /// <summary>
    /// Згорнутий ряд тега для графіка: ≤ <paramref name="buckets"/> кошиків (кількість, мін, макс, середнє)
    /// на все вікно — розмір відповіді не залежить від кількості точок.
    /// </summary>
    /// <param name="id">Сутність збору.</param>
    /// <param name="path">Тег.</param>
    /// <param name="fromUtc">Початок вікна.</param>
    /// <param name="toUtc">Кінець вікна (≤ 400 діб від початку).</param>
    /// <param name="buckets">Кількість кошиків 1..2000; <c>0</c> — 500.</param>
    /// <param name="ct">Скасування.</param>
    [HttpGet("series")]
    [ProducesResponseType<RawSeriesView>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Series(
        int id,
        [FromQuery] string? path,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] int buckets,
        CancellationToken ct)
        => Ok(await rawData.SeriesAsync(id, path, fromUtc, toUtc, buckets, ct).ConfigureAwait(false));

    /// <summary>Покриття вікна збором: злиті зібрані інтервали, прогалини, останній прогін і останній успіх.</summary>
    /// <param name="id">Сутність збору.</param>
    /// <param name="fromUtc">Початок вікна.</param>
    /// <param name="toUtc">Кінець вікна (≤ 400 діб від початку).</param>
    /// <param name="ct">Скасування.</param>
    [HttpGet("coverage")]
    [ProducesResponseType<RawCoverageView>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Coverage(
        int id, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, CancellationToken ct)
        => Ok(await rawData.CoverageAsync(id, fromUtc, toUtc, ct).ConfigureAwait(false));

    /// <summary>Точки тега у CSV потоком, за зростанням часу.</summary>
    /// <remarks>
    /// Стеля <c>Integration:RawDataExportMaxRows</c> (типово 100 000); понад неї — <c>422 auditExportTooLarge</c>
    /// до першого байта. UTF-8 з BOM; час — UTC ISO 8601; текстові значення, що починаються з <c>= + - @</c>,
    /// нейтралізуються. Експорт пишеться подією безпеки <c>PiRawDataExported</c> (без значень).
    /// </remarks>
    /// <param name="id">Сутність збору.</param>
    /// <param name="path">Тег.</param>
    /// <param name="fromUtc">Початок вікна.</param>
    /// <param name="toUtc">Кінець вікна (≤ 400 діб від початку).</param>
    /// <param name="ct">Скасування.</param>
    [HttpGet("export.csv")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(FileResult))]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Export(
        int id,
        [FromQuery] string? path,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        CancellationToken ct)
    {
        var maxRows = configuration.GetValue("Integration:RawDataExportMaxRows", RawDataHandler.RawExportMaxRowsDefault);

        // Усі відмови (403/404/422) — тут, до першого байта відповіді.
        var export = await rawData.PrepareExportAsync(id, path, fromUtc, toUtc, maxRows, ct).ConfigureAwait(false);

        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
        {
            FileName = export.FileName,
        }.ToString();

        await using var writer = new StreamWriter(Response.Body, new System.Text.UTF8Encoding(true), 64 * 1024);
        await writer.WriteAsync(CsvFormat.Row(RawDataQuery.CsvColumns)).ConfigureAwait(false);

        await foreach (var row in export.Rows.WithCancellation(ct).ConfigureAwait(false))
        {
            await writer.WriteAsync(RawDataQuery.ToCsv(row)).ConfigureAwait(false);
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
        return new EmptyResult();
    }
}
