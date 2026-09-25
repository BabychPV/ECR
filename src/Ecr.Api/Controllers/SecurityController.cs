using System.Globalization;
using System.Security.Claims;
using Ecr.Api.Auth;
using Ecr.Application.Common;
using Ecr.Application.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Ролі, користувачі, симуляція, зміна пароля.</summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class SecurityController(
    ListRolesHandler listRoles,
    ListPermissionsHandler listPermissions,
    CreateRoleHandler createRole,
    RenameRoleHandler renameRole,
    DeleteRoleHandler deleteRole,
    CloneRoleHandler cloneRole,
    ListUsersHandler listUsers,
    CreateUserHandler createUser,
    StartSimulationHandler startSimulation,
    EndSimulationHandler endSimulation,
    ChangePasswordHandler changePassword,
    Ecr.Application.Security.ListResourceGrantsHandler listGrants,
    Ecr.Application.Security.ReplaceResourceGrantsHandler replaceGrants,
    Ecr.Application.Security.SetReceivesAlertsHandler setAlerts,
    Ecr.Application.Security.ListUserRolesHandler listUserRoles,
    Ecr.Application.Security.ReplaceUserRolesHandler replaceRoles,
    Ecr.Application.Security.SetUserEmailHandler setEmail,
    Ecr.Application.Security.GetAccessDiagnosticsHandler accessDiagnostics,
    Ecr.Application.Security.ResetUserPasswordHandler resetPassword,
    Ecr.Application.Security.SetUserLockHandler setLock,
    Ecr.Domain.Abstractions.IClock clock,
    ICurrentUser currentUser) : ControllerBase
{
    /// <summary>
    /// Звідки взялися (або не взялися) ролі ВЛАСНОГО запису. Права не потребує.
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Ендпоінт існує тому, що збій рольової моделі на живому домені
    /// <b>не відрізняється від справної системи</b>: людина входить, бачить
    /// порожні переліки і вважає, що даних немає. Тут вона бачить свій SID,
    /// усі SID груп зі свого квитка, які з них дали ролі і які — ні.
    ///
    /// ⚠ Права не потребує НАВМИСНО: вимагати <c>Security.ManageUsers</c> на
    /// власні групи означало б лишити без відповіді саме тих, заради кого
    /// маршрут заведений, — рядових співробітників без жодного права. Чужі
    /// SID цим шляхом не віддаються: суб'єкт завжди сам викликач.
    /// </remarks>
    [HttpGet("security/my-groups")]
    [ProducesResponseType<Ecr.Application.Security.AccessDiagnosticsView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MyGroups(CancellationToken ct)
        => Ok(await accessDiagnostics.HandleAsync(subjectUserId: null, ct).ConfigureAwait(false));

    /// <summary>
    /// Те саме про ЧУЖИЙ запис. Право <c>Security.ManageUsers</c>.
    /// </summary>
    /// <param name="id">Обліковий запис, доступ якого пояснюємо.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Потрібен, щоб адміністратор відповідав на «чому в мене немає
    /// доступу», <b>не заходячи під людиною</b>: симуляція (`ФВ-6.16a`) для
    /// цього завелика — вона пише сеанс в аудит і показує чужі дані, тоді як
    /// питання стосується самих лише призначень.
    ///
    /// ⛔ Членство в групах приходить із квитка (`ФВ-6.15a`), а квитка чужої
    /// сесії в нас немає (`P-02`) — відповідь каже про це прямо
    /// (<c>groupsFromTicket: false</c>) і натомість перелічує, які групи
    /// взагалі щось дають. Мовчазний порожній перелік читався б як «людина ні
    /// в яких групах не перебуває», і це була б неправда.
    /// </remarks>
    [HttpGet("security/users/{id:int}/groups")]
    [ProducesResponseType<Ecr.Application.Security.AccessDiagnosticsView>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UserGroups(int id, CancellationToken ct)
        => Ok(await accessDiagnostics.HandleAsync(id, ct).ConfigureAwait(false));

    /// <summary>Перелік ролей. Право <c>Security.ManageRoles</c>.</summary>
    [HttpGet("roles")]
    [ProducesResponseType<IReadOnlyList<Ecr.Application.Security.RoleView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListRoles(CancellationToken ct)
        // Небезпечні права віддаються ОКРЕМИМ списком: у складені ролі вони не
        // входять навмисно, і адміністратор має бачити різницю (ФВ-6.12, D-40).
        => Ok(await listRoles.HandleAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// Повний каталог системних прав, включно з тими, яких не має жодна роль.
    /// Право <c>Security.ManageRoles</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ До появи цього маршруту клієнт складав перелік прав перетином того,
    /// що вже оголошено в наявних ролях: право без жодного носія не можна було
    /// призначити НІКОМУ — форма створення ролі його просто не показувала
    /// (директива №11, трек T2, `#19`).
    /// </remarks>
    [HttpGet("permissions")]
    [ProducesResponseType<IReadOnlyList<Ecr.Application.Security.PermissionCatalogItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListPermissions(CancellationToken ct)
        => Ok(await listPermissions.HandleAsync(ct).ConfigureAwait(false));

    /// <summary>Створює роль. Право <c>Security.ManageRoles</c>.</summary>
    [HttpPost("roles")]
    [ProducesResponseType<Contracts.RoleIdResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var roleId = await createRole
            .HandleAsync(request.Code, request.NameL10n, request.PermissionCodes, ct)
            .ConfigureAwait(false);

        return Created($"/api/v1/roles/{roleId}", new Contracts.RoleIdResponse(roleId));
    }

    /// <summary>
    /// Клонує роль: новий код і копія набору прав. Право <c>Security.ManageRoles</c>.
    /// </summary>
    /// <remarks>⚠ Гранти й призначення не копіюються — лише права.</remarks>
    [HttpPost("roles/{id:int}/clone")]
    [ProducesResponseType<Contracts.RoleIdResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CloneRole(int id, [FromBody] RenameRoleRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var roleId = await cloneRole.HandleAsync(id, request.Code, request.NameL10n, ct).ConfigureAwait(false);

        return Created($"/api/v1/roles/{roleId}", new Contracts.RoleIdResponse(roleId));
    }

    /// <summary>Перейменовує роль. Право <c>Security.ManageRoles</c>.</summary>
    /// <remarks>
    /// ⛔ Вбудована роль із сіду і зайнятий код — <c>409 ECR-SEC-0409</c>.
    /// </remarks>
    [HttpPut("roles/{id:int}/code")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RenameRole(int id, [FromBody] RenameRoleRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await renameRole.HandleAsync(id, request.Code, request.NameL10n, ct).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>Видаляє роль. Право <c>Security.ManageRoles</c>.</summary>
    /// <remarks>
    /// ⛔ Роль із призначеннями чи грантами — <c>409 ECR-SEC-0409</c> з
    /// кількостями в <c>details</c> (<c>assignments</c>, <c>grants</c>);
    /// вбудована роль — той самий код з іншим <c>messageKey</c>.
    /// </remarks>
    [HttpDelete("roles/{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteRole(int id, CancellationToken ct)
    {
        await deleteRole.HandleAsync(id, ct).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Ресурсні гранти ролі. Право <c>Security.ManageRoles</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Без цих двох маршрутів система не показує даних НІКОМУ (`A7-22`):
    /// доступ до проєкту, аркуша чи таблиці вимагає гранта, а створити грант
    /// не було чим. Права відповідають на питання «що людина вміє», гранти —
    /// «до чого саме»; без другої відповіді перша нічого не відкриває.
    /// </remarks>
    [HttpGet("roles/{id:int}/grants")]
    [ProducesResponseType<IReadOnlyList<Ecr.Application.Security.ResourceGrantDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListGrants(int id, CancellationToken ct)
        => Ok(await listGrants.HandleAsync(id, ct).ConfigureAwait(false));

    /// <summary>
    /// Замінює набір грантів ролі цілком. Право <c>Security.ManageRoles</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Саме заміна набору, а не правка по одному: гранти — це відповідь на
    /// питання «що покриває роль», і вона має бути видима одним поглядом.
    /// Часткові правки лишають стан, у якому джерело доступу не відновлюється.
    /// </remarks>
    [HttpPut("roles/{id:int}/grants")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ReplaceGrants(
        int id, [FromBody] ReplaceGrantsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await replaceGrants.HandleAsync(id, request.Grants, ct).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>Перелік користувачів. Право <c>Security.ManageUsers</c>.</summary>
    [HttpGet("users")]
    [ProducesResponseType<Ecr.Application.Common.PagedResult<Ecr.Application.Security.UserView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListUsers(
        [FromQuery] int limit, [FromQuery] string? cursor, CancellationToken ct)
    {
        // ⛔ Перевірка `page.IsValid` переїхала в `ListUsersHandler`: тут стояв
        // `BadRequest(new { error = "limit поза межами 1..N" })` — звичайний
        // JSON повз `ExceptionHandlingMiddleware`, українське речення і жодного
        // коду помилки. Обробник кидає `ECR-REQ-0422` з реченням із каталогу,
        // і контролер лишається тим, чим має бути, — делегуванням.
        var page = new CursorRequest(limit == 0 ? 50 : limit, cursor);

        // ⛔ Хеш пароля і сіль не покидають сховище: у проєкції UserView їх
        // немає за побудовою, а не «не додали» (ФВ-6.11).
        return Ok(await listUsers.HandleAsync(page, clock.UtcNow, ct).ConfigureAwait(false));
    }

    /// <summary>Створює користувача. Право <c>Security.ManageUsers</c>.</summary>
    [HttpPost("users")]
    [ProducesResponseType<Contracts.UserIdResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var provider = string.Equals(request.Provider, "Windows", StringComparison.OrdinalIgnoreCase)
            ? Ecr.Domain.Enums.AuthProvider.Windows
            : Ecr.Domain.Enums.AuthProvider.Local;

        var userId = await createUser
            .HandleAsync(
                request.UserName, request.DisplayName ?? request.UserName, provider,
                request.Sid, request.InitialPassword, request.RoleCodes ?? [], request.Email, ct)
            .ConfigureAwait(false);

        // ⛔ У відповіді немає ні пароля, ні його хеша — лише ідентифікатор.
        return Created($"/api/v1/users/{userId}", new Contracts.UserIdResponse(userId));
    }

    /// <summary>Ролі користувача. Право <c>Security.ManageUsers</c>.</summary>
    [HttpGet("users/{id:int}/roles")]
    [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<string>>> UserRoles(int id, CancellationToken ct)
        => Ok(await listUserRoles.HandleAsync(id, ct).ConfigureAwait(false));

    /// <summary>
    /// Замінює набір ролей користувача. Право <c>Security.ManageUsers</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Способу призначити роль наявному користувачеві не існувало взагалі:
    /// ролі видавалися лише при створенні, а форма створення надсилала
    /// порожній перелік. Обліковий запис виходив працездатним на вигляд і
    /// безправним насправді.
    ///
    /// ⚠ Заміна НАБОРОМ, а не «додати/прибрати»: набір ролей і є
    /// повноваженнями людини, і бачити його треба цілком.
    /// </remarks>
    [HttpPut("users/{id:int}/roles")]
    [ProducesResponseType<Contracts.AffectedRolesResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReplaceUserRoles(
        int id, [FromBody] ReplaceUserRolesRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var count = await replaceRoles
            .HandleAsync(id, request.RoleCodes, request.Validity, ct)
            .ConfigureAwait(false);

        return Ok(new Contracts.AffectedRolesResponse(count));
    }

    /// <summary>
    /// Задає адресу користувача для сповіщень. Право <c>Security.ManageUsers</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Поле існувало від Етапу 3 і не присвоювалося ніде, тож
    /// <c>NotificationJob</c> завжди отримував порожній перелік адресатів —
    /// сповіщення (<c>ФВ-12</c>) не надходили нікому.
    ///
    /// ⚠ Порожня адреса ЗНІМАЄ і прапорець алертів: прапорець без пошти
    /// виглядав би як налаштований адресат, якому нічого не надсилається.
    /// </remarks>
    [HttpPut("users/{id:int}/email")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetUserEmail(
        int id, [FromBody] SetUserEmailRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await setEmail.HandleAsync(id, request.Email, ct).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Вмикає або вимикає отримання алертів. Право <c>Security.ManageUsers</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Адресати алертів — дані, а не конфігурація (`D-125`): перелік у
    /// змінних оточення довелося б міняти розгортанням щоразу, коли хтось іде
    /// у відпустку.
    /// </remarks>
    [HttpPut("users/{id:int}/alerts")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SetAlerts(
        int id, [FromBody] SetAlertsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await setAlerts.HandleAsync(id, request.ReceivesAlerts, ct).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>Скидає пароль локального запису на разовий. Право <c>Security.ManageUsers</c> (BE-12).</summary>
    /// <remarks>
    /// Пароль задає адміністратор; у відповіді його немає. Далі — <c>MustChangePassword</c>,
    /// сесії цілі обриваються. Власний запис і останній адміністратор — <c>409 ECR-SEC-0409</c>,
    /// доменний запис — <c>422 ECR-USR-0422</c>, коротший за політику — <c>422 ECR-PWD-0422</c>.
    /// </remarks>
    [HttpPost("users/{id:int}/reset-password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ResetPassword(
        int id, [FromBody] ResetPasswordRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await resetPassword.HandleAsync(id, request.NewPassword, ct).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Блокує запис безстроково й обриває його сесії. Право <c>Security.ManageUsers</c> (BE-12).</summary>
    /// <remarks>Себе й останнього адміністратора — <c>409 ECR-SEC-0409</c>; без причини — <c>422 ECR-USR-0422</c>.</remarks>
    [HttpPost("users/{id:int}/lock")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> LockUser(int id, [FromBody] UserLockRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await setLock.HandleAsync(id, locked: true, request.Reason, ct).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Знімає блокування — адміністративне чи після невдалих спроб. Право <c>Security.ManageUsers</c> (BE-12).
    /// </summary>
    [HttpPost("users/{id:int}/unlock")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UnlockUser(int id, [FromBody] UserLockRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await setLock.HandleAsync(id, locked: false, request.Reason, ct).ConfigureAwait(false);
        return NoContent();
    }

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
    [ProducesResponseType<Contracts.SimulationSessionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> StartSimulation(
        [FromBody] StartSimulationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Право, симуляція себе і порожня причина перевіряються в обробнику:
        // правило має діяти незалежно від того, звідки його викликали.
        var sessionId = await startSimulation
            .HandleAsync(request.SubjectUserId, request.Reason, ct)
            .ConfigureAwait(false);

        // ⛔ V-06: сеанс прив'язується до ЦЬОГО входу — claim у cookie. Без
        // нього сеанс лише писався в журнал, а наступний же запит (і `/me`)
        // будував профіль адміністратора з усіма правами.
        await ReissueCookieAsync(sessionId).ConfigureAwait(false);

        // ⚠ Клієнт зобов'язаний показувати банер увесь сеанс — саме тому
        // відповідь несе і суб'єкта, і прапорець, а не лише ідентифікатор.
        return Created(
            $"/api/v1/security/simulation/{sessionId}",
            new Contracts.SimulationSessionResponse(sessionId, request.SubjectUserId, ReadOnly: true));
    }

    /// <summary>Завершує власний сеанс симуляції.</summary>
    /// <param name="sessionId">Сеанс; не задано — сеанс цього входу.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpDelete("security/simulation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> EndSimulation([FromQuery] long? sessionId, CancellationToken ct)
    {
        var own = currentUser.SimulationSessionId;
        var target = sessionId ?? own
            ?? throw new Ecr.Application.Errors.NotFoundException(
                "ECR-SIM-0422", "Активного сеансу симуляції не знайдено.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-SIM-0422.noSession" });

        try
        {
            // Чужий сеанс — 403 з обробника: обрив чужого сеансу псує чужий аудит.
            await endSimulation.HandleAsync(target, ct).ConfigureAwait(false);
        }
        catch (Ecr.Application.Errors.NotFoundException) when (target == own)
        {
            // ⚠ Сеанс цього входу вже закритий (наприклад, в іншій вкладці до
            // того, як ця отримала нову cookie): прибрати слід із cookie — і
            // це й є завершення, а не помилка.
        }

        if (target == own)
        {
            await ReissueCookieAsync(sessionId: null).ConfigureAwait(false);
        }

        return NoContent();
    }

    /// <summary>
    /// Перевидає cookie цього входу з сеансом симуляції або без нього (V-06).
    /// </summary>
    /// <remarks>
    /// ⚠ Решта заявок — ті самі: штамп, групи, разовий пароль. Той самий прийом,
    /// що й у <c>SecurityStampMiddleware</c>, який, побачивши вже записану
    /// cookie, сам її не перевидає.
    /// </remarks>
    private Task ReissueCookieAsync(long? sessionId)
    {
        var claims = User.Claims
            .Where(c => c.Type != AuthenticationSetup.SimulationSessionClaim)
            .Select(c => new Claim(c.Type, c.Value))
            .ToList();

        if (sessionId is { } id)
        {
            claims.Add(new Claim(
                AuthenticationSetup.SimulationSessionClaim, id.ToString(CultureInfo.InvariantCulture)));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }

    /// <summary>Зміна власного пароля.</summary>
    /// <param name="request">Чинний і новий пароль.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Доступна навіть під <c>MustChangePassword</c>, коли решта запитів
    /// відхиляється <c>ECR-PWD-0428</c> — інакше користувач не мав би способу
    /// вийти з цього стану.
    /// </remarks>
    [HttpPost("auth/change-password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ⛔ Ні старий, ні новий пароль не потрапляють у лог, трасування чи
        // відповідь (ФВ-6.11): вони існують лише як аргументи цього виклику.
        await changePassword
            .HandleAsync(request.CurrentPassword, request.NewPassword, ct)
            .ConfigureAwait(false);

        // ⚠ SecurityStamp у cookie застарів разом із паролем, і наступний запит
        // отримав би 401 від SecurityStampMiddleware. Явний вихід зрозуміліший
        // за раптову відмову на випадковому екрані.
        await HttpContext
            .SignOutAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
            .ConfigureAwait(false);

        return NoContent();
    }
}

/// <summary>Запит на створення ролі.</summary>
/// <param name="Code">Код ролі.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="PermissionCodes">Права, що входять у роль.</param>
public sealed record CreateRoleRequest(
    string Code, IReadOnlyDictionary<string, string> NameL10n, IReadOnlyList<string> PermissionCodes);

/// <summary>Новий код ролі — для перейменування і для клона.</summary>
/// <param name="Code">Код.</param>
/// <param name="NameL10n">Назва мовами каталогу; без неї перейменування лишає чинну.</param>
public sealed record RenameRoleRequest(string Code, IReadOnlyDictionary<string, string>? NameL10n = null);

/// <summary>Запит на заміну набору ресурсних грантів ролі.</summary>
/// <param name="Grants">Новий набір; порожній прибирає доступ ролі повністю.</param>
public sealed record ReplaceGrantsRequest(
    IReadOnlyList<Ecr.Application.Security.ResourceGrantDto> Grants);

/// <summary>Запит на створення користувача.</summary>
/// <param name="UserName">Ім'я входу.</param>
/// <param name="Provider"><c>Windows</c> або <c>Local</c>.</param>
/// <param name="Sid">SID доменного користувача; <c>null</c> для локального.</param>
/// <param name="DisplayName">Ім'я для показу; типово збігається з іменем входу.</param>
/// <param name="InitialPassword">Разовий пароль локального запису.</param>
/// <param name="RoleCodes">Ролі, які призначити одразу.</param>
/// <param name="Email">Адреса для сповіщень; без неї листи не надходять (`ФВ-12`).</param>
/// <remarks>
/// ⚠ Три останні поля додані понад форму <c>05h</c>: без пароля неможливо
/// створити локальний запис, а без ролей новий користувач не має жодного
/// права — і «створили, але не працює» виглядало б як дефект системи.
/// Позиційний префікс контракту не змінений (<c>D3-18</c>).
/// </remarks>
public sealed record CreateUserRequest(
    string UserName,
    string Provider,
    string? Sid,
    string? DisplayName = null,
    string? InitialPassword = null,
    IReadOnlyList<string>? RoleCodes = null,
    string? Email = null);

/// <summary>Запит на початок симуляції.</summary>
/// <param name="SubjectUserId">Чиїми очима дивимося.</param>
/// <param name="Reason">Причина; потрапляє в <c>aud.SimulationSession</c>.</param>
public sealed record StartSimulationRequest(int SubjectUserId, string Reason);

/// <summary>Запит на зміну пароля.</summary>
/// <param name="CurrentPassword">Поточний пароль.</param>
/// <param name="NewPassword">Новий пароль.</param>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>Адміністративне скидання пароля (BE-12).</summary>
/// <param name="NewPassword">Разовий пароль; власник змінить його при першому вході.</param>
public sealed record ResetPasswordRequest(string NewPassword);

/// <summary>Блокування або розблокування запису (BE-12).</summary>
/// <param name="Reason">Причина; обов'язкова, до 400 символів, іде в журнал безпеки.</param>
public sealed record UserLockRequest(string Reason);

/// <summary>Запит на заміну набору ролей користувача.</summary>
/// <param name="RoleCodes">Коди ролей; порожній набір прибирає всі.</param>
/// <param name="Validity">
/// Межі чинності за кодом ролі (ФВ-6.16) — підміна на час відпустки; код
/// без запису тут або відсутній словник узагалі — роль безстрокова, як і
/// раніше (сумісно з клієнтами, які про це поле не знають).
/// </param>
public sealed record ReplaceUserRolesRequest(
    IReadOnlyList<string> RoleCodes,
    IReadOnlyDictionary<string, Ecr.Application.Security.RoleValidityWindow>? Validity = null);

/// <summary>Запит на зміну адреси користувача.</summary>
/// <param name="Email">Адреса; порожньо — прибрати разом із прапорцем алертів.</param>
public sealed record SetUserEmailRequest(string? Email);

/// <summary>Запит на зміну отримання алертів.</summary>
/// <param name="ReceivesAlerts">Чи отримує людина алерти про збої.</param>
public sealed record SetAlertsRequest(bool ReceivesAlerts);
