using System.Security.Claims;
using Ecr.Api.Auth;
using Ecr.Application.Security;

namespace Ecr.Api.Middleware;

/// <summary>
/// Доки стоїть <c>MustChangePassword</c>, доступні лише зміна пароля і вихід
/// (ФВ-6.18).
/// </summary>
/// <remarks>
/// ⚠ Стоїть у конвеєрі, а не в кожному контролері. Правило, розкидане по
/// атрибутах, неминуче забудеться на новому маршруті — і разовий пароль
/// bootstrap-адміністратора став би повноцінним безстроковим доступом.
///
/// Саме рішення — у <see cref="PasswordChangeGate"/>: чиста функція, яку можна
/// прогнати без HTTP.
/// </remarks>
public sealed class PasswordChangeMiddleware(RequestDelegate next)
{
    /// <summary>Обробляє запит.</summary>
    /// <param name="context">Контекст запиту.</param>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Анонімні запити не мають прапорця взагалі: до них правило не
        // застосовується, а сторінка входу має працювати.
        if (context.User is { Identity.IsAuthenticated: true })
        {
            var mustChange = string.Equals(
                context.User.FindFirstValue(AuthenticationSetup.MustChangePasswordClaim),
                "1",
                StringComparison.Ordinal);

            PasswordChangeGate.Ensure(mustChange, context.Request.Path.Value);
        }

        return next(context);
    }
}
