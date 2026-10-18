using Ecr.Application.Permits.Bindings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Прив'язки таблиць аркуша версії шаблону до таблиць типів дозволів: перелік, читання, запис, видалення й перегляд
/// очима користувача (Permits, PM-09). Права: читання й перегляд - <c>Template.View</c>, запис і видалення -
/// <c>Template.Edit</c>; перегляд з документа додатково вимагає <c>Document.View</c>.
/// </summary>
[ApiController]
[Route("api/v1/template-versions/{id:int}")]
[Authorize]
public sealed class PermitBindingsController(
    ListPermitBindingsHandler list,
    GetPermitBindingHandler get,
    SavePermitBindingHandler save,
    DeletePermitBindingHandler delete,
    PreviewPermitBindingHandler preview) : ControllerBase
{
    /// <summary>Прив'язки версії. Право <c>Template.View</c>.</summary>
    /// <param name="id">Версія шаблону.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Порожній перелік - законна відповідь (механізм опційний); відповідь - конверт із <c>isEditable</c>, щоб клієнт не
    /// показував «нова прив'язка» там, де версія заморожена.
    /// </remarks>
    [HttpGet("permit-bindings")]
    [ProducesResponseType<PermitBindingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PermitBindingsDto>> List(int id, CancellationToken ct)
        => Ok(await list.HandleAsync(id, ct).ConfigureAwait(false));

    /// <summary>Одна прив'язка за кодом. Право <c>Template.View</c>.</summary>
    /// <param name="id">Версія шаблону.</param>
    /// <param name="code">Код прив'язки.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("permit-bindings/{code}")]
    [ProducesResponseType<PermitBindingDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PermitBindingDto>> Get(int id, string code, CancellationToken ct)
        => Ok(await get.HandleAsync(id, code, ct).ConfigureAwait(false));

    /// <summary>
    /// Створює або замінює прив'язку чернетки (колонки замінюються цілком). Право <c>Template.Edit</c>.
    /// </summary>
    /// <param name="id">Версія-чернетка.</param>
    /// <param name="code">Код прив'язки (унікальний по всій системі).</param>
    /// <param name="request">Налаштування прив'язки.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ <c>PUT</c> за кодом: створення й зміна - та сама дія. <c>409 ECR-PRM-4101</c> - таблицю аркуша вже зайнято чи
    /// код зайнятий; <c>422 ECR-PRM-0422</c> - прив'язка не складається (<c>details.caseKeys</c> - усі випадки);
    /// <c>409 ECR-TMPL-0409</c> - версія заморожена.
    /// </remarks>
    [HttpPut("permit-bindings/{code}")]
    [ProducesResponseType<PermitBindingDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PermitBindingDto>> Save(
        int id, string code, [FromBody] SavePermitBindingRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await save.HandleAsync(id, code, request, ct).ConfigureAwait(false));
    }

    /// <summary>Видаляє прив'язку чернетки разом із її колонками. Право <c>Template.Edit</c>.</summary>
    /// <param name="id">Версія-чернетка.</param>
    /// <param name="code">Код прив'язки.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpDelete("permit-bindings/{code}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(int id, string code, CancellationToken ct)
    {
        await delete.HandleAsync(id, code, ct).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Перегляд аркуша очима користувача: обраний дозвіл, колонки з ролями, рядки з дозволу, попередження. Право
    /// <c>Template.View</c>; з <c>documentId</c> - ще й <c>Document.View</c>.
    /// </summary>
    /// <param name="id">Версія шаблону.</param>
    /// <param name="code">Код прив'язки.</param>
    /// <param name="documentId">Документ - джерело об'єкта, поля шапки й фактичних комірок (необов'язковий).</param>
    /// <param name="periodKey">Період документа <c>РРРРММ</c>; обов'язковий разом із <c>documentId</c>.</param>
    /// <param name="facilityName">Об'єкт вручну (<c>ByFacilityPeriodEnd</c>); перекриває об'єкт документа.</param>
    /// <param name="periodEnd">Останній день періоду вручну (<c>yyyy-MM-dd</c>); без нього - кінець періоду документа, а без документа - сьогодні.</param>
    /// <param name="permitEntryId">Дозвіл вручну (<c>FromHeaderLookup</c>); перекриває поле шапки документа.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Дозвіл не знайдено - це <c>200</c> із <c>permit.reasonIfNone</c> (<c>noFacility</c>, <c>noPermit</c>,
    /// <c>headerEmpty</c>, <c>headerUnknown</c>), а не помилка: саме це перегляд і має показати.
    /// </remarks>
    [HttpGet("permit-bindings/{code}/preview")]
    [ProducesResponseType<PermitBindingPreviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PermitBindingPreviewDto>> Preview(
        int id,
        string code,
        [FromQuery] long? documentId,
        [FromQuery] int? periodKey,
        [FromQuery] string? facilityName,
        [FromQuery] DateOnly? periodEnd,
        [FromQuery] long? permitEntryId,
        CancellationToken ct)
        => Ok(await preview
            .HandleAsync(id, code, new PermitPreviewQuery(documentId, periodKey, facilityName, periodEnd, permitEntryId), ct)
            .ConfigureAwait(false));
}
