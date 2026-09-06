using Ecr.Application.Common;
using Ecr.Application.Errors;

namespace Ecr.Application.Security;

/// <summary>
/// Перевірка функціонального права в обробнику.
/// </summary>
/// <remarks>
/// ⛔ Право перевіряється через <see cref="IAccessDecisionService"/>, а не
/// атрибутом із назвою ролі: перевірка ролі поза єдиною точкою рішення
/// заборонена архітектурним правилом 7. Атрибут бачить лише ім'я політики, а
/// доступ у ECR залежить від ресурсу.
///
/// ⚠ Помічник з'явився після <c>A7-53</c>: дев'ять ендпоінтів оголошували
/// право в контракті й не перевіряли НІЧОГО, крім <c>[Authorize]</c>. Три
/// майже однакові приватні помічники вже існували — у шаблонів, документів і
/// безпеки, — і кожен новий обробник мав вибрати, який із них позичити. Той,
/// хто не вибрав жодного, не перевіряв узагалі, і жоден сторож цього не бачив.
/// </remarks>
public static class PermissionCheck
{
    /// <summary>Вимагає право; повертає профіль, щоб не будувати його двічі.</summary>
    /// <param name="access">Служба рішень про доступ.</param>
    /// <param name="currentUser">Поточний користувач запиту.</param>
    /// <param name="permission">Код права з таблиці ендпоінтів (<c>02-contracts.md</c> §9).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="AccessDeniedException">Анонім або немає права.</exception>
    public static async Task<AccessProfile> RequireAsync(
        IAccessDecisionService access, ICurrentUser currentUser, string permission, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(currentUser);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException("ECR-AUTH-0401", "Потрібна автентифікація.");

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        if (!profile.Has(permission))
        {
            // ⚠ У повідомленні — КОД ПРАВА, а не «недостатньо прав»: інакше
            // адміністратор не знає, що саме видати, і питання приходить до
            // розробника.
            throw new AccessDeniedException("ECR-AUTH-0403", $"Потрібне право {permission}.");
        }

        return profile;
    }
}
