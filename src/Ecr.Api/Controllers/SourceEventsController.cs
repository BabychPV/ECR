// src/Ecr.Api/Controllers/SourceEventsController.cs
using Ecr.Application.Common;
using Ecr.Application.Integration.SourceEvents;
using Ecr.Application.Ports;
using Ecr.Application.Sources;
using Ecr.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Події джерела (PI Event Frames) → рядки динамічних таблиць: каталог шаблонів, проба, мапінг,
/// таблиця подій, «Отримати з PI зараз» (HSE301 A6, FEATURE-HSE301-VIEW §4.7).
/// </summary>
/// <remarks>
/// ⚠ Один контролер на всю родину: усі дії ходять у обробники подій, що діляться сховищем
/// <c>ISourceEventMapStore</c>. Права перевіряє обробник (архітектурне правило 7), а не атрибут.
/// </remarks>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class SourceEventsController(
    ListEventTemplatesHandler templates,
    ProbeSourceEventsHandler probe,
    ListSourceEventsHandler events,
    SyncSourceEventsHandler sync,
    ListSourceEventMapsHandler maps,
    CreateSourceEventMapHandler create,
    UpdateSourceEventMapHandler update,
    DeleteSourceEventMapHandler delete) : ControllerBase
{
    /// <summary>
    /// Каталог шаблонів подій джерела й їхніх атрибутів. Право <c>Integration.View</c> або <c>Integration.Manage</c>.
    /// </summary>
    /// <param name="id">Джерело даних.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// Нічого не пише. Без налаштованого запиту каталогу — <c>422 ECR-INT-0422</c>
    /// (<c>.eventQueryNotConfigured</c>, <c>.queryKindNotConfigured</c> чи <c>.queryKindNotSupported</c>): стан
    /// «не налаштовано», а не порожній список. Недоступне джерело — <c>503 ECR-INT-0503</c>.
    /// </remarks>
    [HttpGet("data-sources/{id:int}/event-templates")]
    [ProducesResponseType<IReadOnlyList<SourceEventTemplate>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> EventTemplates(int id, CancellationToken ct)
        => Ok(await templates.HandleAsync(id, ct).ConfigureAwait(false));

    /// <summary>
    /// Пробне читання подій шаблону за вікно — без запису. Право <c>Integration.Manage</c>.
    /// </summary>
    /// <param name="id">Джерело даних.</param>
    /// <param name="request">Шаблон, вікно (типово 30 днів), атрибути, стеля подій (типово 20, до 100).</param>
    /// <param name="ct">Скасування.</param>
    [HttpPost("data-sources/{id:int}/probe-events")]
    [ProducesResponseType<SourceEventProbeResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ProbeEvents(
        int id, [FromBody] SourceEventProbeRequest request, CancellationToken ct)
        => Ok(await probe.HandleAsync(id, request, ct).ConfigureAwait(false));

    /// <summary>
    /// Таблиця подій сутності: стан зв'язку «подія ↔ рядок», час у поясі проєкту й UTC, ключ рядка, документ і
    /// період. Права — <c>Read</c> на документ мапінгу; невидимі документи для користувача не існують.
    /// </summary>
    /// <param name="id">Сутність-шаблон подій.</param>
    /// <param name="mapId">Лише цей мапінг.</param>
    /// <param name="documentId">Лише цей документ.</param>
    /// <param name="status">Лише ці стани (можна повторювати); порожньо — усі.</param>
    /// <param name="fromUtc">Початок події не раніше (UTC, включно).</param>
    /// <param name="toUtc">Початок події раніше (UTC, виключно).</param>
    /// <param name="periodKey">Лише цей період.</param>
    /// <param name="cursor">Курсор наступної сторінки.</param>
    /// <param name="limit">Розмір сторінки 1..500; <c>0</c> — типове 50.</param>
    /// <param name="ct">Скасування.</param>
    [HttpGet("sources/{id:int}/source-events")]
    [ProducesResponseType<PagedResult<SourceEventRowDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SourceEvents(
        int id,
        [FromQuery] int? mapId,
        [FromQuery] long? documentId,
        [FromQuery] SourceEventLinkStatus[]? status,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] int? periodKey,
        [FromQuery] string? cursor,
        [FromQuery] int limit,
        CancellationToken ct)
        => Ok(await events
            .HandleAsync(
                new SourceEventsFilter(id, mapId, documentId, status, AsUtc(fromUtc), AsUtc(toUtc), periodKey),
                new CursorRequest(limit == 0 ? 50 : limit, cursor),
                ct)
            .ConfigureAwait(false));

    /// <summary>
    /// «Отримати з PI зараз»: ставить синхронізацію подій сутності в чергу. Право <c>Integration.Manage</c>.
    /// </summary>
    /// <param name="id">Сутність-шаблон подій.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// Повторне натискання зливається з задачею, що вже чекає чи виконується (ціль <c>source-events-e{id}</c>), — та
    /// сама задача, що й за розкладом. Немає активного мапінгу подій — <c>422</c>.
    /// </remarks>
    [HttpPost("sources/{id:int}/source-events/sync")]
    [ProducesResponseType<Contracts.JobAcceptedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Sync(int id, CancellationToken ct)
        => Accepted(new Contracts.JobAcceptedResponse(await sync.HandleAsync(id, ct).ConfigureAwait(false)));

    /// <summary>Мапінги подій; за <paramref name="sourceEntityId"/> — лише сутності. Право <c>Integration.View</c> або <c>Integration.Manage</c>.</summary>
    /// <param name="sourceEntityId">Сутність-шаблон подій; <c>null</c> — усі.</param>
    /// <param name="ct">Скасування.</param>
    [HttpGet("source-event-maps")]
    [ProducesResponseType<IReadOnlyList<SourceEventMapDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListMaps([FromQuery] int? sourceEntityId, CancellationToken ct)
        => Ok(await maps.ListAsync(sourceEntityId, ct).ConfigureAwait(false));

    /// <summary>Один мапінг подій з полями й відповідностями значень. Право <c>Integration.View</c> або <c>Integration.Manage</c>.</summary>
    /// <param name="id">Мапінг.</param>
    /// <param name="ct">Скасування.</param>
    [HttpGet("source-event-maps/{id:int}")]
    [ProducesResponseType<SourceEventMapDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMap(int id, CancellationToken ct)
        => Ok(await maps.GetAsync(id, ct).ConfigureAwait(false));

    /// <summary>
    /// Заводить мапінг подій «шаблон → динамічна таблиця документа». Право <c>Integration.Manage</c> і грант
    /// <c>Manage</c> на проєкт документа.
    /// </summary>
    /// <param name="request">Сутність, документ, таблиця, режим об'єму, звуження, поля.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// Поля обов'язково містять <c>$start</c> і <c>$end</c> на Date-колонки (<c>422 ECR-INT-0422</c>). Слід — у
    /// журналі структурних змін.
    /// </remarks>
    [HttpPost("source-event-maps")]
    [ProducesResponseType<SourceEventMapDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateMap([FromBody] CreateSourceEventMapCommand request, CancellationToken ct)
        => Ok(await create.HandleAsync(request, ct).ConfigureAwait(false));

    /// <summary>
    /// Повна заміна налаштувань мапінгу: режим об'єму, звуження, поля, <c>isActive</c> (пауза й відновлення).
    /// Право <c>Integration.Manage</c> і грант <c>Manage</c> на проєкт документа.
    /// </summary>
    /// <param name="id">Мапінг.</param>
    /// <param name="request">Нові налаштування.</param>
    /// <param name="ct">Скасування.</param>
    [HttpPut("source-event-maps/{id:int}")]
    [ProducesResponseType<SourceEventMapDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UpdateMap(
        int id, [FromBody] UpdateSourceEventMapCommand request, CancellationToken ct)
        => Ok(await update.HandleAsync(id, request, ct).ConfigureAwait(false));

    /// <summary>
    /// Видаляє мапінг подій, за яким ще нічого не синхронізовано. Право <c>Integration.Manage</c> і грант
    /// <c>Manage</c> на проєкт документа.
    /// </summary>
    /// <param name="id">Мапінг.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// Мапінг зі зв'язками «подія ↔ рядок» — <c>409 ECR-INT-0409</c>: вихід — пауза (<c>isActive = false</c>).
    /// </remarks>
    [HttpDelete("source-event-maps/{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteMap(int id, CancellationToken ct)
    {
        await delete.HandleAsync(id, ct).ConfigureAwait(false);

        return NoContent();
    }

    private static DateTime? AsUtc(DateTime? value) => value is { } v
        ? v.Kind switch
        {
            DateTimeKind.Utc => v,
            DateTimeKind.Local => v.ToUniversalTime(),
            _ => DateTime.SpecifyKind(v, DateTimeKind.Utc),
        }
        : null;
}
