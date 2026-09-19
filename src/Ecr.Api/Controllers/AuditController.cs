using Ecr.Application.Audit;
using Ecr.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Читання аудиту.</summary>
/// <remarks>
/// Аудит **тільки читається**: записів на зміну чи видалення тут немає і не
/// буде — журнал, який можна відредагувати, не є доказом.
/// </remarks>
[ApiController]
[Route("api/v1/audit")]
[Authorize]
public sealed class AuditController(
    GetCellChangesHandler cellChanges, GetStructureChangesHandler structureChanges) : ControllerBase
{
    /// <summary>
    /// Історія змін комірок. Право <c>Security.ViewAudit</c> — або
    /// <c>Document.View</c>, коли задано повну адресу однієї комірки.
    /// </summary>
    /// <remarks>
    /// Таблиця партиційована за <c>ChangedAt</c>, а не за періодом: місяць
    /// зміни і звітний період — різні осі (зміна за січень може статися в
    /// березні). Тому запит **обов'язково** обмежений вікном часу — інакше він
    /// піде по всіх партиціях.
    ///
    /// ⚠ Одна дія, ДВА рівні доступу (D15-16). <c>documentId + rowKey +
    /// columnDefId</c> разом — це історія ОДНІЄЇ комірки, і її бачить той, хто
    /// бачить документ (<c>Document.View</c> плюс грант на проєкт). Будь-який
    /// ширший запит — загальний журнал, і для нього поріг лишається
    /// <c>Security.ViewAudit</c>. Розділяти це на два маршрути означало б
    /// дублювати вікно, курсор і всі п'ять фільтрів заради різниці, яка
    /// виражається самим фільтром.
    ///
    /// ⚠ <c>rowKey</c>/<c>columnDefId</c> без <c>documentId</c> —
    /// <c>422 ECR-REQ-0422</c>: ключ рядка унікальний у межах екземпляра
    /// таблиці, а не системи, тож без документа він не адресує нічого.
    /// Родина <c>REQ</c>, а не <c>AUD</c>/<c>CELL</c>: суб'єкт відмови —
    /// параметр запиту, і саме так тут уже відмовляють обидві перевірки вікна.
    /// </remarks>
    /// <param name="from">Початок вікна в UTC, включно.</param>
    /// <param name="to">Кінець вікна в UTC, виключно.</param>
    /// <param name="documentId">Документ; без нього — наскрізний журнал.</param>
    /// <param name="rowKey">Ключ рядка; лише разом із <paramref name="documentId"/>.</param>
    /// <param name="columnDefId">Колонка; лише разом із <paramref name="documentId"/>.</param>
    /// <param name="author">Автор зміни — <c>UserId</c>, не SID.</param>
    /// <param name="origin">Походження: <c>UserEdit</c>, <c>Import</c>, <c>Recalculation</c>, <c>Migration</c>.</param>
    /// <param name="lateOnly">Лише пізні правки (<c>Grace</c>/після <c>Reopen</c>).</param>
    /// <param name="limit">Розмір сторінки; <c>0</c> — 50.</param>
    /// <param name="cursor">Курсор наступної сторінки.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("cells")]
    [ProducesResponseType<Ecr.Application.Common.PagedResult<Ecr.Application.Ports.CellChangeView>>(
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]

    // ⚠ 422, а не лише 400. Обидві перевірки вікна і перевірка адреси комірки
    // кидають `BusinessRuleException` з `ECR-REQ-0422`, і це вже сьогоднішня
    // поведінка — контракт її просто не оголошував. 400 лишається: він
    // приходить зі ЗВ'ЯЗУВАННЯ (нерозбірлива дата в query), ще до обробника.
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Cells(
        [FromQuery] DateTime from, [FromQuery] DateTime to,
        [FromQuery] long? documentId, [FromQuery] string? rowKey, [FromQuery] int? columnDefId,
        [FromQuery] int? author, [FromQuery] string? origin, [FromQuery] bool lateOnly,
        [FromQuery] int limit, [FromQuery] string? cursor,
        CancellationToken ct)
    {
        var page = new CursorRequest(limit == 0 ? 50 : limit, cursor);

        // ⚠ Порожній рядок у query (`?rowKey=`) — це НЕ фільтр: браузер
        // надсилає його, коли поле очистили. Без цього зведення очищене поле
        // шукало б рядок із порожнім ключем і давало б порожню видачу там, де
        // людина щойно зняла фільтр.
        var filter = new Ecr.Application.Ports.CellChangeFilter(
            from, to, documentId,
            string.IsNullOrWhiteSpace(rowKey) ? null : rowKey,
            columnDefId,
            author,
            string.IsNullOrWhiteSpace(origin) ? null : origin,
            lateOnly);

        // Вікно, його ширина, розмір сторінки й обидва рівні доступу
        // перевіряються в обробнику: правило «без вікна запит іде по всіх
        // партиціях» має діяти незалежно від того, звідки його викликали.
        return Ok(await cellChanges.HandleAsync(filter, page, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Загальний журнал структурних змін (<c>BE-16</c>). Право <c>Security.ViewAudit</c>.
    /// </summary>
    /// <remarks>
    /// Вікно часу **обов'язкове** й обмежене згори, як у <c>cells</c>:
    /// <c>aud.StructureChange</c> лежить на тій самій схемі партицій. Запит без
    /// вікна — <c>422 ECR-REQ-0422</c>, а не «весь журнал».
    /// </remarks>
    /// <param name="from">Початок вікна в UTC, включно.</param>
    /// <param name="to">Кінець вікна в UTC, виключно.</param>
    /// <param name="entityType">Тип сутності, напр. <c>cfg.RegistryDef</c>.</param>
    /// <param name="changedByUserId">Автор зміни — <c>UserId</c>, не SID.</param>
    /// <param name="limit">Розмір сторінки; <c>0</c> — 50.</param>
    /// <param name="cursor">Курсор наступної сторінки.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("structure")]
    [ProducesResponseType<Ecr.Application.Common.PagedResult<Ecr.Application.Ports.StructureChangeView>>(
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Structure(
        [FromQuery] DateTime from, [FromQuery] DateTime to,
        [FromQuery] string? entityType, [FromQuery] int? changedByUserId,
        [FromQuery] int limit, [FromQuery] string? cursor,
        CancellationToken ct)
    {
        var filter = new Ecr.Application.Ports.StructureChangeFilter(
            from, to,
            string.IsNullOrWhiteSpace(entityType) ? null : entityType,
            changedByUserId);

        return Ok(await structureChanges
            .HandleAsync(filter, new CursorRequest(limit == 0 ? 50 : limit, cursor), ct)
            .ConfigureAwait(false));
    }
}
