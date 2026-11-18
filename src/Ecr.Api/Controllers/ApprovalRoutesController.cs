using Ecr.Application.Workflow;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// ✎ B155 (варіант C, 16.11): маршрути погодження ШАБЛОНУ (усі проєкти), винятку проєкту для шаблону й типовий маршрут
/// майданчика; перелік ролей для кроків і шаблонів проєкту з маршрутом, що діє.
/// </summary>
/// <remarks>
/// ⛔ Крок маршруту шаблону затверджує роль кроку АБО вбудований загальний <c>Approver</c>. Зміна маршруту повертає
/// подані аркуші, для яких область вирішує, на ПЕРШИЙ крок (відповідь <c>PUT</c> і <c>POST …/preview</c> — скільки).
/// Маршрут проєкту без шаблону — як і досі, <c>/projects/{id}/approval-route</c>.
/// </remarks>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class ApprovalRoutesController(
    GetScopedApprovalRouteHandler getRoute,
    ReplaceScopedApprovalRouteHandler replaceRoute,
    PreviewApprovalRouteImpactHandler previewRoute,
    ListProjectTemplateRoutesHandler projectTemplates,
    ListApproverRolesHandler approverRoles) : ControllerBase
{
    /// <summary>Маршрут шаблону в усіх проєктах. Право <c>Template.Edit</c> або <c>Template.View</c>.</summary>
    /// <param name="id">Шаблон.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("templates/{id:int}/approval-route")]
    [ProducesResponseType<ScopedApprovalRouteDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ScopedApprovalRouteDto>> TemplateRoute(int id, CancellationToken ct)
        => await getRoute.HandleAsync(new ApprovalRouteScope(null, id), ct).ConfigureAwait(false);

    /// <summary>Замінює маршрут шаблону в усіх проєктах. Право <c>Template.Edit</c>.</summary>
    /// <param name="id">Шаблон.</param>
    /// <param name="request">Ролі кроків; порожньо — прибрати.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPut("templates/{id:int}/approval-route")]
    [ProducesResponseType<ApprovalRouteChangeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApprovalRouteChangeDto>> ReplaceTemplateRoute(
        int id, [FromBody] ReplaceApprovalRouteRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await replaceRoute.HandleAsync(new ApprovalRouteScope(null, id), request.RoleIds, ct).ConfigureAwait(false);
    }

    /// <summary>Скільки поданих аркушів зміна маршруту шаблону поверне на перший крок. Право <c>Template.Edit</c>.</summary>
    /// <param name="id">Шаблон.</param>
    /// <param name="request">Запропоновані ролі; без них — будь-яка зміна.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost("templates/{id:int}/approval-route/preview")]
    [ProducesResponseType<ApprovalRouteImpactDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApprovalRouteImpactDto>> PreviewTemplateRoute(
        int id, [FromBody] ApprovalRoutePreviewRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await previewRoute.HandleAsync(new ApprovalRouteScope(null, id), request.RoleIds, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Виняток проєкту для шаблону. Право <c>Project.Manage</c> у проєкті й грант <c>Manage</c> на проєкт.
    /// </summary>
    /// <param name="id">Проєкт.</param>
    /// <param name="templateId">Шаблон.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("projects/{id:int}/templates/{templateId:int}/approval-route")]
    [ProducesResponseType<ScopedApprovalRouteDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ScopedApprovalRouteDto>> ProjectTemplateRoute(
        int id, int templateId, CancellationToken ct)
        => await getRoute.HandleAsync(new ApprovalRouteScope(id, templateId), ct).ConfigureAwait(false);

    /// <summary>
    /// Замінює виняток проєкту для шаблону. Право <c>Project.Manage</c> у проєкті й грант <c>Manage</c> на проєкт.
    /// </summary>
    /// <param name="id">Проєкт.</param>
    /// <param name="templateId">Шаблон.</param>
    /// <param name="request">Ролі кроків; порожньо — прибрати виняток.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPut("projects/{id:int}/templates/{templateId:int}/approval-route")]
    [ProducesResponseType<ApprovalRouteChangeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApprovalRouteChangeDto>> ReplaceProjectTemplateRoute(
        int id, int templateId, [FromBody] ReplaceApprovalRouteRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await replaceRoute.HandleAsync(new ApprovalRouteScope(id, templateId), request.RoleIds, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Скільки поданих аркушів зміна винятку поверне на перший крок. Право <c>Project.Manage</c>.</summary>
    /// <param name="id">Проєкт.</param>
    /// <param name="templateId">Шаблон.</param>
    /// <param name="request">Запропоновані ролі; без них — будь-яка зміна.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost("projects/{id:int}/templates/{templateId:int}/approval-route/preview")]
    [ProducesResponseType<ApprovalRouteImpactDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApprovalRouteImpactDto>> PreviewProjectTemplateRoute(
        int id, int templateId, [FromBody] ApprovalRoutePreviewRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await previewRoute.HandleAsync(new ApprovalRouteScope(id, templateId), request.RoleIds, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Шаблони проєкту (основний і приєднані) з маршрутом, що діє для кожного. Право <c>Project.Manage</c> у проєкті й
    /// грант <c>Manage</c> на проєкт.
    /// </summary>
    /// <param name="id">Проєкт.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("projects/{id:int}/template-routes")]
    [ProducesResponseType<IReadOnlyList<ProjectTemplateRouteDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<ProjectTemplateRouteDto>>> ProjectTemplateRoutes(
        int id, CancellationToken ct)
        => Ok(await projectTemplates.HandleAsync(id, ct).ConfigureAwait(false));

    /// <summary>Типовий маршрут майданчика (шаблони без власного маршруту). Право <c>Security.ManageRoles</c>.</summary>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("approval-route/default")]
    [ProducesResponseType<ScopedApprovalRouteDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ScopedApprovalRouteDto>> DefaultRoute(CancellationToken ct)
        => await getRoute.HandleAsync(ApprovalRouteScope.SiteDefault, ct).ConfigureAwait(false);

    /// <summary>Замінює типовий маршрут майданчика. Право <c>Security.ManageRoles</c>.</summary>
    /// <param name="request">Ролі кроків; порожньо — прибрати.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPut("approval-route/default")]
    [ProducesResponseType<ApprovalRouteChangeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApprovalRouteChangeDto>> ReplaceDefaultRoute(
        [FromBody] ReplaceApprovalRouteRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await replaceRoute.HandleAsync(ApprovalRouteScope.SiteDefault, request.RoleIds, ct).ConfigureAwait(false);
    }

    /// <summary>Скільки поданих аркушів зміна типового маршруту поверне на перший крок. Право <c>Security.ManageRoles</c>.</summary>
    /// <param name="request">Запропоновані ролі; без них — будь-яка зміна.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost("approval-route/default/preview")]
    [ProducesResponseType<ApprovalRouteImpactDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApprovalRouteImpactDto>> PreviewDefaultRoute(
        [FromBody] ApprovalRoutePreviewRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await previewRoute.HandleAsync(ApprovalRouteScope.SiteDefault, request.RoleIds, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ролі для кроків маршруту (ідентифікатор, код, назва). Право <c>Security.ManageRoles</c>, <c>Template.Edit</c> або
    /// <c>Project.Manage</c>.
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("roles/approvers")]
    [ProducesResponseType<IReadOnlyList<ApproverRoleDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<ApproverRoleDto>>> ApproverRoles(CancellationToken ct)
        => Ok(await approverRoles.HandleAsync(ct).ConfigureAwait(false));
}

/// <summary>✎ B155: запит підрахунку перезапуску (C-8).</summary>
/// <param name="RoleIds">Запропоновані ролі кроків; <c>null</c> — будь-яка зміна; рівні наявним — 0.</param>
public sealed record ApprovalRoutePreviewRequest(IReadOnlyList<int>? RoleIds);
