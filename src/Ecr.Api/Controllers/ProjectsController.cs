using Ecr.Application.Common;
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
    ListProjectsHandler list,
    CreateProjectHandler create,
    BuildPeriodCalendarHandler buildCalendar,
    GetPeriodCalendarHandler getCalendar,
    SetCurrentPeriodHandler setCurrentPeriod,
    CloneProjectHandler cloneProject,
    ActivateProjectHandler activate,
    ArchiveProjectHandler archive) : ControllerBase
{
    /// <summary>Перелік проєктів. Право <c>Document.View</c>.</summary>
    [HttpGet]
    // ⛔ Тип відповіді оголошений явно — інакше клієнт описує її рукописним
    // інтерфейсом і помиляється в назві поля (`A7-16`, `A7-32`).
    [ProducesResponseType<PagedResult<Ecr.Application.Projects.ProjectSummary>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] int limit, [FromQuery] string? cursor, CancellationToken ct)
        // Видимість за AccessProfile — в обробнику: перелік проєктів, до яких
        // немає доступу, це вже розвідка структури підприємства.
        => Ok(await list.HandleAsync(new CursorRequest(limit == 0 ? 50 : limit, cursor), ct)
            .ConfigureAwait(false));

    /// <summary>Створює проєкт. Право <c>Project.Manage</c>.</summary>
    /// <remarks>
    /// <c>TimeZoneId</c> задається тут і **не змінюється** після відкриття
    /// першого періоду (<c>ECR-PRD-0409</c>): межі періодів рахуються в поясі
    /// майданчика, і зміна поясу заднім числом зсунула б уже подану звітність.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateProjectRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kind = Enum.TryParse<Ecr.Domain.Enums.PeriodKind>(request.PeriodKind, out var parsed)
            ? parsed
            : Ecr.Domain.Enums.PeriodKind.Monthly;

        var projectId = await create
            .HandleAsync(
                request.Code, request.NameL10n, request.TimeZoneId, kind,
                // ⛔ Рік за замовчуванням підставляє ОБРОБНИК: час у системі
                // береться лише через IClock (архітектурне правило), інакше
                // тест на «зараз» залежав би від годинника машини.
                request.Year,
                request.TemplateVersionId ?? 0,
                request.PeriodPolicyId ?? 0,
                ct)
            .ConfigureAwait(false);

        return Created($"/api/v1/projects/{projectId}", new { projectId });
    }

    /// <summary>
    /// Активує проєкт. Право <c>Project.Manage</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Без цього маршруту проєкт лишається чернеткою назавжди, періоди не
    /// відкриваються і система не приймає жодного значення (`A7-25`).
    /// </remarks>
    [HttpPost("{id:int}/activate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Activate(int id, CancellationToken ct)
    {
        await activate.HandleAsync(id, ct).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Позначає проєкт заархівованим. Право <c>Project.Manage</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Це позначка, а не перенесення даних: фізично в <c>arc.*</c> їх
    /// переносить окрема задача, і робить це свідомим кроком людини.
    /// Дозволено лише коли всі періоди закриті (`D-123`).
    /// </remarks>
    [HttpPost("{id:int}/archive")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Archive(int id, CancellationToken ct)
    {
        await archive.HandleAsync(id, ct).ConfigureAwait(false);

        return NoContent();
    }

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
    [ProducesResponseType<Ecr.Application.Periods.Dto.PeriodCalendarDto>(StatusCodes.Status200OK)]
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
/// <param name="Year">Звітний рік; типово поточний.</param>
/// <param name="TemplateVersionId">Версія шаблону, за якою заповнюються документи.</param>
/// <param name="PeriodPolicyId">Політика зсувів періодів.</param>
/// <remarks>
/// ⚠ Три останні поля додані понад форму <c>05h</c>: без версії шаблону
/// проєкт не має структури, без політики — меж періодів, а без року календар
/// нема на що будувати. Позиційний префікс контракту не змінений
/// (<c>D1-01</c>).
/// </remarks>
public sealed record CreateProjectRequest(
    string Code,
    IReadOnlyDictionary<string, string> NameL10n,
    string TimeZoneId,
    string PeriodKind,
    int? Year = null,
    int? TemplateVersionId = null,
    int? PeriodPolicyId = null);

/// <summary>Запит на клонування проєкту.</summary>
/// <param name="Code">Код нового проєкту.</param>
public sealed record CloneProjectRequest(string Code);

/// <summary>Запит на зміну поточного періоду.</summary>
/// <param name="PinnedPeriodId">Закріплений період; <c>null</c> — режим <c>Auto</c>.</param>
/// <param name="Reason">Причина закріплення; потрапляє в аудит.</param>
public sealed record SetCurrentPeriodRequest(int? PinnedPeriodId, string? Reason);
