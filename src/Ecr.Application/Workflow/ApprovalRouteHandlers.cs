using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Workflow;

/// <summary>
/// Маршрут погодження проєкту (<c>ФВ-5.17</c>).
/// </summary>
/// <remarks>
/// ⛔ До цього сутність <c>ApprovalRoute</c> і таблиця <c>wf.ApprovalStep</c>
/// існували, були покриті конфігурацією EF — і не мали ЖОДНОГО способу
/// наповнення: список кроків приватний, ендпоінта немає, seed нічого не
/// створює. Дві таблиці, якими ніхто не користується, — це рівно той клас,
/// що дав `A7-25`: стан у переліку, до якого не веде жоден перехід.
///
/// ⚠ Порожній набір кроків — законна операція: він ПРИБИРАЄ маршрут, і
/// затвердження повертається до одноетапного. Без цього маршрут, заведений
/// помилково, лишався б назавжди.
/// </remarks>
public sealed class GetApprovalRouteHandler(
    IWorkflowStore workflow, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право керування проєктами: маршрут — налаштування проєкту.</summary>
    public const string Permission = "Project.Manage";

    /// <summary>Повертає ВЛАСНИЙ маршрут проєкту; порожній — його немає.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<ApprovalRouteDto> HandleAsync(int projectId, CancellationToken ct)
    {
        var profile = await PermissionCheck.RequireAsync(access, currentUser, Permission, ct)
                                            .ConfigureAwait(false);

        // ⛔ Q-179 (аудит фази 2, авторизація): грант на КОНКРЕТНИЙ проєкт,
        // не лише глобальне `Project.Manage` — рішення людини.
        if (profile.LevelFor(ResourceKind.Project, projectId) < GrantLevel.Manage)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Немає гранта Manage на проєкт {projectId}.");
        }

        var route = await workflow.FindProjectRouteAsync(projectId, ct).ConfigureAwait(false);

        // ⚠ Відповідь є завжди, навіть без маршруту: «маршруту немає» — це
        // стан налаштування, а не помилка. `404` тут змусив би клієнт
        // розрізняти «проєкту немає» і «маршруту немає» за тим самим кодом.
        return route is null
            ? new ApprovalRouteDto(projectId, false, [])
            : new ApprovalRouteDto(
                projectId,
                true,
                [.. route.Steps.OrderBy(s => s.Ordinal)
                    .Select(s => new ApprovalStepDto(s.Ordinal, s.RoleId, s.IsOptional))]);
    }
}

/// <summary>Заміна маршруту погодження проєкту цілим набором кроків.</summary>
/// <remarks>
/// ⛔ Заміна НАБОРОМ, а не поштучна правка кроків. Маршрут — це
/// послідовність, і «змінити третій крок» у ній означає змінити те, після чого
/// він іде; часткова правка лишала б в аудиті сліди, з яких не відновити, який
/// маршрут діяв учора.
/// </remarks>
public sealed class ReplaceApprovalRouteHandler(
    IWorkflowStore workflow,
    IAccessDecisionService access,
    IUnitOfWork uow,
    ICurrentUser currentUser)
{
    /// <summary>Право керування проєктами.</summary>
    public const string Permission = "Project.Manage";

    /// <summary>Замінює маршрут проєкту.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="roleIds">Ролі кроків у порядку проходження; порожньо — прибрати маршрут.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки кроків тепер у маршруті.</returns>
    public async Task<int> HandleAsync(int projectId, IReadOnlyList<int> roleIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roleIds);

        var profile = await PermissionCheck.RequireAsync(access, currentUser, Permission, ct)
                                            .ConfigureAwait(false);

        // ⛔ Q-179 (аудит фази 2, авторизація): грант на КОНКРЕТНИЙ проєкт,
        // не лише глобальне `Project.Manage` — рішення людини.
        if (profile.LevelFor(ResourceKind.Project, projectId) < GrantLevel.Manage)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Немає гранта Manage на проєкт {projectId}.");
        }

        // ⛔ Ролі перевіряються ДО будь-якої зміни. Крок на неіснуючу роль дав
        // би маршрут, який неможливо пройти: документ подали б і не
        // затвердили ніколи, а причина була б видима лише в базі.
        foreach (var roleId in roleIds)
        {
            if (!await workflow.RoleExistsAsync(roleId, ct).ConfigureAwait(false))
            {
                throw new NotFoundException("ECR-SEC-0404", $"Ролі {roleId} не існує.");
            }
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
                    + "користувач одразу за першим, тобто погодження не додасться.");
            }
        }

        var existing = await workflow.FindProjectRouteAsync(projectId, ct).ConfigureAwait(false);

        if (roleIds.Count == 0)
        {
            // Порожній набір ПРИБИРАЄ маршрут: затвердження повертається до
            // одноетапного. Інакше помилково заведений маршрут лишався б назавжди.
            if (existing is not null)
            {
                await workflow.RemoveRouteAsync(existing, ct).ConfigureAwait(false);
                await uow.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            return 0;
        }

        var route = existing;

        if (route is null)
        {
            route = new ApprovalRoute(
                EcrCode.Create($"PRJ_{projectId}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = $"Project {projectId}" }),
                projectId);

            await workflow.AddRouteAsync(route, ct).ConfigureAwait(false);
        }
        else
        {
            await workflow.RemoveStepsAsync(route, ct).ConfigureAwait(false);
        }

        foreach (var roleId in roleIds)
        {
            route.AddStep(roleId);
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return roleIds.Count;
    }
}

/// <summary>Маршрут погодження проєкту.</summary>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="HasRoute">
/// Чи налаштований власний маршрут. <c>false</c> — затвердження одноетапне:
/// достатньо одного носія рівня <c>Approve</c>.
/// </param>
/// <param name="Steps">Кроки в порядку проходження.</param>
public sealed record ApprovalRouteDto(int ProjectId, bool HasRoute, IReadOnlyList<ApprovalStepDto> Steps);

/// <summary>Крок маршруту.</summary>
/// <param name="Ordinal">Порядковий номер, від 1.</param>
/// <param name="RoleId">Роль, яка затверджує на цьому кроці.</param>
/// <param name="IsOptional">Крок можна пропустити.</param>
public sealed record ApprovalStepDto(int Ordinal, int RoleId, bool IsOptional);
