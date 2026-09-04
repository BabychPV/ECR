using System.Text.Json;
using Ecr.Application.Localization;
using Ecr.Application.Templates;
using Ecr.Application.Templates.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

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
    public async Task<IActionResult> Clone(
        int id, [FromBody] CloneVersionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var versionId = await clone
            .CloneAsync(id, request.NewVersion, UserId, ct)
            .ConfigureAwait(false);

        return Created($"/api/v1/template-versions/{versionId}", new { versionId });
    }

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
    public async Task<IActionResult> Publish(int id, CancellationToken ct)
    {
        // Цикл у графі формул, нерозв'язані посилання і несумісні одиниці —
        // помилки ПУБЛІКАЦІЇ, і кидає їх обробник. Контролер лише передає.
        await publish.PublishAsync(id, UserId, ct).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Diff двох версій. Право <c>Template.View</c>.</summary>
    /// <remarks>Зіставлення за ідентичністю (<c>Code</c>, <c>RowKey</c>), не за позицією.</remarks>
    [HttpGet("diff/{otherId:int}")]
    [ProducesResponseType<TemplateDiffDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TemplateDiffDto>> Diff(int id, int otherId, CancellationToken ct)
        => await diff.HandleAsync(id, otherId, ct).ConfigureAwait(false);

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
    public async Task<IActionResult> PatchPresentation(
        int id, [FromBody] JsonElement patch, CancellationToken ct)
    {
        var revision = await patchPresentation
            .PatchAsync(id, patch.GetRawText(), UserId, ct)
            .ConfigureAwait(false);

        // Нова ревізія — це новий ключ кешу v{id}:r{rev}. Клієнт має її
        // отримати, інакше він і далі питатиме структуру за старим ключем.
        return Ok(new { presentationRevision = revision });
    }

    /// <summary>Структура версії для клієнта. Право <c>Template.View</c>.</summary>
    /// <remarks>Кешується за ключем <c>v{id}:r{rev}</c> (ФВ-2.5); віддається з <c>ETag</c>.</remarks>
    [HttpGet("structure")]
    [ProducesResponseType<TemplateStructureDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public async Task<ActionResult<TemplateStructureDto>> Structure(int id, CancellationToken ct)
    {
        var dto = await structure.HandleAsync(id, ct).ConfigureAwait(false);

        // ETag = v{id}:r{rev} — той самий ключ, що й у кеші метаданих (ФВ-2.5).
        // Одне значення на дві ролі: інвалідація не потрібна ні тут, ні там.
        var etag = $"\"v{dto.TemplateVersionId}:r{dto.PresentationRevision}\"";
        Response.Headers[HeaderNames.ETag] = etag;

        if (UiStringResolver.IsNotModified(Request.Headers[HeaderNames.IfNoneMatch], etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return dto;
    }

    /// <summary>Поточний користувач; анонім сюди не доходить через [Authorize].</summary>
    private int UserId => currentUser.UserId
        ?? throw new Application.Errors.AccessDeniedException(
            Errors.ErrorCodes.Unauthorized, "Сесія не містить користувача.");
}

/// <summary>Запит на клонування версії.</summary>
/// <param name="NewVersion">Номер нової версії.</param>
public sealed record CloneVersionRequest(string NewVersion);
