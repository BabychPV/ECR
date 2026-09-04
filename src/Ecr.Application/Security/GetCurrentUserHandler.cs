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
public sealed record CurrentUserView(
    int UserId,
    string? UserName,
    string Language,
    IReadOnlyList<string> Permissions,
    IReadOnlyDictionary<string, string> Grants,
    IReadOnlyList<string> Denies);

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
            [.. profile.Denies.OrderBy(d => d, StringComparer.Ordinal)]);
    }

    /// <summary>Рівень гранта на проєкт — для швидкої перевірки на клієнті.</summary>
    /// <param name="view">Проєкція профілю.</param>
    /// <param name="projectId">Проєкт.</param>
    public static GrantLevel LevelForProject(CurrentUserView view, int projectId)
    {
        ArgumentNullException.ThrowIfNull(view);

        var key = $"{ResourceKind.Project}:{projectId}";

        // Заборона виграє на будь-якому рівні (ФВ-6.6) — і на клієнті теж, бо
        // інакше UI показував би доступним те, що сервер відхилить.
        if (view.Denies.Contains(key))
        {
            return GrantLevel.None;
        }

        return view.Grants.TryGetValue(key, out var level) && Enum.TryParse<GrantLevel>(level, out var parsed)
            ? parsed
            : GrantLevel.None;
    }
}
