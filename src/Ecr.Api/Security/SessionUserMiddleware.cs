using System.Globalization;
using System.Security.Claims;
using Ecr.Api.Auth;
using Ecr.Application.Errors;
using Ecr.Domain.Errors;

namespace Ecr.Api.Security;

/// <summary>
/// Відмова в небезпечному запиті вкладки, яка вважає себе ІНШИМ користувачем, ніж власник cookie
/// (<c>ECR-AUTH-0409</c>, AN-108 / S2-05).
/// </summary>
/// <remarks>
/// ⛔ Предмет. Cookie сеансу спільна для всіх вкладок браузера. Після входу користувача B у сусідній
/// вкладці автозбереження чи маячок <c>beforeunload</c> вкладки A везли утримані правки A вже з cookie B,
/// і журнал правок приписував B чужі значення. Клієнт закриває це сам (<c>sessionChannel</c>, звірка
/// <c>/me</c>), але обидва рубежі клієнтські: сповіщення може не дійти, а маячок іде в мить вивантаження.
/// Тут — серверний рубіж: вкладка шле заголовок <see cref="HeaderName"/> з id користувача, якого вона
/// бачила в <c>/me</c>; розбіжність з <c>ecr:uid</c> cookie — відмова ДО обробника.
///
/// ⚠ Правило:
/// <list type="bullet">
/// <item>лише <c>POST</c>/<c>PUT</c>/<c>PATCH</c>/<c>DELETE</c> (як <see cref="CsrfOriginMiddleware"/>);</item>
/// <item>заголовка немає (порожній) — пропускається: зворотна сумісність, скрипти, служби, <c>tools/land</c>;</item>
/// <item>запит без автентифікації — не тут: відмовить авторизація (<c>401</c>);</item>
/// <item>вхід і вихід не перевіряються: вони і є легальна зміна користувача;</item>
/// <item>значення не є числом або не збігається з <c>ecr:uid</c> — <c>409 ECR-AUTH-0409</c>.</item>
/// </list>
///
/// ⚠ Симуляція «очима користувача» не заважає: <c>/me</c> віддає <c>userId</c> власника cookie
/// (ціль — окремими полями), і <c>ecr:uid</c> під сеансом симуляції не змінюється.
/// Стоїть одразу після <see cref="CsrfOriginMiddleware"/>: cookie лише прочитано, нічого не змінено.
/// </remarks>
public sealed class SessionUserMiddleware(RequestDelegate next)
{
    /// <summary>Заголовок з id користувача, якого бачила вкладка.</summary>
    public const string HeaderName = "X-Ecr-User";

    /// <summary>Ключ причини відмови в каталозі рядків.</summary>
    public const string MessageKey = "err.ECR-AUTH-0409.sessionUserChanged";

    /// <summary>Шляхи, які не перевіряються: легальна зміна користувача.</summary>
    private static readonly string[] Exempt =
    [
        "/api/v1/logout",
        "/api/v1/login/local",
        "/api/v1/login/windows",
    ];

    /// <summary>Перевіряє запит.</summary>
    /// <param name="context">Контекст запиту.</param>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Request;
        if (IsStateChanging(request.Method)
            && context.User is { Identity.IsAuthenticated: true } user
            && !Exempt.Contains(request.Path.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            && IsMismatch(request.Headers[HeaderName].ToString(), user.FindFirstValue(AuthenticationSetup.UserIdClaim)))
        {
            throw new BusinessRuleException(
                ErrorCodes.SessionUserMismatch,
                "Сеанс браузера належить іншому користувачу; запит вкладки відхилено.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = MessageKey,
                });
        }

        return next(context);
    }

    /// <summary><c>true</c> — заголовок є і називає не власника cookie.</summary>
    /// <param name="claimed">Значення заголовка; порожнє — заголовка немає.</param>
    /// <param name="cookieUserId">Значення <c>ecr:uid</c> cookie.</param>
    internal static bool IsMismatch(string claimed, string? cookieUserId)
    {
        if (string.IsNullOrWhiteSpace(claimed))
        {
            return false;
        }

        return !int.TryParse(claimed.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var tabUserId)
               || !int.TryParse(cookieUserId, NumberStyles.None, CultureInfo.InvariantCulture, out var ownerId)
               || tabUserId != ownerId;
    }

    private static bool IsStateChanging(string method)
        => HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
            || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
}
