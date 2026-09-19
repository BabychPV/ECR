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
    ///
    /// ⛔ Шляхи мусять збігатися з реальними маршрутами ДОСЛІВНО. До `A7-14`
    /// тут стояли <c>/api/v1/auth/logout</c> і <c>/api/v1/auth/me</c>, яких на
    /// сервері немає: контролер віддає <c>/api/v1/logout</c> і
    /// <c>/api/v1/me</c>. Через це список не дозволяв нічого, і разовий пароль
    /// закривав систему НАЗАВЖДИ — включно з екраном, який єдиний міг його
    /// зняти. Дефект невидимий за побудовою: помилковий шлях не «падає», він
    /// просто не збігається.
    ///
    /// ⚠ <c>/api/v1/me</c> у переліку не з поблажливості: клієнт саме з нього
    /// дізнається, що треба на зміну пароля. Без нього застосунок вважає сеанс
    /// недійсним і кидає на вхід, звідки знову потрапляє сюди.
    /// </remarks>
    private static readonly string[] Allowed =
    [
        "/api/v1/auth/change-password",
        "/api/v1/logout",
        "/api/v1/me",
        "/api/v1/ui-strings",
    ];

    /// <summary>Перелік дозволених шляхів; відкритий для сторожа архітектури.</summary>
    public static IReadOnlyList<string> AllowedPaths => Allowed;

    /// <summary>Чи дозволений маршрут із непоміненим паролем.</summary>
    /// <param name="path">Шлях запиту.</param>
    /// <remarks>
    /// ⛔ Порівняння — на **межі сегмента шляху**, не голим префіксом. Голий
    /// <c>StartsWith</c> тут був дірою в безпеці: <c>/api/v1/methodologies</c>
    /// починається на <c>/api/v1/me</c> («me» в «methodologies»), тож разовий
    /// пароль відкривав повний CRUD над методологією розрахунків — формули,
    /// константи, <c>publish</c>, <c>simulate</c> — ще до того, як користувач
    /// довів, що знає власний пароль. Дефект невидимий за побудовою: жоден
    /// маршрут не «падає», просто дозволяється зайвий.
    /// </remarks>
    public static bool IsAllowed(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var allowed in Allowed)
        {
            if (!path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Точний збіг або наступний символ — межа: підшлях (`/`), запит
            // (`?`), фрагмент (`#`). Інакше це інший маршрут, що просто
            // починається з тих самих літер.
            if (path.Length == allowed.Length || path[allowed.Length] is '/' or '?' or '#')
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
            "Пароль видано разово: доки його не змінено, доступні лише зміна пароля і вихід.",
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-PWD-0428.oneTimePassword" });
    }
}
