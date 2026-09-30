// src/Ecr.Application/Projects/ProjectVisibility.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.Domain.Errors;

namespace Ecr.Application.Projects;

/// <summary>
/// Проєкт, якого користувач не бачить, для нього НЕ ІСНУЄ: відповідь та сама,
/// що й на неіснуючий, — <c>404 ECR-PRJ-0404</c> (S17, аудит безпеки).
/// </summary>
/// <remarks>
/// ⛔ S17. Обробники проєкту й періоду перевіряли ІСНУВАННЯ до гранта:
/// неіснуючий <c>projectId</c> давав <c>404</c>, а існуючий чужий — <c>403</c>
/// «немає гранта». Різниця між відповідями сама є відомістю: перебором
/// ідентифікаторів видно, які проєкти (і періоди) є в системі. Для документів
/// те саме вже закрито (<c>DocumentVisibility</c>, B-08) — тут рівно той
/// самий підхід для проєкту.
///
/// ⚠ Видимість — <see cref="AccessProfile.SeesDocumentsOf"/>: грант
/// <c>Read</c> і вище на проєкт або роль, звужена аркушами чи періодами
/// (D-214). Хто бачить документи проєкту, той знає, що проєкт існує, —
/// приховувати від нього нічого. Глобального права «бачити всі проєкти» в
/// профілі немає: навіть системний адміністратор без гранта проєкту не бачить
/// (bootstrap навмисно мінімальний).
///
/// ⚠ Лише ВИДИМІСТЬ. Видимий проєкт без потрібного рівня (<c>Read</c> замість
/// <c>Manage</c>) чи без права в ньому і далі отримує <c>403</c> із причиною:
/// приховувати там нічого, людина проєкт бачить.
/// </remarks>
public static class ProjectVisibility
{
    /// <summary>Чи бачить користувач проєкт.</summary>
    /// <param name="profile">Профіль користувача.</param>
    /// <param name="projectId">Проєкт.</param>
    public static bool IsVisible(AccessProfile profile, int projectId)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.SeesDocumentsOf(projectId);
    }

    /// <summary>Вимагає, щоб проєкт був видимий користувачеві.</summary>
    /// <param name="profile">Профіль користувача.</param>
    /// <param name="projectId">Проєкт.</param>
    /// <exception cref="NotFoundException"><c>ECR-PRJ-0404</c> — проєкт невидимий.</exception>
    public static void RequireVisible(AccessProfile profile, int projectId)
    {
        if (!IsVisible(profile, projectId))
        {
            throw NotFound(projectId);
        }
    }

    /// <summary>Відповідь «проєкту немає» — однакова для відсутнього й невидимого.</summary>
    /// <param name="projectId">Проєкт.</param>
    public static NotFoundException NotFound(int projectId)
        => new(
            ErrorCodes.ProjectNotFound,
            $"Проєкт {projectId} не знайдено.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "err.ECR-PRJ-0404.project",
                ["projectId"] = projectId.ToString(CultureInfo.InvariantCulture),
            });

    /// <summary>Відповідь «періоду немає» — однакова для відсутнього й періоду невидимого проєкту.</summary>
    /// <param name="periodId">Період.</param>
    public static NotFoundException PeriodNotFound(int periodId)
        => new(
            ErrorCodes.PeriodNotFound,
            $"Період {periodId} не знайдено.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "err.ECR-PRD-0404.period",
                ["periodId"] = periodId.ToString(CultureInfo.InvariantCulture),
            });
}
