using Ecr.Application.Documents;
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
    CreateDocumentHandler create,
    ValidateDocumentHandler validate,
    SubmitSheetHandler submit,
    ApproveSheetHandler approve,
    ReopenDocumentHandler reopen) : ControllerBase
{
    /// <summary>Перелік документів. Право <c>Document.View</c>.</summary>
    /// <remarks>
    /// Зведений стан документа <b>рахується запитом</b> із <c>wf.ApprovalState</c>,
    /// а не зберігається полем (<c>D-93</c>): скалярний статус був би другим
    /// джерелом істини і рано чи пізно показав би <c>Approved</c> на документі
    /// з половиною аркушів у <c>Draft</c>.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<IActionResult> List([FromQuery] int limit, [FromQuery] string? cursor, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: курсорна пагінація; зведений стан рахувати запитом по wf.ApprovalState; " +
            "фільтрувати за AccessProfile.");

    /// <summary>Створює документ. Право <c>Document.Create</c>.</summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public Task<IActionResult> Create([FromBody] CreateDocumentRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: делегувати create.HandleAsync(request.ProjectId, request.TemplateVersionId, " +
            "request.SheetDefIds, ct); 201 із Location.");

    /// <summary>Документ і його аркуші. Право <c>Document.View</c>.</summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Get(long id, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: шапка документа, перелік аркушів і їхній стан за (DocumentId, SheetDefId, PeriodKey).");

    /// <summary>Валідація документа. Право <c>Document.View</c>.</summary>
    /// <remarks>
    /// Повертає повідомлення трьох рівнів. Запис блокує <b>лише</b> комірковий
    /// <c>Error</c> (R-B3); рядковий і табличний повертаються у відповіді й
    /// запису не заважають.
    /// </remarks>
    [HttpPost("{id:long}/validate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<IActionResult> Validate(long id, [FromQuery] int periodKey, CancellationToken ct)
        => throw new NotImplementedException("TODO: делегувати validate.HandleAsync(id, periodKey, ct).");

    /// <summary>Перерахунок документа. Право <c>Calculation.Recalculate</c>.</summary>
    /// <remarks>Довга операція — у фон із прогресом; повертає <c>jobId</c>, а не результат.</remarks>
    [HttpPost("{id:long}/recalculate")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public Task<IActionResult> Recalculate(long id, [FromQuery] int periodKey, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перевірити Calculation.Recalculate; поставити задачу через IBackgroundJobScheduler; " +
            "202 із jobId. ⚠ ЗАКРИТІ періоди автоматично не перераховуються ніколи (ФВ-9.7).");

    /// <summary>Подання аркуша на погодження.</summary>
    [HttpPost("{id:long}/submit")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> Submit(long id, [FromBody] SheetWorkflowRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: делегувати submit.HandleAsync(id, request.SheetDefId, request.PeriodKey, ct).");

    /// <summary>Погодження або відхилення аркуша.</summary>
    [HttpPost("{id:long}/approve")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> Approve(long id, [FromBody] ApproveSheetRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: делегувати approve.HandleAsync(id, request.SheetDefId, request.PeriodKey, " +
            "request.Approved, request.Reason, ct).");

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
    public Task<IActionResult> Reopen(long id, [FromBody] ReopenDocumentRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: делегувати reopen.HandleAsync(id, request.SheetDefId, request.PeriodKey, request.Reason, ct).");

    /// <summary>Експорт у <c>.xlsx</c>. Право <c>Document.Export</c>.</summary>
    [HttpPost("{id:long}/export")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public Task<IActionResult> Export(long id, [FromBody] ExportRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: у фон через IExcelExporter (бюджет 10 с p95); 202 із jobId.");

    /// <summary>Попередній перегляд імпорту. Право <c>Document.Import</c>.</summary>
    /// <remarks>Імпорт **завжди** через перегляд diff (ФВ-4.3): застосування — окремим викликом.</remarks>
    [HttpPost("{id:long}/import/preview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<IActionResult> ImportPreview(long id, IFormFile file, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: делегувати IExcelImporter.PreviewAsync; повернути ImportPreview із previewToken.");

    /// <summary>Застосування раніше переглянутого імпорту. Право <c>Document.Import</c>.</summary>
    [HttpPost("{id:long}/import/apply")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> ImportApply(long id, [FromBody] ImportApplyRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: делегувати IExcelImporter.ApplyAsync; версії рядків перевіряються ЗАНОВО — " +
            "між переглядом і застосуванням могла статися чужа правка.");
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
public sealed record ExportRequest(bool IncludeFormulas, bool IncludeStyles, string Language);

/// <summary>Запит на застосування імпорту.</summary>
/// <param name="PreviewToken">Токен раніше побудованого diff.</param>
public sealed record ImportApplyRequest(string PreviewToken);
