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
public sealed class TemplatesController(CreateTemplateVersionHandler createVersion) : ControllerBase
{
    /// <summary>Перелік шаблонів. Право <c>Template.View</c>.</summary>
    /// <remarks>Сторінка, а не «все»: ендпоінтів, що повертають увесь набір, не існує.</remarks>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<IActionResult> List([FromQuery] int limit, [FromQuery] string? cursor, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перевірити Template.View через IAccessDecisionService; курсорна пагінація " +
            "через CursorRequest (limit ≤ 500, інакше 400); повернути PagedResult<TemplateDto>.");

    /// <summary>Створює шаблон. Право <c>Template.Edit</c>.</summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public Task<IActionResult> Create([FromBody] CreateTemplateRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перевірити Template.Edit; створити cfg.Template; 201 із Location на /api/v1/templates/{id}.");

    /// <summary>Версії шаблону. Право <c>Template.View</c>.</summary>
    [HttpGet("{id:int}/versions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<IActionResult> ListVersions(int id, CancellationToken ct)
        => throw new NotImplementedException("TODO: перелік версій із їхнім Status і PresentationRevision.");

    /// <summary>Створює версію шаблону. Право <c>Template.Edit</c>.</summary>
    /// <remarks><c>CloneFromVersionId</c> задає клонування замість порожньої версії.</remarks>
    [HttpPost("{id:int}/versions")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public Task<IActionResult> CreateVersion(int id, [FromBody] CreateTemplateVersionRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перевірити Template.Edit; делегувати createVersion.HandleAsync(id, request.VersionNumber, " +
            "request.CloneFromVersionId, ct); 201 із Location.");
}

/// <summary>Запит на створення шаблону.</summary>
/// <param name="Code">Код шаблону, унікальний у системі.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
public sealed record CreateTemplateRequest(string Code, IReadOnlyDictionary<string, string> NameL10n);

/// <summary>Запит на створення версії шаблону.</summary>
/// <param name="VersionNumber">Номер нової версії.</param>
/// <param name="CloneFromVersionId">Версія-джерело; <c>null</c> — порожня версія.</param>
public sealed record CreateTemplateVersionRequest(string VersionNumber, int? CloneFromVersionId);
