using Ecr.Application.Common;
using Ecr.Application.Integration.RawData;
using Ecr.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// «Дані з PI» для подій PI (Event Frames, <c>D-465</c>): мапінги сутності-шаблону, події за вікном зі значеннями
/// атрибутів за останнім прочитанням синку й порівнянням із комірками, журнал видалень, CSV.
/// Право <c>Integration.View</c> або <c>Integration.Manage</c>.
/// </summary>
/// <remarks>
/// ⚠ Лише сховище: у PI наживо контролер не ходить і нічого не пише, крім події безпеки про експорт. Документ, якого
/// читач не бачить, — без ключа рядка, документа й значень комірок.
/// </remarks>
[ApiController]
[Route("api/v1/sources/{id:int}/raw-data/events")]
[Authorize]
public sealed class PiEventDataController(PiEventDataHandler events, IConfiguration configuration) : ControllerBase
{
    /// <summary>Мапінги подій сутності й періоди, що перетинають вікно.</summary>
    /// <param name="id">Сутність-шаблон подій.</param>
    /// <param name="fromUtc">Початок вікна (UTC); <c>null</c> — 7 діб до кінця.</param>
    /// <param name="toUtc">Кінець вікна (UTC, виключно); <c>null</c> — «зараз». Вікно ≤ 400 діб.</param>
    /// <param name="ct">Скасування.</param>
    [HttpGet("maps")]
    [ProducesResponseType<PiEventMapsView>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Maps(int id, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, CancellationToken ct)
        => Ok(await events.MapsAsync(id, fromUtc, toUtc, ct).ConfigureAwait(false));

    /// <summary>Сторінка подій мапінгу за вікном початку, новіші першими (курсор за початком і Id).</summary>
    /// <param name="id">Сутність-шаблон подій.</param>
    /// <param name="mapId">Мапінг; <c>null</c> — перший мапінг сутності.</param>
    /// <param name="fromUtc">Початок вікна (UTC, включно).</param>
    /// <param name="toUtc">Кінець вікна (UTC, виключно); ≤ 400 діб від початку.</param>
    /// <param name="status">Стани зв'язку; порожньо — усі.</param>
    /// <param name="element">Первинний елемент починається з (без регістру).</param>
    /// <param name="name">Назва події містить.</param>
    /// <param name="cursor">Курсор наступної сторінки.</param>
    /// <param name="limit">1..200; <c>0</c> — 50.</param>
    /// <param name="ct">Скасування.</param>
    [HttpGet]
    [ProducesResponseType<PagedResult<PiEventView>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> List(
        int id,
        [FromQuery] int? mapId,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] SourceEventLinkStatus[]? status,
        [FromQuery] string? element,
        [FromQuery] string? name,
        [FromQuery] string? cursor,
        [FromQuery] int limit,
        CancellationToken ct)
        => Ok(await events.EventsAsync(id, mapId, fromUtc, toUtc, status, element, name, cursor, limit, ct).ConfigureAwait(false));

    /// <summary>Журнал видалень рядків подій сутності (повна звірка) за вікно моменту видалення, новіші першими.</summary>
    /// <param name="id">Сутність-шаблон подій.</param>
    /// <param name="fromUtc">Початок вікна.</param>
    /// <param name="toUtc">Кінець вікна (≤ 400 діб).</param>
    /// <param name="ct">Скасування.</param>
    [HttpGet("removed")]
    [ProducesResponseType<PiEventRemovalsView>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Removed(int id, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, CancellationToken ct)
        => Ok(await events.RemovalsAsync(id, fromUtc, toUtc, ct).ConfigureAwait(false));

    /// <summary>Події мапінгу × поля у CSV потоком.</summary>
    /// <remarks>
    /// Стеля <c>Integration:RawDataExportMaxRows</c> (типово 100 000 рядків «подія × поле»); понад неї —
    /// <c>422 auditExportTooLarge</c> до першого байта. UTF-8 з BOM; час — UTC ISO 8601; текст джерела й комірок
    /// нейтралізується від формул. Подія безпеки <c>PiEventDataExported</c> (без значень). Комірки документа, якого
    /// читач не бачить, — порожні.
    /// </remarks>
    /// <param name="id">Сутність-шаблон подій.</param>
    /// <param name="mapId">Мапінг; <c>null</c> — перший.</param>
    /// <param name="fromUtc">Початок вікна.</param>
    /// <param name="toUtc">Кінець вікна (≤ 400 діб).</param>
    /// <param name="status">Стани зв'язку.</param>
    /// <param name="element">Первинний елемент починається з.</param>
    /// <param name="name">Назва містить.</param>
    /// <param name="ct">Скасування.</param>
    [HttpGet("export.csv")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(FileResult))]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Export(
        int id,
        [FromQuery] int? mapId,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] SourceEventLinkStatus[]? status,
        [FromQuery] string? element,
        [FromQuery] string? name,
        CancellationToken ct)
    {
        var maxRows = configuration.GetValue("Integration:RawDataExportMaxRows", RawDataHandler.RawExportMaxRowsDefault);

        // Усі відмови (403/404/422) — тут, до першого байта відповіді.
        var export = await events
            .PrepareExportAsync(id, mapId, fromUtc, toUtc, status, element, name, maxRows, ct)
            .ConfigureAwait(false);

        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
        {
            FileName = export.FileName,
        }.ToString();

        await using var writer = new StreamWriter(Response.Body, new System.Text.UTF8Encoding(true), 64 * 1024);
        await writer.WriteAsync(CsvFormat.Row(PiEventDataHandler.CsvColumns)).ConfigureAwait(false);

        await foreach (var row in export.Rows.WithCancellation(ct).ConfigureAwait(false))
        {
            await writer.WriteAsync(PiEventDataHandler.ToCsv(row)).ConfigureAwait(false);
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
        return new EmptyResult();
    }
}
