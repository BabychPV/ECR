using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Вхід і вихід. Два провайдери, одна cookie.</summary>
[ApiController]
[Route("api/v1")]
public sealed class AuthController : ControllerBase
{
    /// <summary>Вхід доменного користувача.</summary>
    /// <remarks>
    /// **Єдиний** endpoint, де застосовується Negotiate: на решті IIS має
    /// анонімний доступ, інакше локальні користувачі не увійдуть узагалі.
    /// </remarks>
    [HttpPost("login/windows")]
    [Authorize(AuthenticationSchemes = NegotiateDefaults.AuthenticationScheme)]
    public Task<IActionResult> LoginWindows(CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: узяти SID із WindowsIdentity; знайти або створити sec.User із Provider = Windows; " +
            "підписати ТУ САМУ cookie, що й локальний вхід, із claims: userId, userName, securityStamp; " +
            "записати aud.SecurityEvent і sec.LoginAttempt.");

    /// <summary>Вхід локального користувача.</summary>
    [HttpPost("login/local")]
    [AllowAnonymous]
    public Task<IActionResult> LoginLocal([FromBody] LocalLoginRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: знайти користувача з Provider = Local; перевірити LockedUntil; " +
            "звірити пароль через IPasswordHasher; при невдачі — інкремент FailedAttempts і, " +
            "за політикою, блокування (ECR-AUTH-0423); при успіху — скинути лічильник і " +
            "підписати cookie. " +
            "⚠ Повідомлення про помилку однакове для 'немає користувача' і 'невірний пароль' — " +
            "інакше endpoint стає засобом перебору імен.");

    /// <summary>Вихід.</summary>
    [HttpPost("logout")]
    [Authorize]
    public Task<IActionResult> Logout(CancellationToken ct)
        => throw new NotImplementedException("TODO: SignOutAsync і запис події.");

    /// <summary>Поточний користувач і його права для UI.</summary>
    [HttpGet("me")]
    [Authorize]
    public Task<IActionResult> Me(CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: повернути userId, ім'я, мову і КОМПАКТНУ проєкцію AccessProfile — " +
            "функціональні права і гранти. Клієнт має знати їх наперед, щоб не показувати " +
            "кнопки, які все одно дадуть 403.");
}

/// <summary>Запит локального входу.</summary>
public sealed record LocalLoginRequest(string UserName, string Password);
