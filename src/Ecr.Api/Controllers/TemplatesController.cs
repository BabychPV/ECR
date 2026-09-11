using Ecr.Application.Common;
using Ecr.Application.Templates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Шаблони звітності: перелік, створення, версії.</summary>
/// <remarks>
/// Право перевіряється через <c>IAccessDecisionService</c>, а не атрибутом із
/// назвою ролі: перевірка ролі поза єдиною точкою рішення заборонена
/// архітектурним правилом 7 (`tz/03` §3.3).
/// </remarks>
[ApiController]
[Route("api/v1/templates")]
[Authorize]
public sealed class TemplatesController(
    ListTemplatesHandler list,
    CreateTemplateHandler create,
    ListTemplateVersionsHandler listVersions,
    CreateTemplateVersionHandler createVersion) : ControllerBase
{
    /// <summary>Перелік шаблонів. Право <c>Template.View</c>.</summary>
    /// <remarks>Сторінка, а не «все»: ендпоінтів, що повертають увесь набір, не існує.</remarks>
    [HttpGet]
    [ProducesResponseType<Ecr.Application.Common.PagedResult<Ecr.Application.Ports.TemplateSummary>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] int limit, [FromQuery] string? cursor, CancellationToken ct)
        // Право і межі сторінки перевіряє обробник: правило має діяти
        // незалежно від того, звідки його викликали.
        => Ok(await list.HandleAsync(new CursorRequest(limit == 0 ? 50 : limit, cursor), ct)
            .ConfigureAwait(false));

    /// <summary>Створює шаблон. Право <c>Template.Edit</c>.</summary>
    [HttpPost]
    [ProducesResponseType<Contracts.TemplateIdResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateTemplateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var templateId = await create.HandleAsync(request.Code, request.NameL10n, ct).ConfigureAwait(false);
        return Created($"/api/v1/templates/{templateId}", new Contracts.TemplateIdResponse(templateId));
    }

    /// <summary>Сторінка версій шаблону. Право <c>Template.View</c>.</summary>
    /// <remarks>
    /// ⛔ Q-225: раніше — `new CursorRequest()`, завжди дефолтний ліміт 50,
    /// без жодного способу передати `cursor` чи `limit` від клієнта. Версія
    /// шаблону за 50-ту була назавжди недосяжна через цей ендпоінт. Той
    /// самий патерн, що вже в <see cref="List"/> поруч.
    /// </remarks>
    [HttpGet("{id:int}/versions")]
    [ProducesResponseType<Ecr.Application.Common.PagedResult<Ecr.Application.Ports.TemplateVersionSummary>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListVersions(
        int id, [FromQuery] int limit, [FromQuery] string? cursor, CancellationToken ct)
        => Ok(await listVersions.HandleAsync(id, new CursorRequest(limit == 0 ? 50 : limit, cursor), ct)
            .ConfigureAwait(false));

    /// <summary>Створює версію шаблону. Право <c>Template.Edit</c>.</summary>
    /// <remarks><c>CloneFromVersionId</c> задає клонування замість порожньої версії.</remarks>
    [HttpPost("{id:int}/versions")]
    [ProducesResponseType<Contracts.VersionIdResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateVersion(
        int id, [FromBody] CreateTemplateVersionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var versionId = await createVersion
            .HandleAsync(id, request.VersionNumber, request.CloneFromVersionId, ct)
            .ConfigureAwait(false);

        return Created($"/api/v1/template-versions/{versionId}", new Contracts.VersionIdResponse(versionId));
    }
}

/// <summary>Запит на створення шаблону.</summary>
/// <param name="Code">Код шаблону, унікальний у системі.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
public sealed record CreateTemplateRequest(string Code, IReadOnlyDictionary<string, string> NameL10n);

/// <summary>Запит на створення версії шаблону.</summary>
/// <param name="VersionNumber">Номер нової версії.</param>
/// <param name="CloneFromVersionId">Версія-джерело; <c>null</c> — порожня версія.</param>
public sealed record CreateTemplateVersionRequest(string VersionNumber, int? CloneFromVersionId);
