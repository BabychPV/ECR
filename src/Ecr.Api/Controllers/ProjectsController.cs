using Ecr.Application.Calculations;
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
    ArchiveProjectHandler archive,
    ListPeriodPoliciesHandler policies,
    RunCalculationHandler recalculate,
    Ecr.Application.Workflow.GetApprovalRouteHandler getRoute,
    Ecr.Application.Workflow.ReplaceApprovalRouteHandler replaceRoute) : ControllerBase
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

    /// <summary>
    /// Політики періодів для форми створення проєкту. Право <c>Project.Manage</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Без цього переліку форма не має з чого вибирати політику, надсилає
    /// запит без неї — і сервер відхиляє його з <c>ECR-PRD-0422</c>. Так
    /// створення проєкту, тобто ПЕРШИЙ крок роботи із системою, не працювало
    /// з інтерфейсу взагалі (<c>A7-56</c>).
    /// </remarks>
    [HttpGet("period-policies")]
    [ProducesResponseType<IReadOnlyList<Ecr.Application.Projects.PeriodPolicyDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<Ecr.Application.Projects.PeriodPolicyDto>>> PeriodPolicies(
        CancellationToken ct)
        => Ok(await policies.HandleAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// Маршрут погодження проєкту. Право <c>Project.Manage</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Відповідь є завжди: «маршруту немає» — стан налаштування, а не
    /// помилка, і `404` змусив би клієнт розрізняти його від «проєкту немає»
    /// за тим самим кодом.
    /// </remarks>
    [HttpGet("{id:int}/approval-route")]
    [ProducesResponseType<Ecr.Application.Workflow.ApprovalRouteDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<Ecr.Application.Workflow.ApprovalRouteDto>> ApprovalRoute(
        int id, CancellationToken ct)
        => await getRoute.HandleAsync(id, ct).ConfigureAwait(false);

    /// <summary>
    /// Замінює маршрут погодження проєкту. Право <c>Project.Manage</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Заміна НАБОРОМ кроків, а не поштучна правка: маршрут — це
    /// послідовність, і «змінити третій крок» означає змінити те, після чого
    /// він іде.
    ///
    /// ⚠ Порожній набір ПРИБИРАЄ маршрут, і затвердження повертається до
    /// одноетапного. Без цього маршрут, заведений помилково, лишався б назавжди.
    /// </remarks>
    [HttpPut("{id:int}/approval-route")]
    [ProducesResponseType<Contracts.AffectedStepsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ReplaceApprovalRoute(
        int id, [FromBody] ReplaceApprovalRouteRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var steps = await replaceRoute.HandleAsync(id, request.RoleIds, ct).ConfigureAwait(false);

        return Ok(new Contracts.AffectedStepsResponse(steps));
    }

    /// <summary>Створює проєкт. Право <c>Project.Manage</c>.</summary>
    /// <remarks>
    /// <c>TimeZoneId</c> задається тут і **не змінюється** після відкриття
    /// першого періоду (<c>ECR-PRD-0409</c>): межі періодів рахуються в поясі
    /// майданчика, і зміна поясу заднім числом зсунула б уже подану звітність.
    ///
    /// ⛔ Тільки ідентифікатор IANA (<c>Asia/Aqtau</c>). Windows-ідентифікатор
    /// (<c>Central Asia Standard Time</c>) і зсув (<c>+05:00</c>) — це
    /// <c>ECR-CFG-4221</c>: зсув міняється переходом на літній час, а
    /// Windows-ідентифікатор на зворотному шляху втрачає державу
    /// (<c>Asia/Aqtau</c> повертається як <c>Asia/Tashkent</c>).
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<Contracts.ProjectIdResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
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
                ct,
                request.CustomPeriodCount)
            .ConfigureAwait(false);

        return Created($"/api/v1/projects/{projectId}", new Contracts.ProjectIdResponse(projectId));
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
    [ProducesResponseType<Contracts.ProjectIdResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Clone(
        int id, [FromBody] CloneProjectRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var projectId = await cloneProject.HandleAsync(id, request.Code, ct).ConfigureAwait(false);
        return Created($"/api/v1/projects/{projectId}", new Contracts.ProjectIdResponse(projectId));
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

    /// <summary>
    /// Перерахунок УСЬОГО проєкту. Право <c>Calculation.Recalculate</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Q-151/Q-162 (аудит фази 1). <c>RunCalculationHandler</c> існував,
    /// був протестований і не мав звідки його викликати: жоден контролер
    /// на нього не посилався. Задача, яку він ставить у чергу
    /// (<c>IRecalculationJob</c>), тепер справді перераховує всі документи
    /// проєкту, коли <c>periodKey</c> — <c>null</c> (повний рік) чи період
    /// охоплює кілька документів (Q-162: раніше `DocumentId = 0` мовчки
    /// повертав нуль перерахованих прив'язок).
    /// <para>
    /// Довга операція — у фон, як і перерахунок документа: повертає
    /// <c>jobId</c>, а не результат.
    /// </para>
    /// </remarks>
    [HttpPost("{id:int}/recalculate")]
    [ProducesResponseType<Contracts.ProjectRecalculationAcceptedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Recalculate(
        int id, [FromBody] ProjectRecalculationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ⚠ Погодження передається лише коли обидва поля заповнені: часткове
        // (сама причина без того, хто погодив, чи навпаки) для
        // `RunCalculationHandler` означає «погодження немає» — і саме так
        // правило ФВ-9.7 і мало відмовити.
        var approval = request is { ApprovedByUserId: { } approvedBy, ApprovalReason: { } reason }
            ? new ClosedPeriodApproval(approvedBy, reason)
            : null;

        var jobId = await recalculate
            .HandleAsync(id, request.PeriodKey, approval, ct)
            .ConfigureAwait(false);

        return Accepted(new Contracts.ProjectRecalculationAcceptedResponse(jobId, id, request.PeriodKey));
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
/// <param name="TimeZoneId">
/// Пояс майданчика — ідентифікатор IANA (`Asia/Aqtau`). Обов'язковий; після
/// відкриття періоду не змінюється. Windows-ідентифікатор або зсув —
/// `ECR-CFG-4221`.
/// </param>
/// <param name="PeriodKind">Періодичність.</param>
/// <param name="Year">Звітний рік; типово поточний.</param>
/// <param name="TemplateVersionId">Версія шаблону, за якою заповнюються документи.</param>
/// <param name="PeriodPolicyId">Політика зсувів періодів.</param>
/// <param name="CustomPeriodCount">
/// Кількість періодів для <c>PeriodKind = "Custom"</c> (T6/#36); для решти
/// періодичностей ігнорується. Має ділити рік нарівно (1..12), інакше
/// <c>ECR-PRD-4224</c>.
/// </param>
/// <remarks>
/// ⚠ Чотири останні поля додані понад форму <c>05h</c>: без версії шаблону
/// проєкт не має структури, без політики — меж періодів, без року календар
/// нема на що будувати, а без кількості <c>Custom</c> лишався оголошеним у
/// домені й недосяжним через API (T6/#36). Позиційний префікс контракту не
/// змінений (<c>D1-01</c>).
/// </remarks>
public sealed record CreateProjectRequest(
    string Code,
    IReadOnlyDictionary<string, string> NameL10n,
    string TimeZoneId,
    string PeriodKind,
    int? Year = null,
    int? TemplateVersionId = null,
    int? PeriodPolicyId = null,
    int? CustomPeriodCount = null);

/// <summary>Запит на заміну маршруту погодження.</summary>
/// <param name="RoleIds">
/// Ролі кроків у порядку проходження; порожній набір прибирає маршрут і
/// повертає одноетапне затвердження.
/// </param>
public sealed record ReplaceApprovalRouteRequest(IReadOnlyList<int> RoleIds);

/// <summary>Запит на клонування проєкту.</summary>
/// <param name="Code">Код нового проєкту.</param>
public sealed record CloneProjectRequest(string Code);

/// <summary>Запит на зміну поточного періоду.</summary>
/// <param name="PinnedPeriodId">Закріплений період; <c>null</c> — режим <c>Auto</c>.</param>
/// <param name="Reason">Причина закріплення; потрапляє в аудит.</param>
public sealed record SetCurrentPeriodRequest(int? PinnedPeriodId, string? Reason);

/// <summary>Запит на перерахунок усього проєкту (Q-151).</summary>
/// <param name="PeriodKey">Період; <c>null</c> — повний рік, усі документи проєкту.</param>
/// <param name="ApprovedByUserId">
/// Хто погодив перерахунок закритого періоду (ФВ-9.7); <c>null</c> — без погодження.
/// </param>
/// <param name="ApprovalReason">Причина погодження; обов'язкова разом із <c>ApprovedByUserId</c>.</param>
public sealed record ProjectRecalculationRequest(
    int? PeriodKey, int? ApprovedByUserId, string? ApprovalReason);
