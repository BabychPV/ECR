using Ecr.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Ролі, користувачі, симуляція, зміна пароля.</summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class SecurityController(
    StartSimulationHandler startSimulation,
    EndSimulationHandler endSimulation,
    ChangePasswordHandler changePassword) : ControllerBase
{
    /// <summary>Перелік ролей. Право <c>Security.ManageRoles</c>.</summary>
    [HttpGet("roles")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<IActionResult> ListRoles(CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: ролі з їхніми правами; небезпечні права (IsDangerous) позначати окремо — " +
            "у складені ролі вони не входять навмисно (ФВ-6.12, D-40).");

    /// <summary>Створює роль. Право <c>Security.ManageRoles</c>.</summary>
    [HttpPost("roles")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public Task<IActionResult> CreateRole([FromBody] CreateRoleRequest request, CancellationToken ct)
        => throw new NotImplementedException("TODO: перевірити Security.ManageRoles; створити sec.Role.");

    /// <summary>Перелік користувачів. Право <c>Security.ManageUsers</c>.</summary>
    [HttpGet("users")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<IActionResult> ListUsers([FromQuery] int limit, [FromQuery] string? cursor, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: курсорна пагінація. ⚠ Хеш пароля і сіль не повертати ніколи (ФВ-6.11).");

    /// <summary>Створює користувача. Право <c>Security.ManageUsers</c>.</summary>
    [HttpPost("users")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public Task<IActionResult> CreateUser([FromBody] CreateUserRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: локальний користувач створюється з MustChangePassword = 1; " +
            "доменний — за SID, без пароля взагалі.");

    /// <summary>
    /// Починає сеанс симуляції. Право <c>Security.Simulate</c>.
    /// </summary>
    /// <remarks>
    /// Сеанс пишеться в <c>aud.SimulationSession</c> <b>одразу</b> і не
    /// батчиться: без запису «подивитися очима» стало б способом безслідно
    /// переглянути чужі дані. Будь-який запис під симуляцією відхиляється
    /// <c>EditDenyReason.SimulationReadOnly</c> — навіть із правом
    /// <c>Manage</c> (ФВ-6.16a).
    /// </remarks>
    [HttpPost("security/simulation")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> StartSimulation([FromBody] StartSimulationRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перевірити Security.Simulate; симуляція себе → ECR-SIM-0422; " +
            "делегувати startSimulation.HandleAsync(request.SubjectUserId, request.Reason, ct).");

    /// <summary>Завершує власний сеанс симуляції.</summary>
    [HttpDelete("security/simulation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> EndSimulation([FromQuery] long sessionId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: завершити ЛИШЕ власний сеанс; чужий — 403.");

    /// <summary>Зміна власного пароля.</summary>
    /// <remarks>
    /// Доступна навіть під <c>MustChangePassword</c>, коли решта запитів
    /// відхиляється <c>ECR-PWD-0428</c> — інакше користувач не мав би способу
    /// вийти з цього стану.
    /// </remarks>
    [HttpPost("auth/change-password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: делегувати changePassword.HandleAsync(request.CurrentPassword, request.NewPassword, ct). " +
            "⚠ Ні старий, ні новий пароль не потрапляють у лог, трасування чи відповідь (ФВ-6.11).");
}

/// <summary>Запит на створення ролі.</summary>
/// <param name="Code">Код ролі.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="PermissionCodes">Права, що входять у роль.</param>
public sealed record CreateRoleRequest(
    string Code, IReadOnlyDictionary<string, string> NameL10n, IReadOnlyList<string> PermissionCodes);

/// <summary>Запит на створення користувача.</summary>
/// <param name="UserName">Ім'я входу.</param>
/// <param name="Provider"><c>Windows</c> або <c>Local</c>.</param>
/// <param name="Sid">SID доменного користувача; <c>null</c> для локального.</param>
public sealed record CreateUserRequest(string UserName, string Provider, string? Sid);

/// <summary>Запит на початок симуляції.</summary>
/// <param name="SubjectUserId">Чиїми очима дивимося.</param>
/// <param name="Reason">Причина; потрапляє в <c>aud.SimulationSession</c>.</param>
public sealed record StartSimulationRequest(int SubjectUserId, string Reason);

/// <summary>Запит на зміну пароля.</summary>
/// <param name="CurrentPassword">Поточний пароль.</param>
/// <param name="NewPassword">Новий пароль.</param>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
