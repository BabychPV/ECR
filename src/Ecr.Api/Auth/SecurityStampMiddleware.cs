using System.Globalization;
using System.Security.Claims;
using Ecr.Api.Errors;
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

        await next(context).ConfigureAwait(false);
    }
}
