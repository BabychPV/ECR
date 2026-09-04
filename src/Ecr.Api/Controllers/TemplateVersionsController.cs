using Ecr.Application.Templates;
using Ecr.Application.Templates.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Операції над версією шаблону: клон, публікація, diff, структура.</summary>
[ApiController]
[Route("api/v1/template-versions/{id:int}")]
[Authorize]
public sealed class TemplateVersionsController(
    CloneTemplateVersionHandler clone,
    PublishTemplateVersionHandler publish,
    DiffTemplateVersionsHandler diff,
    PatchPresentationHandler patchPresentation,
    GetTemplateStructureHandler structure,
    Ecr.Api.Auth.CurrentUser currentUser) : ControllerBase
{
    /// <summary>Клонує версію. Право <c>Template.Edit</c>.</summary>
    [HttpPost("clone")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public Task<IActionResult> Clone(int id, [FromBody] CloneVersionRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перевірити Template.Edit; делегувати clone.CloneAsync(id, request.NewVersion, userId, ct).");

    /// <summary>
    /// Публікує версію. Право <c>Template.Publish</c>.
    /// </summary>
    /// <remarks>
    /// Після публікації структура незмінна: тригер БД відхиляє структурний
    /// <c>UPDATE</c>, презентаційний пропускає. Цикл у графі формул — помилка
    /// **публікації** (<c>ECR-TMPL-4221</c>), а не рантайму (ФВ-9.4).
    /// </remarks>
    [HttpPost("publish")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> Publish(int id, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перевірити Template.Publish; делегувати publish.PublishAsync(id, userId, ct); 204.");

    /// <summary>Diff двох версій. Право <c>Template.View</c>.</summary>
    /// <remarks>Зіставлення за ідентичністю (<c>Code</c>, <c>RowKey</c>), не за позицією.</remarks>
    [HttpGet("diff/{otherId:int}")]
    [ProducesResponseType<TemplateDiffDto>(StatusCodes.Status200OK)]
    public Task<ActionResult<TemplateDiffDto>> Diff(int id, int otherId, CancellationToken ct)
        => throw new NotImplementedException("TODO: делегувати diff.HandleAsync(id, otherId, ct).");

    /// <summary>
    /// Патч презентаційного шару опублікованої версії. Право <c>Template.Edit</c>.
    /// </summary>
    /// <remarks>
    /// Єдина зміна, дозволена після публікації. Інкрементує
    /// <c>PresentationRevision</c> одним statement із <c>OUTPUT</c> (R-B7) —
    /// саме тому ключ кешу <c>v{id}:r{rev}</c> не потребує інвалідації.
    /// </remarks>
    [HttpPatch("presentation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<IActionResult> PatchPresentation(int id, [FromBody] object patch, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перевірити Template.Edit; делегувати patchPresentation.PatchAsync(id, patchJson, userId, ct); " +
            "повернути новий PresentationRevision.");

    /// <summary>Структура версії для клієнта. Право <c>Template.View</c>.</summary>
    /// <remarks>Кешується за ключем <c>v{id}:r{rev}</c> (ФВ-2.5); віддається з <c>ETag</c>.</remarks>
    [HttpGet("structure")]
    [ProducesResponseType<TemplateStructureDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public Task<ActionResult<TemplateStructureDto>> Structure(int id, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: делегувати structure.HandleAsync(id, ct); ETag = v{id}:r{rev}; " +
            "If-None-Match зі збігом → 304.");
}

/// <summary>Запит на клонування версії.</summary>
/// <param name="NewVersion">Номер нової версії.</param>
public sealed record CloneVersionRequest(string NewVersion);
