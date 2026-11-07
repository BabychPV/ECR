// src/Ecr.Application/Calculations/RecalculationGrantPolicy.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.Domain.Enums;

namespace Ecr.Application.Calculations;

/// <summary>Поріг гранта на проєкт для запуску перерахунку проєкту (ФВ-9.7, аудит S1, Q-238).</summary>
/// <remarks>
/// ✎ 07.11 (D-468): до цього клас звався <c>RecalculationApprovalPolicy</c> і тримав ще поріг
/// підтвердження погоджень перерахунку закритого періоду. Погодження прибрано (закритий місяць має
/// один шлях — Reopen), лишився поріг ініціатора — в ОДНОМУ місці (питання S13 — зміна одного рядка тут).
/// </remarks>
public static class RecalculationGrantPolicy
{
    /// <summary>Грант на проєкт, з якого можна запускати перерахунок проєкту.</summary>
    public const GrantLevel InitiatorGrant = GrantLevel.Read;

    /// <summary>Вимагає гранта на проєкт не нижче <paramref name="level"/>.</summary>
    /// <exception cref="AccessDeniedException"><c>ECR-AUTH-0403</c> із ключем за рівнем.</exception>
    public static void RequireProjectGrant(AccessProfile profile, int projectId, GrantLevel level)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.LevelFor(ResourceKind.Project, projectId) >= level)
        {
            return;
        }

        var key = level switch
        {
            >= GrantLevel.Manage => "err.ECR-AUTH-0403.noProjectManageGrant",
            >= GrantLevel.Write => "err.ECR-AUTH-0403.noProjectWriteGrant",
            _ => "err.ECR-AUTH-0403.noProjectGrant",
        };

        throw new AccessDeniedException(
            "ECR-AUTH-0403", $"Немає гранта {level} на проєкт {projectId}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = key,
                ["projectId"] = projectId.ToString(CultureInfo.InvariantCulture),
            });
    }
}
