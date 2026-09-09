using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Domain.Enums;

namespace Ecr.Application.Security;

/// <summary>Поточний користувач і його права для UI (<c>GET /api/v1/me</c>).</summary>
/// <param name="UserId">Ідентифікатор у нашій базі.</param>
/// <param name="UserName">Ім'я входу.</param>
/// <param name="Language">Мова інтерфейсу.</param>
/// <param name="Permissions">Функціональні права.</param>
/// <param name="Grants">Ресурсні гранти: <c>"{ResourceKind}:{Id}"</c> → рівень.</param>
/// <param name="Denies">Явні заборони.</param>
/// <param name="IsSimulation">
/// Сеанс симуляції «очима користувача» (ФВ-6.16a).
/// </param>
/// <param name="SimulatedForUserId">Кого симулюють; <c>null</c> — не симуляція.</param>
public sealed record CurrentUserView(
    int UserId,
    string? UserName,
    string Language,
    IReadOnlyList<string> Permissions,
    IReadOnlyDictionary<string, string> Grants,
    IReadOnlyList<string> Denies,
    bool IsSimulation,
    int? SimulatedForUserId);

/// <summary>
/// Компактна проєкція профілю доступу для клієнта.
/// </summary>
/// <remarks>
/// Клієнт має знати права **наперед**, щоб не показувати кнопки, які все одно
/// дадуть 403. Це не заміна перевіркам на сервері: UI ховає, сервер забороняє.
/// </remarks>
public sealed class GetCurrentUserHandler(IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Повертає профіль поточного користувача.</summary>
    /// <param name="ct">Токен скасування.</param>
    public async Task<CurrentUserView> HandleAsync(CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Сесія не містить користувача.");

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        return new CurrentUserView(
            userId,
            currentUser.UserName,
            currentUser.Language,
            [.. profile.Permissions.OrderBy(p => p, StringComparer.Ordinal)],
            profile.Grants.ToDictionary(g => g.Key, g => g.Value.ToString(), StringComparer.Ordinal),
            [.. profile.Denies.OrderBy(d => d, StringComparer.Ordinal)],

            // ⚠ Ознака симуляції йде клієнтові ОБОВ'ЯЗКОВО. Адміністратор,
            // який забув, що дивиться чужими правами, ухвалює рішення про
            // чужий доступ, бачачи не свої можливості (ФВ-6.16a).
            profile.IsSimulation,
            profile.SimulatedForUserId);
    }

    /// <summary>Рівень гранта на проєкт — для швидкої перевірки на клієнті.</summary>
    /// <param name="view">Проєкція профілю.</param>
    /// <param name="projectId">Проєкт.</param>
    /// <remarks>
    /// Саме правило («заборона виграє, інакше грант, інакше None») тут не
    /// реалізується — воно живе рівно в одному місці,
    /// <see cref="AccessProfile.Resolve"/> (<c>Q-188</c>). Тут лише
    /// адаптація: <c>CurrentUserView</c> — серіалізована у рядки форма
    /// (потрібна для JSON-відповіді клієнту), тож грант доводиться
    /// розпарсити назад в <see cref="GrantLevel"/> перед передачею в
    /// спільне рішення.
    /// </remarks>
    public static GrantLevel LevelForProject(CurrentUserView view, int projectId)
    {
        ArgumentNullException.ThrowIfNull(view);

        var key = $"{ResourceKind.Project}:{projectId}";

        var grant = view.Grants.TryGetValue(key, out var raw) && Enum.TryParse<GrantLevel>(raw, out var parsed)
            ? (GrantLevel?)parsed
            : null;

        return AccessProfile.Resolve(view.Denies.Contains(key), grant);
    }
}
