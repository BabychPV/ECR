// src/Ecr.Api/Controllers/SmtpSettingsController.cs
using Ecr.Application.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Налаштування SMTP, задані адміністратором (<c>D-256</c>). Право <c>System.ManageNotifications</c>.
/// ⛔ Пароль write-only: приймається в PUT, у жодній відповіді його немає — лише <c>hasPassword</c>.
/// </summary>
[ApiController]
[Route("api/v1/notifications/smtp")]
[Authorize]
public sealed class SmtpSettingsController(
    GetSmtpSettingsHandler get, SaveSmtpSettingsHandler save, TestSmtpSettingsHandler test) : ControllerBase
{
    /// <summary>Поточні налаштування (без пароля).</summary>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet]
    [ProducesResponseType<SmtpSettingsView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct)
        => Ok(await get.HandleAsync(ct).ConfigureAwait(false));

    /// <summary>Замінює налаштування; порожній пароль — не змінювати.</summary>
    /// <param name="request">Нові значення.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPut]
    [ProducesResponseType<SmtpSettingsView>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Put([FromBody] SmtpSettingsInput request, CancellationToken ct)
        => Ok(await save.HandleAsync(request, ct).ConfigureAwait(false));

    /// <summary>Пробний лист на вказану адресу через ефективні налаштування.</summary>
    /// <param name="request">Адресат.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost("test")]
    [ProducesResponseType<NotificationTestResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Test([FromBody] SmtpTestRequest request, CancellationToken ct)
        => Ok(await test.HandleAsync(request, ct).ConfigureAwait(false));
}