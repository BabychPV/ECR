using Ecr.Application.Periods;
using Ecr.Application.Projects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Проєкти: перелік, створення, клон, поточний період, календар.</summary>
[ApiController]
[Route("api/v1/projects")]
[Authorize]
public sealed class ProjectsController(
    BuildPeriodCalendarHandler buildCalendar,
    GetPeriodCalendarHandler getCalendar,
    SetCurrentPeriodHandler setCurrentPeriod,
    CloneProjectHandler cloneProject) : ControllerBase
{
    /// <summary>Перелік проєктів. Право <c>Document.View</c>.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<IActionResult> List([FromQuery] int limit, [FromQuery] string? cursor, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: показувати лише проєкти, видимі за AccessProfile; курсорна пагінація " +
            "через CursorRequest (limit більший за MaxLimit → 400).");

    /// <summary>Створює проєкт. Право <c>Project.Manage</c>.</summary>
    /// <remarks>
    /// <c>TimeZoneId</c> задається тут і **не змінюється** після відкриття
    /// першого періоду (<c>ECR-PRD-0409</c>): межі періодів рахуються в поясі
    /// майданчика, і зміна поясу заднім числом зсунула б уже подану звітність.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public Task<IActionResult> Create([FromBody] CreateProjectRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перевірити Project.Manage через IAccessDecisionService; створити doc.Project; " +
            "201 із Location.");

    /// <summary>Клонує проєкт разом із налаштуваннями. Право <c>Project.Manage</c>.</summary>
    [HttpPost("{id:int}/clone")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> Clone(
        int id, [FromBody] CloneProjectRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var projectId = await cloneProject.HandleAsync(id, request.Code, ct).ConfigureAwait(false);
        return Created($"/api/v1/projects/{projectId}", new { projectId });
    }

    /// <summary>
    /// Встановлює поточний період проєкту. Право <c>Period.Configure</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>CurrentPeriod</c> — підказка UI, а **не** правило доступу
    /// (<c>D-77</c>): на рішення про право запису він не впливає взагалі.
    /// <c>null</c> у <c>PinnedPeriodId</c> повертає режим <c>Auto</c>.
    /// </remarks>
    [HttpPut("{id:int}/current-period")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SetCurrentPeriod(
        int id, [FromBody] SetCurrentPeriodRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await setCurrentPeriod
            .HandleAsync(id, request.PinnedPeriodId, request.Reason, ct)
            .ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>Календар періодів проєкту. Право <c>Document.View</c>.</summary>
    [HttpGet("{id:int}/periods")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Periods(int id, CancellationToken ct)
    {
        // Календар добудовується перед читанням: проєкт міг бути створений до
        // того, як задача станів відпрацювала, і порожній список періодів
        // виглядав би як «проєкт зламаний». Виклик ідемпотентний — повторно
        // нічого не створює.
        await buildCalendar.HandleAsync(id, ct).ConfigureAwait(false);

        // ⚠ Межі віддаються в поясі майданчика, а не в UTC (D-68).
        return Ok(await getCalendar.HandleAsync(id, ct).ConfigureAwait(false));
    }
}

/// <summary>Запит на створення проєкту.</summary>
/// <param name="Code">Код проєкту.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="TimeZoneId">Пояс майданчика; після відкриття періоду не змінюється.</param>
/// <param name="PeriodKind">Періодичність.</param>
public sealed record CreateProjectRequest(
    string Code, IReadOnlyDictionary<string, string> NameL10n, string TimeZoneId, string PeriodKind);

/// <summary>Запит на клонування проєкту.</summary>
/// <param name="Code">Код нового проєкту.</param>
public sealed record CloneProjectRequest(string Code);

/// <summary>Запит на зміну поточного періоду.</summary>
/// <param name="PinnedPeriodId">Закріплений період; <c>null</c> — режим <c>Auto</c>.</param>
/// <param name="Reason">Причина закріплення; потрапляє в аудит.</param>
public sealed record SetCurrentPeriodRequest(int? PinnedPeriodId, string? Reason);
