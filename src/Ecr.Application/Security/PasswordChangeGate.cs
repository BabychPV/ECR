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

    /// <summary>Префікс каталогу рядків — із нього дозволене лише ЧИТАННЯ однієї мови.</summary>
    private const string UiStrings = "/api/v1/ui-strings";

    /// <summary>
    /// Літеральні маршрути під <see cref="UiStrings"/>, які шаблон <c>{lang}</c>
    /// не перекриває: адміністративні зрізи каталогу, а не тексти екрана.
    /// </summary>
    private static readonly string[] UiStringsAdminSegments = ["coverage", "import", "export.csv"];

    /// <summary>Перелік дозволених шляхів; відкритий для сторожа архітектури.</summary>
    public static IReadOnlyList<string> AllowedPaths => Allowed;

    /// <summary>Чи дозволений запит із непоміненим паролем.</summary>
    /// <param name="method">HTTP-метод запиту.</param>
    /// <param name="path">Шлях запиту.</param>
    /// <remarks>
    /// ⛔ S16: під префіксом <c>/api/v1/ui-strings</c> живуть не лише тексти
    /// екрана, а й ЗАПИС каталогу — <c>PUT {lang}/{key}</c> і
    /// <c>POST import</c>. Префікс без методу пускав туди власника разового
    /// пароля, тобто того, хто ще не довів, що знає пароль (разовий знає й
    /// адміністратор, що його видав). Екранові зміни пароля потрібне рівно
    /// <c>GET /api/v1/ui-strings/{lang}</c> (<c>shared/i18n</c>) — лише воно
    /// й дозволене; решта маршрутів переліку — без обмеження методу, як і
    /// досі.
    ///
    /// ⛔ Порівняння — на **межі сегмента шляху**, не голим префіксом. Голий
    /// <c>StartsWith</c> тут був дірою в безпеці: <c>/api/v1/methodologies</c>
    /// починається на <c>/api/v1/me</c> («me» в «methodologies»), тож разовий
    /// пароль відкривав повний CRUD над методологією розрахунків — формули,
    /// константи, <c>publish</c>, <c>simulate</c> — ще до того, як користувач
    /// довів, що знає власний пароль. Дефект невидимий за побудовою: жоден
    /// маршрут не «падає», просто дозволяється зайвий.
    /// </remarks>
    public static bool IsAllowed(string? method, string? path)
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
                return string.Equals(allowed, UiStrings, StringComparison.Ordinal)
                    ? IsCatalogRead(method, path)
                    : true;
            }
        }

        return false;
    }

    /// <summary>
    /// Чи це <c>GET /api/v1/ui-strings/{lang}</c> — читання текстів однієї мови.
    /// </summary>
    /// <param name="method">HTTP-метод.</param>
    /// <param name="path">Шлях, що вже починається з <see cref="UiStrings"/> на межі сегмента.</param>
    private static bool IsCatalogRead(string? method, string path)
    {
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = path[UiStrings.Length..];
        var end = rest.IndexOfAny(['?', '#']);
        if (end >= 0)
        {
            rest = rest[..end];
        }

        // Рівно ОДИН непорожній сегмент після префікса — мова. Сам префікс
        // (перелік бракуючих ключів) і `{lang}/{key}` сюди не проходять.
        if (rest.Length < 2 || rest[0] != '/')
        {
            return false;
        }

        var segment = rest[1..];
        return !segment.Contains('/', StringComparison.Ordinal)
            && !UiStringsAdminSegments.Contains(segment, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Перевіряє запит; кидає, якщо пароль треба змінити.</summary>
    /// <param name="mustChangePassword">Прапорець облікового запису.</param>
    /// <param name="method">HTTP-метод запиту.</param>
    /// <param name="path">Шлях запиту.</param>
    /// <exception cref="BusinessRuleException">Потрібна зміна пароля — <c>ECR-PWD-0428</c>.</exception>
    public static void Ensure(bool mustChangePassword, string? method, string? path)
    {
        if (!mustChangePassword || IsAllowed(method, path))
        {
            return;
        }

        throw new BusinessRuleException(
            "ECR-PWD-0428",
            "Пароль видано разово: доки його не змінено, доступні лише зміна пароля і вихід.",
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-PWD-0428.oneTimePassword" });
    }
}
