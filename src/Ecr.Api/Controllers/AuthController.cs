using System.Globalization;
using System.Security.Claims;
using Ecr.Api.Auth;
using Ecr.Application.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Вхід і вихід. Два провайдери, одна cookie.</summary>
[ApiController]
[Route("api/v1")]
public sealed class AuthController(
    LoginHandler login,
    GetCurrentUserHandler currentUserHandler) : ControllerBase
{
    /// <summary>Вхід доменного користувача.</summary>
    /// <remarks>
    /// **Єдиний** endpoint, де застосовується Negotiate: на решті IIS має
    /// анонімний доступ, інакше локальні користувачі не увійдуть узагалі.
    /// </remarks>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost("login/windows")]
    [Authorize(AuthenticationSchemes = NegotiateDefaults.AuthenticationScheme)]
    public async Task<IActionResult> LoginWindows(CancellationToken ct)
    {
        // SID — це зіставлення з каталогом, а не авторство: автором дії в
        // аудиті завжди лишається наш UserId (D-37, D-86).
        var sid = User.FindFirstValue(ClaimTypes.PrimarySid)
                  ?? User.FindFirstValue(ClaimTypes.Sid);

        if (string.IsNullOrEmpty(sid))
        {
            return Unauthorized();
        }

        var userName = User.Identity?.Name ?? sid;
        var result = await login
            .HandleWindowsAsync(sid, userName, userName, RemoteIp, ct)
            .ConfigureAwait(false);

        await SignInAsync(result).ConfigureAwait(false);
        return Ok(Describe(result));
    }

    /// <summary>Вхід локального користувача.</summary>
    /// <param name="request">Ім'я і пароль.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost("login/local")]
    [AllowAnonymous]
    public async Task<IActionResult> LoginLocal([FromBody] LocalLoginRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ⛔ Пароль існує лише як аргумент цього виклику: ні в лог, ні в
        // трасування, ні у відповідь він не потрапляє (ФВ-6.11).
        var result = await login
            .HandleAsync(request.UserName, request.Password, RemoteIp, ct)
            .ConfigureAwait(false);

        await SignInAsync(result).ConfigureAwait(false);
        return Ok(Describe(result));
    }

    /// <summary>Вихід.</summary>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)
                         .ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Поточний користувач і його права для UI.</summary>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Клієнт має знати права наперед, щоб не показувати кнопки, які все одно
    /// дадуть 403.
    /// </remarks>
    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var view = await currentUserHandler.HandleAsync(ct).ConfigureAwait(false);

        // Прапорець «пароль виданий разово» — стан СЕСІЇ, а не профілю прав:
        // він живе в cookie і знімається лише зміною пароля.
        return Ok(new
        {
            view.UserId,
            view.UserName,
            view.Language,
            mustChangePassword = MustChangePassword,
            view.Permissions,
            view.Grants,
            view.Denies,
        });
    }

    /// <summary>Чи стоїть вимога змінити пароль у поточній cookie.</summary>
    private bool MustChangePassword
        => string.Equals(
            User.FindFirstValue(AuthenticationSetup.MustChangePasswordClaim),
            "1",
            StringComparison.Ordinal);

    private string? RemoteIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    /// <summary>Підписує **ту саму** cookie для обох провайдерів (ФВ-6.1).</summary>
    private Task SignInAsync(LoginResult result)
    {
        var claims = new List<Claim>
        {
            new(AuthenticationSetup.UserIdClaim, result.UserId.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, result.UserName),
            new(AuthenticationSetup.SecurityStampClaim, result.SecurityStamp),
        };

        if (result.MustChangePassword)
        {
            claims.Add(new Claim(AuthenticationSetup.MustChangePasswordClaim, "1"));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

        return HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }

    private static object Describe(LoginResult result)
        => new
        {
            userId = result.UserId,
            userName = result.UserName,
            displayName = result.DisplayName,
            mustChangePassword = result.MustChangePassword,
        };
}

/// <summary>Запит локального входу.</summary>
public sealed record LocalLoginRequest(string UserName, string Password);
