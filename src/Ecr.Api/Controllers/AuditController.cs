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
    GetCellChangesHandler cellChanges,
    GetStructureChangesHandler structureChanges,
    GetSecurityEventsHandler securityEvents,
    ExportStructureChangesHandler exportStructure,
    GetWorkflowEventsHandler workflowEvents,
    ExportCellChangesHandler exportCells,
    ExportSecurityEventsHandler exportSecurity,
    ExportWorkflowEventsHandler exportWorkflow,
    IConfiguration configuration) : ControllerBase
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
    /// <param name="q">
    /// Пошук (UI-38): підрядок бізнес-ключа документа, ключа рядка чи коду колонки; до 100 знаків (довше
    /// обрізається), спецсимволи <c>LIKE</c> — буквальні. Лише по видимих читачу рядках.
    /// </param>
    /// <param name="periodKey">Звітний період змін (AN-98); без нього — усі. Чужі періоди відсіює сервер до сторінки.</param>
    /// <param name="sort">Порядок: <c>ChangedAtDesc</c> (типово, найновіші першими), <c>ChangedAtAsc</c>, <c>UserAsc</c>, <c>UserDesc</c>; курсор прив'язаний до порядку.</param>
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
        [FromQuery] int limit, [FromQuery] string? cursor, [FromQuery] string? q,
        [FromQuery] int? periodKey, [FromQuery] Ecr.Application.Ports.AuditSort sort,
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
            lateOnly,
            string.IsNullOrWhiteSpace(q) ? null : q,
            PeriodKey: periodKey,
            Sort: sort);

        // Вікно, його ширина, розмір сторінки й обидва рівні доступу
        // перевіряються в обробнику: правило «без вікна запит іде по всіх
        // партиціях» має діяти незалежно від того, звідки його викликали.
        return Ok(await cellChanges.HandleAsync(filter, page, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Підсумок журналу змін комірок за вікном (UI-38): усього, за сьогодні, імпортом, перерахунком.
    /// Ті самі права й вікно, що в <c>cells</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Лічильники — лише за тим, що читач бачить (R-11): приховані колонки, таблиці й аркуші не рахуються.
    /// ⚠ Лічильника пізніх правок немає навмисно: індексу по <c>IsLateEdit</c> немає, див. <c>ui-cell-changes-ext</c>.
    /// Вікно обмежене (92 дні; 396 для адреси однієї комірки) — підрахунок читає лише партиції вікна.
    /// </remarks>
    /// <param name="from">Початок вікна в UTC, включно.</param>
    /// <param name="to">Кінець вікна в UTC, виключно.</param>
    /// <param name="documentId">Документ; без нього — наскрізний журнал (<c>Security.ViewAudit</c>).</param>
    /// <param name="origin">Походження: <c>UserEdit</c>, <c>Import</c>, <c>Recalculation</c>, <c>Migration</c>.</param>
    /// <param name="lateOnly">Лише пізні правки.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("cells/summary")]
    [ProducesResponseType<Ecr.Application.Ports.CellChangeSummaryView>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CellsSummary(
        [FromQuery] DateTime from, [FromQuery] DateTime to,
        [FromQuery] long? documentId, [FromQuery] string? origin, [FromQuery] bool lateOnly,
        CancellationToken ct)
    {
        var filter = new Ecr.Application.Ports.CellChangeFilter(
            from, to, documentId,
            RowKey: null,
            ColumnDefId: null,
            ChangedByUserId: null,
            Origin: string.IsNullOrWhiteSpace(origin) ? null : origin,
            LateOnly: lateOnly);

        return Ok(await cellChanges.SummaryAsync(filter, ct).ConfigureAwait(false));
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
    /// <param name="sort">Порядок: <c>ChangedAtDesc</c> (типово, найновіші першими), <c>ChangedAtAsc</c>, <c>UserAsc</c>, <c>UserDesc</c>; курсор прив'язаний до порядку.</param>
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
        [FromQuery] int limit, [FromQuery] string? cursor, [FromQuery] Ecr.Application.Ports.AuditSort sort,
        CancellationToken ct)
    {
        var filter = new Ecr.Application.Ports.StructureChangeFilter(
            from, to,
            string.IsNullOrWhiteSpace(entityType) ? null : entityType,
            changedByUserId,
            sort);

        return Ok(await structureChanges
            .HandleAsync(filter, new CursorRequest(limit == 0 ? 50 : limit, cursor), ct)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Журнал подій безпеки (<c>aud.SecurityEvent</c>): зміни прав і ролей, відмови в доступі
    /// (<c>AccessDenied</c>, ФВ-5.24). Право <c>Security.ViewAudit</c>.
    /// </summary>
    /// <remarks>
    /// Вікно часу **обов'язкове** й обмежене згори, як у <c>cells</c> і <c>structure</c>:
    /// таблиця лежить на тій самій схемі партицій. Автор — <c>UserId</c>, не SID.
    /// </remarks>
    /// <param name="from">Початок вікна в UTC, включно.</param>
    /// <param name="to">Кінець вікна в UTC, виключно.</param>
    /// <param name="eventType">Тип події, напр. <c>AccessDenied</c>.</param>
    /// <param name="changedByUserId">Хто спричинив подію — <c>UserId</c>.</param>
    /// <param name="limit">Розмір сторінки; <c>0</c> — 50.</param>
    /// <param name="cursor">Курсор наступної сторінки.</param>
    /// <param name="sort">Порядок: <c>ChangedAtDesc</c> (типово, найновіші першими), <c>ChangedAtAsc</c>, <c>UserAsc</c>, <c>UserDesc</c>; курсор прив'язаний до порядку.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("security")]
    [ProducesResponseType<Ecr.Application.Common.PagedResult<Ecr.Application.Ports.SecurityEventView>>(
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Security(
        [FromQuery] DateTime from, [FromQuery] DateTime to,
        [FromQuery] string? eventType, [FromQuery] int? changedByUserId,
        [FromQuery] int limit, [FromQuery] string? cursor, [FromQuery] Ecr.Application.Ports.AuditSort sort,
        CancellationToken ct)
    {
        var filter = new Ecr.Application.Ports.SecurityEventFilter(
            from, to,
            string.IsNullOrWhiteSpace(eventType) ? null : eventType,
            changedByUserId,
            sort);

        return Ok(await securityEvents
            .HandleAsync(filter, new CursorRequest(limit == 0 ? 50 : limit, cursor), ct)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Експорт журналу структурних змін у CSV (<c>BE-16</c>). Право й фільтри — як у <c>structure</c>.
    /// </summary>
    /// <remarks>
    /// Синхронний потік, а не фонова задача: стеля рядків (<c>Audit:ExportMaxRows</c>,
    /// типово 100 000) тримає файл у межах одного запиту. Понад стелю — <c>422</c>.
    /// Кодування — UTF-8 із BOM, щоб Excel не читав кирилицю як cp1251.
    /// </remarks>
    /// <param name="from">Початок вікна в UTC, включно.</param>
    /// <param name="to">Кінець вікна в UTC, виключно.</param>
    /// <param name="entityType">Тип сутності.</param>
    /// <param name="changedByUserId">Автор зміни — <c>UserId</c>.</param>
    /// <param name="sort">Порядок: <c>ChangedAtDesc</c> (типово, найновіші першими), <c>ChangedAtAsc</c>, <c>UserAsc</c>, <c>UserDesc</c>; курсор прив'язаний до порядку.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("structure/export.csv")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(FileResult))]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ExportStructure(
        [FromQuery] DateTime from, [FromQuery] DateTime to,
        [FromQuery] string? entityType, [FromQuery] int? changedByUserId,
        [FromQuery] Ecr.Application.Ports.AuditSort sort,
        CancellationToken ct)
    {
        var filter = new Ecr.Application.Ports.StructureChangeFilter(
            from, to,
            string.IsNullOrWhiteSpace(entityType) ? null : entityType,
            changedByUserId,
            sort);

        var maxRows = configuration.GetValue("Audit:ExportMaxRows", ExportStructureChangesHandler.DefaultMaxRows);

        // Усі відмови (403/422) — тут, до першого байта відповіді.
        var export = await exportStructure.PrepareAsync(filter, maxRows, ct).ConfigureAwait(false);

        await StreamCsvAsync(
            export.FileName, ExportStructureChangesHandler.Columns, export.Rows, ExportStructureChangesHandler.ToCsv, ct)
            .ConfigureAwait(false);

        return new EmptyResult();
    }

    /// <summary>
    /// Експорт журналу змін комірок у CSV (гаряче + архів). Право й фільтри — як у <c>cells</c>; порядок — <c>sort</c>.
    /// </summary>
    /// <remarks>
    /// Потік сторінками, стеля <c>Audit:ExportMaxRows</c> (типово 100 000); понад неї — <c>422 auditExportTooLarge</c>.
    /// UTF-8 з BOM; час — UTC ISO 8601; значення, що починаються з <c>= + - @</c>, нейтралізуються. Експорт сам
    /// пишеться подією безпеки <c>AuditCellsExported</c>.
    /// </remarks>
    [HttpGet("cells/export.csv")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(FileResult))]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ExportCells(
        [FromQuery] DateTime from, [FromQuery] DateTime to,
        [FromQuery] long? documentId, [FromQuery] string? rowKey, [FromQuery] int? columnDefId,
        [FromQuery] int? author, [FromQuery] string? origin, [FromQuery] bool lateOnly,
        [FromQuery] string? q, [FromQuery] int? periodKey, [FromQuery] Ecr.Application.Ports.AuditSort sort,
        CancellationToken ct)
    {
        var filter = new Ecr.Application.Ports.CellChangeFilter(
            from, to, documentId,
            string.IsNullOrWhiteSpace(rowKey) ? null : rowKey,
            columnDefId,
            author,
            string.IsNullOrWhiteSpace(origin) ? null : origin,
            lateOnly,
            string.IsNullOrWhiteSpace(q) ? null : q,
            PeriodKey: periodKey,
            Sort: sort);

        var export = await exportCells.PrepareAsync(filter, MaxRows(), ct).ConfigureAwait(false);

        await StreamCsvAsync(
            export.FileName, ExportCellChangesHandler.Columns, export.Rows, ExportCellChangesHandler.ToCsv, ct)
            .ConfigureAwait(false);

        return new EmptyResult();
    }

    private int MaxRows() => configuration.GetValue("Audit:ExportMaxRows", AuditExportRunner.DefaultMaxRows);

    /// <summary>Віддає CSV потоком: UTF-8 з BOM, заголовок, рядки по одному (без повної матеріалізації).</summary>
    private async Task StreamCsvAsync<T>(
        string fileName, string[] columns, IAsyncEnumerable<T> rows, Func<T, string> toCsv, CancellationToken ct)
    {
        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
        {
            FileName = fileName,
        }.ToString();

        await using var writer = new StreamWriter(Response.Body, new System.Text.UTF8Encoding(true), 64 * 1024);
        await writer.WriteAsync(Ecr.Application.Common.CsvFormat.Row(columns)).ConfigureAwait(false);

        await foreach (var row in rows.WithCancellation(ct).ConfigureAwait(false))
        {
            await writer.WriteAsync(toCsv(row)).ConfigureAwait(false);
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Експорт журналу подій безпеки у CSV. Право й фільтри — як у <c>security</c>; порядок — <c>sort</c>.
    /// </summary>
    /// <remarks>Ті самі стеля, кодування й захист, що в <c>cells/export.csv</c>; подія безпеки <c>AuditSecurityExported</c>.</remarks>
    [HttpGet("security/export.csv")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(FileResult))]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ExportSecurity(
        [FromQuery] DateTime from, [FromQuery] DateTime to,
        [FromQuery] string? eventType, [FromQuery] int? changedByUserId,
        [FromQuery] Ecr.Application.Ports.AuditSort sort,
        CancellationToken ct)
    {
        var filter = new Ecr.Application.Ports.SecurityEventFilter(
            from, to,
            string.IsNullOrWhiteSpace(eventType) ? null : eventType,
            changedByUserId,
            sort);

        var export = await exportSecurity.PrepareAsync(filter, MaxRows(), ct).ConfigureAwait(false);

        await StreamCsvAsync(
            export.FileName, ExportSecurityEventsHandler.Columns, export.Rows, ExportSecurityEventsHandler.ToCsv, ct)
            .ConfigureAwait(false);

        return new EmptyResult();
    }

    /// <summary>
    /// Журнал подій погодження аркушів (<c>wf.ApprovalEvent</c>): подання, затвердження, відхилення, повернення,
    /// відкликання — з коментарем. Право <c>Security.ViewAudit</c>; вікно обов'язкове й обмежене, як у <c>cells</c>.
    /// </summary>
    /// <param name="from">Початок вікна в UTC, включно.</param>
    /// <param name="to">Кінець вікна в UTC, виключно.</param>
    /// <param name="action">Дія: <c>Submit</c>, <c>Approve</c>, <c>ApproveStep</c>, <c>Reject</c>, <c>Reopen</c>, <c>Recall</c>.</param>
    /// <param name="changedByUserId">Автор дії — <c>UserId</c>.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="limit">Розмір сторінки; <c>0</c> — 50.</param>
    /// <param name="cursor">Курсор наступної сторінки (прив'язаний до <paramref name="sort"/>).</param>
    /// <param name="sort">Порядок; типово найновіші першими.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("workflow")]
    [ProducesResponseType<Ecr.Application.Common.PagedResult<Ecr.Application.Ports.WorkflowEventView>>(
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Workflow(
        [FromQuery] DateTime from, [FromQuery] DateTime to,
        [FromQuery] string? action, [FromQuery] int? changedByUserId, [FromQuery] long? documentId,
        [FromQuery] int limit, [FromQuery] string? cursor, [FromQuery] Ecr.Application.Ports.AuditSort sort,
        CancellationToken ct)
    {
        var filter = new Ecr.Application.Ports.WorkflowEventFilter(
            from, to,
            string.IsNullOrWhiteSpace(action) ? null : action,
            changedByUserId,
            documentId,
            sort);

        return Ok(await workflowEvents
            .HandleAsync(filter, new CursorRequest(limit == 0 ? 50 : limit, cursor), ct)
            .ConfigureAwait(false));
    }

    /// <summary>Експорт журналу подій погодження у CSV; ті самі правила, що в <c>cells/export.csv</c>.</summary>
    [HttpGet("workflow/export.csv")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(FileResult))]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ExportWorkflow(
        [FromQuery] DateTime from, [FromQuery] DateTime to,
        [FromQuery] string? action, [FromQuery] int? changedByUserId, [FromQuery] long? documentId,
        [FromQuery] Ecr.Application.Ports.AuditSort sort,
        CancellationToken ct)
    {
        var filter = new Ecr.Application.Ports.WorkflowEventFilter(
            from, to,
            string.IsNullOrWhiteSpace(action) ? null : action,
            changedByUserId,
            documentId,
            sort);

        var export = await exportWorkflow.PrepareAsync(filter, MaxRows(), ct).ConfigureAwait(false);

        await StreamCsvAsync(
            export.FileName, ExportWorkflowEventsHandler.Columns, export.Rows, ExportWorkflowEventsHandler.ToCsv, ct)
            .ConfigureAwait(false);

        return new EmptyResult();
    }
}
