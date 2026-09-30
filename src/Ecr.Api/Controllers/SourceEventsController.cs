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
    ProbeSourceEventsHandler probe) : ControllerBase
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
}
