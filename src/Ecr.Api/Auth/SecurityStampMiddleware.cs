using System.Globalization;
using System.Security.Claims;
using Ecr.Api.Errors;
using Ecr.Domain.Errors;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Ecr.Api.Auth;

/// <summary>
/// Перевіряє <c>SecurityStamp</c> на кожен запит.
/// </summary>
/// <remarks>
/// Сенс саме в негайності: відкликана роль має перестати діяти **до**
/// завершення сесії, а не після закінчення cookie (тест безпеки №2).
/// </remarks>
public sealed class SecurityStampMiddleware(RequestDelegate next)
{
    /// <summary>Обробляє запит.</summary>
    public async Task InvokeAsync(HttpContext context, Ecr.Infrastructure.Security.SecurityStampValidator validator)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(validator);

        // Анонімні запити проходять без перевірки: перевіряти нема чого, а
        // зайвий запит до БД на кожен виклик /health коштував би дорожче.
        if (context.User is not { Identity.IsAuthenticated: true })
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var userIdClaim = context.User.FindFirstValue(AuthenticationSetup.UserIdClaim);
        var stamp = context.User.FindFirstValue(AuthenticationSetup.SecurityStampClaim);

        if (!int.TryParse(userIdClaim, CultureInfo.InvariantCulture, out var userId)
            || string.IsNullOrEmpty(stamp)
            || !await validator.IsCurrentAsync(userId, stamp, context.RequestAborted).ConfigureAwait(false))
        {
            // Cookie підписана нами і формально дійсна, але за нею стоїть
            // відкликаний стан. Вихід обов'язковий: інакше клієнт носитиме
            // мертву cookie до закінчення терміну і отримуватиме 401 на все.
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);

            throw new Application.Errors.AccessDeniedException(
                ErrorCodes.Unauthorized,
                "Сесія втратила чинність: права користувача змінилися.");
        }

        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.OnStarting(() => ReissueIfStampRotatedAsync(context, validator, userId, stamp));
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Дія могла прокрутити штамп самого виконавця (гранти власної ролі,
    /// призначення ролей собі): тоді видаємо йому нову cookie з новим штампом
    /// у тій самій відповіді, а не 401 на наступному запиті. Права в cookie не
    /// лежать — профіль будується за новим штампом, тож звужені права діють одразу.
    /// </summary>
    private static async Task ReissueIfStampRotatedAsync(
        HttpContext context, Ecr.Infrastructure.Security.SecurityStampValidator validator, int userId, string stamp)
    {
        if (context.Response.StatusCode >= 400 || AuthCookieAlreadyWritten(context))
        {
            return; // ендпоінт сам вийшов (зміна пароля) або сам видав cookie
        }

        var current = await validator.ReadCurrentAsync(userId, context.RequestAborted).ConfigureAwait(false);
        if (current is null || string.Equals(current, stamp, StringComparison.Ordinal))
        {
            return;
        }

        var claims = context.User.Claims
            .Where(c => c.Type != AuthenticationSetup.SecurityStampClaim)
            .Select(c => new Claim(c.Type, c.Value))
            .Append(new Claim(AuthenticationSetup.SecurityStampClaim, current));
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity))
                     .ConfigureAwait(false);
    }

    private static bool AuthCookieAlreadyWritten(HttpContext context)
    {
        var name = context.RequestServices
            .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme).Cookie.Name;

        return !string.IsNullOrEmpty(name)
            && context.Response.Headers.SetCookie.Any(v => v?.StartsWith(name, StringComparison.Ordinal) == true);
    }
}
