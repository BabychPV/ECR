// src/Ecr.Application/Workflow/ScopedApprovalRouteHandlers.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Workflow;

/// <summary>
/// ✎ B155 (варіант C, 16.11): область налаштування маршруту погодження без версії шаблону.
/// </summary>
/// <param name="ProjectId">Проєкт; <c>null</c> — усі проєкти.</param>
/// <param name="TemplateId">Шаблон; <c>null</c> — усі шаблони.</param>
/// <remarks>
/// (проєкт, шаблон) — виняток проєкту; (–, шаблон) — маршрут шаблону в усіх проєктах; (–, –) — типовий маршрут
/// майданчика. (проєкт, –) — наявний маршрут проєкту, його веде <see cref="ReplaceApprovalRouteHandler"/>.
/// </remarks>
public sealed record ApprovalRouteScope(int? ProjectId, int? TemplateId)
{
    /// <summary>Типовий маршрут майданчика.</summary>
    public static ApprovalRouteScope SiteDefault { get; } = new(null, null);
}

/// <summary>✎ B155: звідки береться маршрут, що діє в області.</summary>
public static class ApprovalRouteSource
{
    /// <summary>Власний маршрут області.</summary>
    public const string Own = "own";

    /// <summary>Успадковано від маршруту шаблону (усі проєкти).</summary>
    public const string Template = "template";

    /// <summary>Успадковано від маршруту проєкту.</summary>
    public const string Project = "project";

    /// <summary>Успадковано від типового маршруту майданчика.</summary>
    public const string Default = "default";

    /// <summary>Маршруту немає: затвердження одноетапне (будь-який носій гранта <c>Approve</c>).</summary>
    public const string None = "none";
}

/// <summary>✎ B155: маршрут області з тим, що в ній діє.</summary>
/// <param name="ProjectId">Проєкт області; <c>null</c> — усі.</param>
/// <param name="TemplateId">Шаблон області; <c>null</c> — усі.</param>
/// <param name="HasRoute">Чи є ВЛАСНИЙ маршрут області.</param>
/// <param name="Steps">Кроки власного маршруту (порожньо — його немає).</param>
/// <param name="EffectiveSource">
/// Звідки маршрут, що діє: <c>own</c> | <c>template</c> | <c>project</c> | <c>default</c> | <c>none</c>
/// (<see cref="ApprovalRouteSource"/>). Для області «шаблон, усі проєкти» маршрути окремих проєктів не враховуються.
/// </param>
/// <param name="EffectiveSteps">Кроки маршруту, що діє (власного або успадкованого).</param>
/// <param name="GeneralApproverAlsoApproves">
/// Маршрут, що діє, — маршрут ШАБЛОНУ: кожен його крок затверджує роль кроку АБО вбудований загальний <c>Approver</c>.
/// </param>
/// <param name="GeneralApproverRoleId">Вбудована загальна роль <c>Approver</c>; <c>null</c> — її немає.</param>
/// <param name="CanEdit">Чи може викликач змінити маршрут цієї області.</param>
/// <param name="WorkflowModeNone">Найновіша версія шаблону області — без подання й затвердження (B75): маршрут не діє.</param>
public sealed record ScopedApprovalRouteDto(
    int? ProjectId,
    int? TemplateId,
    bool HasRoute,
    IReadOnlyList<ApprovalStepDto> Steps,
    string EffectiveSource,
    IReadOnlyList<ApprovalStepDto> EffectiveSteps,
    bool GeneralApproverAlsoApproves,
    int? GeneralApproverRoleId,
    bool CanEdit,
    bool WorkflowModeNone);

/// <summary>✎ B155: поданий аркуш, погодження якого почнеться (почалося) з першого кроку.</summary>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="SheetDefId">Аркуш.</param>
/// <param name="SheetCopyId">Копія (RS); <c>null</c> — оригінал.</param>
/// <param name="PeriodKey">Період.</param>
public sealed record RestartedSheetDto(int ProjectId, long DocumentId, int SheetDefId, long? SheetCopyId, int PeriodKey);

/// <summary>✎ B155 (C-8): подані аркуші, які зміна маршруту області поверне на перший крок.</summary>
/// <param name="Total">Усього таких аркушів (у всіх проєктах, де маршрут області діє, крім проєктів із власним винятком).</param>
/// <param name="Visible">Скільки з них у проєктах, видимих викликачу.</param>
/// <param name="Sheets">Видимі аркуші (не більше <see cref="ApprovalRouteImpact.MaxListed"/>).</param>
/// <param name="Truncated">Видимих більше, ніж у <paramref name="Sheets"/>.</param>
public sealed record ApprovalRouteImpactDto(int Total, int Visible, IReadOnlyList<RestartedSheetDto> Sheets, bool Truncated);

/// <summary>✎ B155: відповідь заміни маршруту області.</summary>
/// <param name="Steps">Скільки кроків тепер у власному маршруті області (0 — прибрано).</param>
/// <param name="Restarted">Подані аркуші, що почали погодження з першого кроку.</param>
public sealed record ApprovalRouteChangeDto(int Steps, ApprovalRouteImpactDto Restarted);

/// <summary>✎ B155 (C-5): шаблон проєкту і маршрут, що для нього діє в цьому проєкті.</summary>
/// <param name="TemplateId">Шаблон.</param>
/// <param name="Code">Код шаблону.</param>
/// <param name="NameL10n">Назва.</param>
/// <param name="WorkflowModeNone">Найновіша версія — без подання й затвердження (B75).</param>
/// <param name="EffectiveSource">Звідки маршрут: <c>own</c> (виняток проєкту) | <c>template</c> | <c>project</c> | <c>default</c> | <c>none</c>.</param>
/// <param name="EffectiveSteps">Кроки маршруту, що діє.</param>
/// <param name="GeneralApproverAlsoApproves">Діє маршрут шаблону: загальний <c>Approver</c> теж затверджує.</param>
public sealed record ProjectTemplateRouteDto(
    int TemplateId,
    string Code,
    LocalizedText NameL10n,
    bool WorkflowModeNone,
    string EffectiveSource,
    IReadOnlyList<ApprovalStepDto> EffectiveSteps,
    bool GeneralApproverAlsoApproves);

/// <summary>✎ B155 (C-7): роль для вибору в кроці маршруту.</summary>
/// <param name="Id">Роль.</param>
/// <param name="Code">Код.</param>
/// <param name="NameL10n">Назва; <c>null</c> — немає.</param>
/// <param name="IsBuiltIn">Вбудована.</param>
/// <param name="IsGeneralApprover">Вбудована загальна роль <c>Approver</c>.</param>
public sealed record ApproverRoleDto(int Id, string Code, LocalizedText? NameL10n, bool IsBuiltIn, bool IsGeneralApprover);

/// <summary>✎ B155: права обробника на області маршруту.</summary>
/// <param name="Project">Виняток проєкту (<c>Project.Manage</c>).</param>
/// <param name="Template">Маршрут шаблону — зміна (<c>Template.Edit</c>).</param>
/// <param name="TemplateRead">Маршрут шаблону — лише читання (<c>Template.View</c>); <c>null</c> — лише <paramref name="Template"/>.</param>
/// <param name="Default">Типовий маршрут майданчика (<c>Security.ManageRoles</c>).</param>
public sealed record ApprovalRouteScopePermissions(string Project, string Template, string? TemplateRead, string Default);

/// <summary>✎ B155: права й перевірки області маршруту — одне місце для читання, підрахунку й заміни.</summary>
/// <remarks>
/// ⛔ Рішення координатора 16.11: маршрут ШАБЛОНУ (усі проєкти) ставить TemplateAdmin (<c>Template.Edit</c>; читання —
/// також <c>Template.View</c>); виняток проєкту — як маршрут проєкту (<c>Project.Manage</c> у проєкті + грант
/// <c>Manage</c> на проєкт); типовий маршрут майданчика — SysAdmin (<c>Security.ManageRoles</c>).
/// </remarks>
public static class ApprovalRouteScopeAccess
{
    /// <summary>Перевіряє право на область; повертає профіль, шаблон області й чи можна змінювати.</summary>
    /// <param name="scope">Область (не «лише проєкт»).</param>
    /// <param name="permissions">Права обробника на кожну область (константи обробника — сторож §9 їх бачить).</param>
    /// <param name="workflow">Сховище.</param>
    /// <param name="access">Рішення про доступ.</param>
    /// <param name="currentUser">Поточний користувач.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<(AccessProfile Profile, ApprovalTemplateRef? Template, bool CanEdit)> AuthorizeAsync(
        ApprovalRouteScope scope,
        ApprovalRouteScopePermissions permissions,
        IWorkflowStore workflow,
        IAccessDecisionService access,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(workflow);

        switch (scope)
        {
            case { ProjectId: { } projectId, TemplateId: { } templateId }:
            {
                var profile = await RequireProjectManageAsync(access, currentUser, permissions.Project, projectId, ct)
                    .ConfigureAwait(false);
                return (profile, await RequireTemplateAsync(workflow, templateId, ct).ConfigureAwait(false), true);
            }

            case { ProjectId: null, TemplateId: { } templateId }:
            {
                var profile = permissions.TemplateRead is { } read
                    ? await PermissionCheck.RequireAnyAsync(access, currentUser, [permissions.Template, read], ct)
                        .ConfigureAwait(false)
                    : await PermissionCheck.RequireAsync(access, currentUser, permissions.Template, ct).ConfigureAwait(false);
                return (profile, await RequireTemplateAsync(workflow, templateId, ct).ConfigureAwait(false),
                    PermissionCheck.IsGranted(profile, permissions.Template));
            }

            case { ProjectId: null, TemplateId: null }:
                return (await PermissionCheck.RequireAsync(access, currentUser, permissions.Default, ct).ConfigureAwait(false),
                    null, true);

            default:
                throw new ArgumentException("Маршрут проєкту без шаблону веде ReplaceApprovalRouteHandler.", nameof(scope));
        }
    }

    /// <summary>
    /// Право <paramref name="permission"/> (<c>Project.Manage</c>) у проєкті, видимість і грант <c>Manage</c> на нього — як
    /// маршрут проєкту (Q-179, S17).
    /// </summary>
    /// <param name="access">Рішення про доступ.</param>
    /// <param name="currentUser">Поточний користувач.</param>
    /// <param name="permission">Проєктне право.</param>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<AccessProfile> RequireProjectManageAsync(
        IAccessDecisionService access, ICurrentUser currentUser, string permission, int projectId, CancellationToken ct)
    {
        var profile = await PermissionCheck.RequireInAnyProjectAsync(access, currentUser, permission, ct)
            .ConfigureAwait(false);

        // ⛔ S17: невидимий проєкт — `404 ECR-PRJ-0404`, як неіснуючий, і ДО права в проєкті й гранта.
        Projects.ProjectVisibility.RequireVisible(profile, projectId);

        // ⛔ ФВ-6.14: право — у ЦЬОМУ проєкті.
        PermissionCheck.RequireIn(profile, permission, projectId);

        if (profile.LevelFor(ResourceKind.Project, projectId) < GrantLevel.Manage)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Немає гранта Manage на проєкт {projectId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.noProjectManageGrant",
                    ["projectId"] = projectId.ToString(CultureInfo.InvariantCulture),
                });
        }

        return profile;
    }

    private static async Task<ApprovalTemplateRef> RequireTemplateAsync(
        IWorkflowStore workflow, int templateId, CancellationToken ct)
        => await workflow.FindTemplateAsync(templateId, ct).ConfigureAwait(false)
           ?? throw GetTemplateCardHandler.NotFound(templateId);
}

/// <summary>✎ B155: подані аркуші, яких торкається зміна маршруту області (C-8), — одне правило для підрахунку й перезапуску.</summary>
public static class ApprovalRouteImpact
{
    /// <summary>Скільки видимих аркушів перелічується у відповіді.</summary>
    public const int MaxListed = 200;

    /// <summary>
    /// Подані аркуші, для яких маршрут області вирішує (<see cref="ApprovalRoute.ScopeGoverns"/>): область стосується
    /// аркуша, і жоден конкретніший маршрут (виняток проєкту, маршрут версії) його не перекриває.
    /// </summary>
    /// <param name="workflow">Сховище.</param>
    /// <param name="scope">Область.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<IReadOnlyList<InFlightSheet>> AffectedAsync(
        IWorkflowStore workflow, ApprovalRouteScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(scope);

        var routes = await workflow.ListActiveRoutesAsync(ct).ConfigureAwait(false);
        var sheets = await workflow.ListInFlightSheetsAsync(scope.ProjectId, scope.TemplateId, ct).ConfigureAwait(false);

        return
        [
            .. sheets.Where(s => ApprovalRoute.ScopeGoverns(
                routes, scope.ProjectId, scope.TemplateId, s.ProjectId, s.TemplateId, s.TemplateVersionId)),
        ];
    }

    /// <summary>
    /// Відповідь для викликача: загальна кількість і ЛИШЕ видимі йому аркуші (видимість проєкту — та сама, що в
    /// переліку проєктів, <see cref="Projects.ProjectVisibility"/>).
    /// </summary>
    /// <param name="affected">Аркуші.</param>
    /// <param name="profile">Профіль викликача.</param>
    public static ApprovalRouteImpactDto ToDto(IReadOnlyList<InFlightSheet> affected, AccessProfile profile)
    {
        ArgumentNullException.ThrowIfNull(affected);
        ArgumentNullException.ThrowIfNull(profile);

        var visible = affected.Where(s => Projects.ProjectVisibility.IsVisible(profile, s.ProjectId)).ToList();
        return new ApprovalRouteImpactDto(
            affected.Count,
            visible.Count,
            [.. visible.Take(MaxListed).Select(s => new RestartedSheetDto(s.ProjectId, s.DocumentId, s.SheetDefId, s.SheetCopyId, s.PeriodKey))],
            visible.Count > MaxListed);
    }
}

/// <summary>✎ B155: перевірка набору ролей кроків — одна на маршрут проєкту й маршрути областей.</summary>
public static class ApprovalRouteSteps
{
    /// <summary>
    /// Найбільша довжина маршруту (A1-06). Технічна межа від зловживання (кожен крок — рядок `wf.ApprovalStep` і рівень
    /// погодження людиною), а не бізнес-правило: реальний маршрут — кілька рівнів.
    /// </summary>
    public const int MaxSteps = 20;

    /// <summary>Перевіряє ролі кроків ДО будь-якої зміни.</summary>
    /// <param name="workflow">Сховище.</param>
    /// <param name="roleIds">Ролі кроків у порядку проходження.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException">Задовгий маршрут або дві однакові ролі поспіль (<c>ECR-DOC-0422</c>).</exception>
    /// <exception cref="NotFoundException">Ролі немає (<c>ECR-SEC-0404</c>).</exception>
    public static async Task ValidateAsync(IWorkflowStore workflow, IReadOnlyList<int> roleIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(roleIds);

        // ⛔ A1-06: довжина маршруту обмежена ДО будь-якого запиту до бази. Раніше `roleIds` приймався з тіла без
        // межі, і кожен крок давав окремий запит `RoleExistsAsync` та рядок `wf.ApprovalStep`.
        if (roleIds.Count > MaxSteps)
        {
            throw new BusinessRuleException(
                "ECR-DOC-0422",
                $"Маршрут має {roleIds.Count} кроків, дозволено не більше {MaxSteps}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0422.approvalRouteTooLong",
                    ["count"] = roleIds.Count.ToString(CultureInfo.InvariantCulture),
                    ["max"] = MaxSteps.ToString(CultureInfo.InvariantCulture),
                });
        }

        // ⛔ Ролі перевіряються ДО будь-якої зміни. Крок на неіснуючу роль дав
        // би маршрут, який неможливо пройти: документ подали б і не
        // затвердили ніколи, а причина була б видима лише в базі.
        // ⚠ Одним запитом на весь набір (A1-06), а не по запиту на крок.
        if (await workflow.FirstMissingRoleAsync(roleIds, ct).ConfigureAwait(false) is { } missingRoleId)
        {
            throw new NotFoundException(
                "ECR-SEC-0404", $"Ролі {missingRoleId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SEC-0404.roleNotFound",
                    ["roleId"] = missingRoleId.ToString(CultureInfo.InvariantCulture),
                });
        }

        // ⚠ Дубль ролі в маршруті — не помилка: та сама роль може
        // затверджувати двічі (наприклад, до і після розрахунку). А от
        // ПОСПІЛЬ два однакові кроки безглузді: другий пройде той самий
        // користувач одразу за першим.
        for (var i = 1; i < roleIds.Count; i++)
        {
            if (roleIds[i] == roleIds[i - 1])
            {
                throw new BusinessRuleException(
                    "ECR-DOC-0422",
                    $"Кроки {i} і {i + 1} мають ту саму роль {roleIds[i]}: другий пройде той самий "
                    + "користувач одразу за першим, тобто погодження не додасться.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-DOC-0422.approvalRouteConsecutiveRole",
                        ["stepA"] = i.ToString(CultureInfo.InvariantCulture),
                        ["stepB"] = (i + 1).ToString(CultureInfo.InvariantCulture),
                        ["roleId"] = roleIds[i].ToString(CultureInfo.InvariantCulture),
                    });
            }
        }
    }

    /// <summary>Кроки маршруту в порядку проходження.</summary>
    /// <param name="route">Маршрут; <c>null</c> — порожньо.</param>
    public static IReadOnlyList<ApprovalStepDto> Of(ApprovalRoute? route)
        => route is null
            ? []
            : [.. route.Steps.OrderBy(s => s.Ordinal).Select(s => new ApprovalStepDto(s.Ordinal, s.RoleId, s.IsOptional))];

    /// <summary>Звідки маршрут, що діє в області (проєкт?, шаблон?): власний чи успадкований.</summary>
    /// <param name="effective">Маршрут, що діє (<see cref="ApprovalRoute.ResolveForScope"/>); <c>null</c> — немає.</param>
    /// <param name="scope">Область.</param>
    public static string SourceOf(ApprovalRoute? effective, ApprovalRouteScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return effective switch
        {
            null => ApprovalRouteSource.None,
            _ when effective.ProjectId == scope.ProjectId && effective.TemplateId == scope.TemplateId => ApprovalRouteSource.Own,
            { TemplateId: not null } => ApprovalRouteSource.Template,
            { ProjectId: not null } => ApprovalRouteSource.Project,
            _ => ApprovalRouteSource.Default,
        };
    }
}

/// <summary>
/// ✎ B155: читання маршруту області — власний і той, що діє (C-3, C-4, C-6, C-9).
/// </summary>
public sealed class GetScopedApprovalRouteHandler(
    IWorkflowStore workflow, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Виняток проєкту: право в проєкті й грант Manage.</summary>
    public const string ProjectPermission = "Project.Manage";

    /// <summary>Маршрут шаблону: зміна.</summary>
    public const string TemplatePermission = "Template.Edit";

    /// <summary>Маршрут шаблону: лише читання.</summary>
    public const string TemplateReadPermission = "Template.View";

    /// <summary>Типовий маршрут майданчика.</summary>
    public const string DefaultPermission = "Security.ManageRoles";

    private static readonly ApprovalRouteScopePermissions Permissions =
        new(ProjectPermission, TemplatePermission, TemplateReadPermission, DefaultPermission);

    /// <summary>Маршрут області.</summary>
    /// <param name="scope">Область (не «лише проєкт»).</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<ScopedApprovalRouteDto> HandleAsync(ApprovalRouteScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var (_, template, canEdit) = await ApprovalRouteScopeAccess
            .AuthorizeAsync(scope, Permissions, workflow, access, currentUser, ct)
            .ConfigureAwait(false);

        var routes = await workflow.ListActiveRoutesAsync(ct).ConfigureAwait(false);
        var own = routes.FirstOrDefault(r => r.TemplateVersionId is null
                                             && r.ProjectId == scope.ProjectId
                                             && r.TemplateId == scope.TemplateId);
        var effective = ApprovalRoute.ResolveForScope(routes, scope.ProjectId, scope.TemplateId);

        return new ScopedApprovalRouteDto(
            scope.ProjectId,
            scope.TemplateId,
            own is not null,
            ApprovalRouteSteps.Of(own),
            ApprovalRouteSteps.SourceOf(effective, scope),
            ApprovalRouteSteps.Of(effective),
            effective?.AllowsGeneralApprover == true,
            await workflow.GeneralApproverRoleIdAsync(ct).ConfigureAwait(false),
            canEdit,
            template?.WorkflowNone == true);
    }
}

/// <summary>✎ B155 (C-8): скільки поданих аркушів зміна маршруту області поверне на перший крок — до збереження.</summary>
public sealed class PreviewApprovalRouteImpactHandler(
    IWorkflowStore workflow, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Виняток проєкту: право в проєкті й грант Manage.</summary>
    public const string ProjectPermission = "Project.Manage";

    /// <summary>Маршрут шаблону.</summary>
    public const string TemplatePermission = "Template.Edit";

    /// <summary>Типовий маршрут майданчика.</summary>
    public const string DefaultPermission = "Security.ManageRoles";

    private static readonly ApprovalRouteScopePermissions Permissions =
        new(ProjectPermission, TemplatePermission, TemplateRead: null, DefaultPermission);

    /// <summary>Підрахунок.</summary>
    /// <param name="scope">Область.</param>
    /// <param name="roleIds">
    /// Запропоновані ролі кроків; <c>null</c> — будь-яка зміна. Набір, рівний наявному (або порожній без наявного
    /// маршруту), нічого не змінює — 0.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<ApprovalRouteImpactDto> HandleAsync(
        ApprovalRouteScope scope, IReadOnlyList<int>? roleIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var (profile, _, _) = await ApprovalRouteScopeAccess
            .AuthorizeAsync(scope, Permissions, workflow, access, currentUser, ct)
            .ConfigureAwait(false);

        if (roleIds is not null)
        {
            var existing = await workflow.FindScopedRouteAsync(scope.ProjectId, scope.TemplateId, ct).ConfigureAwait(false);
            if (ReplaceScopedApprovalRouteHandler.IsNoOp(existing, roleIds))
            {
                return new ApprovalRouteImpactDto(0, 0, [], false);
            }
        }

        var affected = await ApprovalRouteImpact.AffectedAsync(workflow, scope, ct).ConfigureAwait(false);
        return ApprovalRouteImpact.ToDto(affected, profile);
    }
}

/// <summary>
/// ✎ B155: заміна маршруту області цілим набором кроків; подані аркуші, для яких область вирішує, починають
/// погодження з ПЕРШОГО кроку маршруту, що діє після зміни (слово людини 16.11).
/// </summary>
/// <remarks>
/// ⛔ Порожній набір прибирає власний маршрут області — діє успадкований. ⚠ Власна роль НЕ отримує гранта
/// <c>Approve</c> автоматично (рішення координатора 16.11): грант видає адміністратор окремо.
/// </remarks>
public sealed class ReplaceScopedApprovalRouteHandler(
    IWorkflowStore workflow,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    IClock clock,
    ICurrentUser currentUser)
{
    /// <summary>Виняток проєкту: право в проєкті й грант Manage.</summary>
    public const string ProjectPermission = "Project.Manage";

    /// <summary>Маршрут шаблону (TemplateAdmin).</summary>
    public const string TemplatePermission = "Template.Edit";

    /// <summary>Типовий маршрут майданчика (SysAdmin).</summary>
    public const string DefaultPermission = "Security.ManageRoles";

    private static readonly ApprovalRouteScopePermissions Permissions =
        new(ProjectPermission, TemplatePermission, TemplateRead: null, DefaultPermission);

    /// <summary>Замінює маршрут області.</summary>
    /// <param name="scope">Область (не «лише проєкт»).</param>
    /// <param name="roleIds">Ролі кроків у порядку проходження; порожньо — прибрати.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<ApprovalRouteChangeDto> HandleAsync(
        ApprovalRouteScope scope, IReadOnlyList<int> roleIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(roleIds);

        var (profile, _, _) = await ApprovalRouteScopeAccess
            .AuthorizeAsync(scope, Permissions, workflow, access, currentUser, ct)
            .ConfigureAwait(false);
        var userId = currentUser.UserId ?? 0;

        await ApprovalRouteSteps.ValidateAsync(workflow, roleIds, ct).ConfigureAwait(false);

        IReadOnlyList<InFlightSheet> affected = [];
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            var existing = await workflow.FindScopedRouteAsync(scope.ProjectId, scope.TemplateId, innerCt).ConfigureAwait(false);
            if (IsNoOp(existing, roleIds))
            {
                affected = [];
                return;
            }

            // ⚠ Аркуші — ДО зміни: множина «для кого область вирішує» від кроків не залежить (ScopeGoverns).
            affected = await ApprovalRouteImpact.AffectedAsync(workflow, scope, innerCt).ConfigureAwait(false);
            IReadOnlyList<int> oldRoles = [.. ApprovalRouteSteps.Of(existing).Select(s => s.RoleId)];

            var routeId = existing?.Id ?? 0;
            if (roleIds.Count == 0)
            {
                await workflow.RemoveRouteAsync(existing!, innerCt).ConfigureAwait(false);
            }
            else
            {
                var route = existing;
                if (route is null)
                {
                    route = new ApprovalRoute(
                        EcrCode.Create(ApprovalRoute.ScopeCode(scope.ProjectId, scope.TemplateId)),
                        new LocalizedText(new Dictionary<string, string>
                        {
                            ["en"] = ApprovalRoute.ScopeCode(scope.ProjectId, scope.TemplateId),
                        }),
                        scope.ProjectId,
                        templateVersionId: null,
                        templateId: scope.TemplateId);
                    await workflow.AddRouteAsync(route, innerCt).ConfigureAwait(false);
                }
                else
                {
                    await workflow.RemoveStepsAsync(route, innerCt).ConfigureAwait(false);
                }

                foreach (var roleId in roleIds)
                {
                    route.AddStep(roleId);
                }

                await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
                routeId = route.Id;
            }

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

            // Перший крок маршруту, що діє ПІСЛЯ зміни, — тим самим вибором, що й затвердження (`Resolve`).
            if (affected.Count > 0)
            {
                var routes = await workflow.ListActiveRoutesAsync(innerCt).ConfigureAwait(false);
                var firstSteps = affected.ToDictionary(
                    s => s.StateId,
                    s => ApprovalRoute.Resolve(routes, s.ProjectId, s.TemplateId, s.TemplateVersionId)?.FirstStep?.Id);
                var states = await workflow.GetStatesAsync([.. firstSteps.Keys], innerCt).ConfigureAwait(false);
                var restarted = states.Where(state => state.RestartApproval(firstSteps[state.Id])).Select(s => s.Id).ToHashSet();
                affected = [.. affected.Where(s => restarted.Contains(s.StateId))];
            }

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    clock.UtcNow,
                    TemplateVersionId: 0,
                    EntityType: "wf.ApprovalRoute",
                    EntityId: routeId,
                    ChangeClass: ChangeClass.Guarded,
                    Operation: roleIds.Count == 0 ? "Delete" : existing is null ? "Create" : "Update",
                    OldJson: Describe(scope, oldRoles, null),
                    NewJson: Describe(scope, roleIds, affected.Count),
                    ChangeReason: null,
                    ChangedByUserId: userId,
                    CorrelationId: null),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return new ApprovalRouteChangeDto(roleIds.Count, ApprovalRouteImpact.ToDto(affected, profile));
    }

    /// <summary>Чи набір кроків нічого не змінює: той самий порядок ролей, або прибирання відсутнього маршруту.</summary>
    /// <param name="existing">Наявний власний маршрут області.</param>
    /// <param name="roleIds">Запропоновані ролі.</param>
    public static bool IsNoOp(ApprovalRoute? existing, IReadOnlyList<int> roleIds)
    {
        ArgumentNullException.ThrowIfNull(roleIds);

        return existing is null
            ? roleIds.Count == 0
            : ApprovalRouteSteps.Of(existing).Select(s => s.RoleId).SequenceEqual(roleIds);
    }

    private static string Describe(ApprovalRouteScope scope, IReadOnlyList<int> roleIds, int? restarted)
        => JsonSerializer.Serialize(new { scope.ProjectId, scope.TemplateId, roleIds, restarted });
}

/// <summary>✎ B155 (C-5): шаблони проєкту (<c>doc.ProjectTemplate</c>) й маршрут, що діє для кожного в цьому проєкті.</summary>
public sealed class ListProjectTemplateRoutesHandler(
    IWorkflowStore workflow, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право в проєкті (і грант Manage) — як маршрут проєкту.</summary>
    public const string Permission = "Project.Manage";

    /// <summary>Перелік.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<ProjectTemplateRouteDto>> HandleAsync(int projectId, CancellationToken ct)
    {
        await ApprovalRouteScopeAccess.RequireProjectManageAsync(access, currentUser, Permission, projectId, ct)
            .ConfigureAwait(false);

        var templates = await workflow.ListProjectTemplatesAsync(projectId, ct).ConfigureAwait(false);
        var routes = await workflow.ListActiveRoutesAsync(ct).ConfigureAwait(false);

        return
        [
            .. templates.Select(t =>
            {
                var scope = new ApprovalRouteScope(projectId, t.Id);
                var effective = ApprovalRoute.ResolveForScope(routes, projectId, t.Id);
                return new ProjectTemplateRouteDto(
                    t.Id, t.Code, t.NameL10n, t.WorkflowNone,
                    ApprovalRouteSteps.SourceOf(effective, scope),
                    ApprovalRouteSteps.Of(effective),
                    effective?.AllowsGeneralApprover == true);
            }),
        ];
    }
}

/// <summary>
/// ✎ B155 (C-7): ролі для вибору в кроці маршруту — тим, хто веде маршрути, але не має <c>Security.ManageRoles</c>
/// (TemplateAdmin, менеджер проєкту). Лише ідентифікатор, код і назва: без прав ролей.
/// </summary>
public sealed class ListApproverRolesHandler(
    IUserStore users, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Перелік чинних ролей.</summary>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<ApproverRoleDto>> HandleAsync(CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Потрібна автентифікація.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        if (!PermissionCheck.IsGranted(profile, ManageRolesPermission)
            && !PermissionCheck.IsGranted(profile, TemplatePermission)
            && !profile.HasInAnyProject(ProjectPermission))
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {TemplatePermission}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.permission",
                    ["permission"] = TemplatePermission,
                });
        }

        var roles = await users.ListRolesAsync(ct).ConfigureAwait(false);
        return
        [
            .. roles.Where(r => r.IsActive)
                .OrderBy(r => r.Code, StringComparer.Ordinal)
                .Select(r => new ApproverRoleDto(
                    r.Id, r.Code, r.NameL10n, r.IsBuiltIn, r.IsBuiltIn && r.Code == GeneralApproverCode)),
        ];
    }

    /// <summary>Код вбудованої загальної ролі погоджувача (сід <c>00-prelude.sql</c>).</summary>
    public const string GeneralApproverCode = "Approver";

    /// <summary>Право SysAdmin на ролі.</summary>
    public const string ManageRolesPermission = "Security.ManageRoles";

    /// <summary>Право TemplateAdmin на маршрут шаблону.</summary>
    public const string TemplatePermission = "Template.Edit";

    /// <summary>Право менеджера проєкту на маршрут проєкту.</summary>
    public const string ProjectPermission = "Project.Manage";
}
