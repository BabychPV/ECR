using Ecr.Application.Errors;

namespace Ecr.Application.Security;

/// <summary>
/// Доки стоїть <c>MustChangePassword</c>, доступні лише зміна пароля і вихід
/// (ФВ-6.18).
/// </summary>
/// <remarks>
/// ⚠ Правило записане **чистою функцією** і одним переліком. Розкидане по
/// контролерах, воно неминуче забудеться на новому маршруті — і разовий пароль
/// bootstrap-адміністратора став би повноцінним, безстроковим доступом.
/// </remarks>
public static class PasswordChangeGate
{
    /// <summary>
    /// Маршрути, доступні з непоміненим паролем.
    /// </summary>
    /// <remarks>
    /// Перелік навмисно короткий і закритий. Каталог рядків тут не з
    /// поблажливості: без нього екран зміни пароля показав би сирі ключі
    /// замість підписів полів, і користувач не зрозумів би, чого від нього
    /// хочуть.
    /// </remarks>
    private static readonly string[] Allowed =
    [
        "/api/v1/auth/change-password",
        "/api/v1/auth/logout",
        "/api/v1/auth/me",
        "/api/v1/ui-strings",
    ];

    /// <summary>Чи дозволений маршрут із непоміненим паролем.</summary>
    /// <param name="path">Шлях запиту.</param>
    public static bool IsAllowed(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var allowed in Allowed)
        {
            if (path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Перевіряє запит; кидає, якщо пароль треба змінити.</summary>
    /// <param name="mustChangePassword">Прапорець облікового запису.</param>
    /// <param name="path">Шлях запиту.</param>
    /// <exception cref="BusinessRuleException">Потрібна зміна пароля — <c>ECR-PWD-0428</c>.</exception>
    public static void Ensure(bool mustChangePassword, string? path)
    {
        if (!mustChangePassword || IsAllowed(path))
        {
            return;
        }

        throw new BusinessRuleException(
            "ECR-PWD-0428",
            "Пароль видано разово: доки його не змінено, доступні лише зміна пароля і вихід.");
    }
}
