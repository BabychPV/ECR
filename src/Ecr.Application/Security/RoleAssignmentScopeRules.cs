// src/Ecr.Application/Security/RoleAssignmentScopeRules.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Security;

/// <summary>Область дії призначення ролі в тілі запиту (ФВ-6.14).</summary>
/// <param name="Projects">Проєкти, у яких роль діє; непорожньо, без повторів.</param>
/// <remarks>
/// Та сама форма, що й збережений <c>sec.RoleAssignment.ScopeJson</c>:
/// <c>{"projects":[1,2]}</c>. Аркуш і період (названі в ТЗ) не реалізовано —
/// див. <see cref="RoleAssignmentScope"/>.
/// </remarks>
public sealed record RoleScopeDto(IReadOnlyList<int> Projects);

/// <summary>Перевірка області дії, яку адміністратор задає призначенню.</summary>
internal static class RoleAssignmentScopeRules
{
    /// <summary>
    /// Перевіряє область і повертає її доменну форму.
    /// </summary>
    /// <param name="scope">Область із запиту.</param>
    /// <param name="roleCode">Роль, якій область задається, — для подробиць відмови.</param>
    /// <param name="actor">Профіль того, хто призначає.</param>
    /// <param name="documents">Сховище — для перевірки існування проєкту.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException">Порожньо, повтор, проєкту немає — <c>422 ECR-REQ-0422</c>.</exception>
    /// <exception cref="AccessDeniedException">Немає <c>Manage</c> на проєкт — <c>403 ECR-AUTH-0403</c>.</exception>
    /// <remarks>
    /// ⛔ Обмежити роль проєктом може лише той, хто цим проєктом КЕРУЄ
    /// (<c>Manage</c>) — на додачу до <c>Security.ManageUsers</c>, яке вже
    /// перевірив обробник. Інакше область стала б способом розпоряджатися
    /// чужим проєктом.
    ///
    /// ⚠ Проєкт, якого той, хто призначає, не бачить (<c>None</c>), дає ту
    /// саму відповідь, що й неіснуючий (<c>ФВ-14.2</c>): різниця між 422 і 403
    /// сама розкривала б, що проєкт існує.
    /// </remarks>
    public static async Task<RoleAssignmentScope> ValidateAsync(
        RoleScopeDto scope, string roleCode, AccessProfile actor, IDocumentStore documents, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(documents);

        var projects = scope.Projects ?? [];
        if (projects.Count == 0 || projects.Any(id => id <= 0) || projects.Distinct().Count() != projects.Count)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Роль «{roleCode}»: область дії — непорожній перелік проєктів без повторів.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422",
                    ["code"] = roleCode,
                });
        }

        foreach (var projectId in projects)
        {
            var level = actor.LevelFor(ResourceKind.Project, projectId);
            var exists = await documents.FindProjectStatusAsync(projectId, ct).ConfigureAwait(false) is not null;
            var id = projectId.ToString(CultureInfo.InvariantCulture);

            if (!exists || level == GrantLevel.None)
            {
                throw new BusinessRuleException(
                    ErrorCodes.RequestInvalid,
                    $"Роль «{roleCode}»: проєкту {id} в області дії не існує.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-REQ-0422",
                        ["code"] = roleCode,
                        ["projectId"] = id,
                    });
            }

            if (level < GrantLevel.Manage)
            {
                throw new AccessDeniedException(
                    "ECR-AUTH-0403",
                    $"Обмежити роль проєктом {id} може лише той, хто має на нього Manage.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-AUTH-0403.noProjectManageGrant",
                        ["projectId"] = id,
                    });
            }
        }

        return RoleAssignmentScope.Create(projects);
    }
}
