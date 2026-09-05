using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Domain.ValueObjects;
using Ecr.Application.Workflow;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Документи: перелік, створення, читання, валідація, робочий процес.</summary>
/// <remarks>
/// Комірки і рядки — в окремому <see cref="CellsController"/>: у них інший
/// профіль навантаження і власний бюджет часу.
/// </remarks>
[ApiController]
[Route("api/v1/documents")]
[Authorize]
public sealed class DocumentsController(
    ListDocumentsHandler listDocuments,
    GetDocumentHandler getDocument,
    CreateDocumentHandler create,
    ValidateDocumentHandler validate,
    SubmitSheetHandler submit,
    ApproveSheetHandler approve,
    ReopenDocumentHandler reopen,
    RecalculateDocumentHandler recalculate,
    GetDocumentTablesHandler tables,
    ExportDocumentHandler export,
    DownloadExportHandler downloadExport,
    PreviewImportHandler previewImport,
    ApplyImportHandler applyImport) : ControllerBase
{
    /// <summary>Перелік документів. Право <c>Document.View</c>.</summary>
    /// <remarks>
    /// Зведений стан документа <b>рахується запитом</b> із <c>wf.ApprovalState</c>,
    /// а не зберігається полем (<c>D-93</c>): скалярний статус був би другим
    /// джерелом істини і рано чи пізно показав би <c>Approved</c> на документі
    /// з половиною аркушів у <c>Draft</c>.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<Ecr.Application.Common.PagedResult<Ecr.Application.Ports.DocumentSummary>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] int limit,
        [FromQuery] string? cursor,
        [FromQuery] int? projectId,
        [FromQuery] int? periodKey,
        CancellationToken ct)
    {
        var page = new CursorRequest(limit == 0 ? 50 : limit, cursor);
        if (!page.IsValid)
        {
            return BadRequest(new { error = $"limit поза межами 1..{CursorRequest.MaxLimit}" });
        }

        // ⛔ Зведений стан рахується запитом по wf.ApprovalState і лише коли
        // вказано період: без періоду «стан документа» не визначений — аркуші
        // за різні періоди бувають у різних станах одночасно (D-93).
        return Ok(await listDocuments
            .HandleAsync(projectId, periodKey, page, ct)
            .ConfigureAwait(false));
    }

    /// <summary>Створює документ. Право <c>Document.Create</c>.</summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateDocumentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var documentId = await create
            .HandleAsync(request.ProjectId, request.TemplateVersionId, request.SheetDefIds, ct)
            .ConfigureAwait(false);

        return Created($"/api/v1/documents/{documentId}", new { documentId });
    }

    /// <summary>Документ і його аркуші. Право <c>Document.View</c>.</summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType<Ecr.Application.Ports.DocumentSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(long id, [FromQuery] int? periodKey, CancellationToken ct)
    {
        var document = await getDocument.HandleAsync(id, periodKey, ct).ConfigureAwait(false);

        return document is null
            ? NotFound(new { errorCode = "ECR-DOC-0404" })
            : Ok(document);
    }

    /// <summary>Валідація документа. Право <c>Document.View</c>.</summary>
    /// <remarks>
    /// Повертає повідомлення трьох рівнів. Запис блокує <b>лише</b> комірковий
    /// <c>Error</c> (R-B3); рядковий і табличний повертаються у відповіді й
    /// запису не заважають.
    /// </remarks>
    [HttpPost("{id:long}/validate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Validate(long id, [FromQuery] int periodKey, CancellationToken ct)
    {
        var messages = await validate.HandleAsync(id, new PeriodKey(periodKey), ct).ConfigureAwait(false);

        // Повертаються ВСІ рівні. Рішення «чи можна подавати» ухвалює клієнт
        // за наявністю Error, а не сервер за кодом відповіді: 200 тут означає
        // «перевірку виконано», а не «зауважень немає».
        return Ok(new
        {
            documentId = id,
            periodKey,
            messages = messages.Select(m => new
            {
                severity = m.Severity.ToString(),
                ruleCode = m.RuleCode,
                message = m.Message,
                rowKey = m.RowKey,
                columnCode = m.ColumnCode,
                blocksSave = m.BlocksSave,
            }),
        });
    }

    /// <summary>Перерахунок документа. Право <c>Calculation.Recalculate</c>.</summary>
    /// <remarks>Довга операція — у фон із прогресом; повертає <c>jobId</c>, а не результат.</remarks>
    [HttpPost("{id:long}/recalculate")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Recalculate(long id, [FromQuery] int periodKey, CancellationToken ct)
    {
        // Контролер лише делегує: рішення про чергу, payload і умови — у
        // прикладному шарі, інакше те саме правило почало б жити у двох місцях.
        var jobId = await recalculate.HandleAsync(id, new PeriodKey(periodKey), ct).ConfigureAwait(false);

        return Accepted(new { jobId, documentId = id, periodKey });
    }

    /// <summary>Подання аркуша на погодження.</summary>
    [HttpPost("{id:long}/submit")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Submit(
        long id, [FromBody] SheetWorkflowRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await submit.HandleAsync(id, request.SheetDefId, request.PeriodKey, ct).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Погодження або відхилення аркуша.</summary>
    [HttpPost("{id:long}/approve")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Approve(
        long id, [FromBody] ApproveSheetRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await approve
            .HandleAsync(id, request.SheetDefId, request.PeriodKey, request.Approved, request.Reason, ct)
            .ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Відкриває поданий документ. Право <c>Document.Reopen</c>.
    /// </summary>
    /// <remarks>
    /// Відкриття документа при <b>закритому періоді</b> відхиляється
    /// (<c>ECR-PRD-4223</c>): спершу відкривають період, і це інше право
    /// (<c>D-67</c>).
    /// </remarks>
    [HttpPost("{id:long}/reopen")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Reopen(
        long id, [FromBody] ReopenDocumentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await reopen
            .HandleAsync(id, request.SheetDefId, request.PeriodKey, request.Reason, ct)
            .ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Таблиці документа за період. Право <c>Document.View</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Grid читає і пише за <c>TableInstanceId</c>, а екземпляр існує <b>на
    /// кожен період окремо</b> (R-A6). Ані <c>DocumentSummary</c>, ані
    /// структура версії шаблону його не несуть: перша описує документ,
    /// друга — опис таблиць, а не їхні екземпляри (`A7-05`).
    /// </remarks>
    [HttpGet("{id:long}/tables")]
    [ProducesResponseType<IReadOnlyList<DocumentTableDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Tables(long id, [FromQuery] int periodKey, CancellationToken ct)
        => Ok(await tables.HandleAsync(id, periodKey, ct).ConfigureAwait(false));

    /// <summary>Експорт у <c>.xlsx</c>. Право <c>Document.Export</c>.</summary>
    [HttpPost("{id:long}/export")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Export(long id, [FromBody] ExportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ⚠ 202 з jobId, а не файл у відповіді. Бюджет експорту — 10 с p95, і
        // це середнє: книга на 500×60×12 будується довше за будь-який
        // розумний таймаут проксі.
        var jobId = await export
            .HandleAsync(
                id,
                new Ecr.Application.Ports.ExcelExportOptions(
                    request.IncludeFormulas, request.IncludeStyles, request.Language, request.PeriodKey),
                ct)
            .ConfigureAwait(false);

        return Accepted(new { jobId });
    }

    /// <summary>
    /// Віддає побудовану книгу. Право <c>Document.Export</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Ідентифікатор експорту приходить у повідомленні прогресу задачі:
    /// саме тому побудова повертає <c>202</c> з <c>jobId</c>, а не файл.
    /// Книга живе годину — довше тримати немає сенсу, це знімок даних на
    /// момент побудови.
    /// </remarks>
    [HttpGet("{id:long}/export/{exportId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadExport(long id, string exportId, CancellationToken ct)
    {
        var content = await downloadExport.HandleAsync(exportId, ct).ConfigureAwait(false);

        return File(
            content,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"document-{id}-{exportId}.xlsx");
    }

    /// <summary>Попередній перегляд імпорту. Право <c>Document.Import</c>.</summary>
    /// <remarks>Імпорт **завжди** через перегляд diff (ФВ-4.3): застосування — окремим викликом.</remarks>
    [HttpPost("{id:long}/import/preview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ImportPreview(long id, IFormFile file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);

        // ⛔ Перегляд НЕ застосовує нічого. Це не проміжний крок майстра, а
        // сам механізм захисту: імпорт без перегляду непомітно перезаписує
        // чужу роботу (ФВ-4.3).
        await using var stream = file.OpenReadStream();

        return Ok(await previewImport.HandleAsync(id, stream, ct).ConfigureAwait(false));
    }

    /// <summary>Застосування раніше переглянутого імпорту. Право <c>Document.Import</c>.</summary>
    [HttpPost("{id:long}/import/apply")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ImportApply(
        long id, [FromBody] ImportApplyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Конфлікт версій рядків підіймається зі звичайного шляху запису як
        // ECR-CELL-0409 і перетворюється на 409 середовищем обробки помилок:
        // окрема перевірка тут була б другою, яка вміє розійтися з першою.
        return Ok(await applyImport
            .HandleAsync(id, request.PreviewToken, ct)
            .ConfigureAwait(false));
    }
}

/// <summary>Запит на створення документа.</summary>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="TemplateVersionId">Опублікована версія шаблону.</param>
/// <param name="SheetDefIds">Аркуші, які входять у документ.</param>
public sealed record CreateDocumentRequest(int ProjectId, int TemplateVersionId, IReadOnlyList<int> SheetDefIds);

/// <summary>Аркуш × період — адреса операції робочого процесу.</summary>
/// <param name="SheetDefId">Аркуш.</param>
/// <param name="PeriodKey">Період.</param>
public sealed record SheetWorkflowRequest(int SheetDefId, int PeriodKey);

/// <summary>Запит на погодження аркуша.</summary>
/// <param name="SheetDefId">Аркуш.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="Approved"><c>true</c> — погодити, <c>false</c> — відхилити.</param>
/// <param name="Reason">Причина; обов'язкова при відхиленні.</param>
public sealed record ApproveSheetRequest(int SheetDefId, int PeriodKey, bool Approved, string? Reason);

/// <summary>Запит на відкриття поданого документа.</summary>
/// <param name="SheetDefId">Аркуш.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="Reason">Причина; обов'язкова.</param>
public sealed record ReopenDocumentRequest(int SheetDefId, int PeriodKey, string Reason);

/// <summary>Запит на експорт.</summary>
/// <param name="IncludeFormulas">Транслювати вирази в Excel-синтаксис (ФВ-4.2).</param>
/// <param name="IncludeStyles">Переносити стилі шаблону.</param>
/// <param name="Language">Мова заголовків.</param>
/// <param name="PeriodKey">Період вивантаження (R-A6).</param>
/// <remarks>
/// ⚠ Період обовʼязковий: подання, затвердження і перерахунок працюють за
/// період, і «експорт усього документа» означав би книгу, у якій неможливо
/// сказати, який стовпчик за який місяць.
/// </remarks>
public sealed record ExportRequest(
    bool IncludeFormulas, bool IncludeStyles, string Language, int PeriodKey);

/// <summary>Запит на застосування імпорту.</summary>
/// <param name="PreviewToken">Токен раніше побудованого diff.</param>
public sealed record ImportApplyRequest(string PreviewToken);
