using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Security;

/// <summary>Роль із її правами.</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="Code">Код.</param>
/// <param name="IsBuiltIn">Вбудована роль із seed: видаленню не підлягає.</param>
/// <param name="IsActive">Чи діє.</param>
/// <param name="Permissions">Права ролі.</param>
/// <param name="DangerousPermissions">
/// Небезпечні права серед них — показуються окремо, бо їх видають поіменно.
/// </param>
public sealed record RoleView(
    int Id,
    string Code,
    bool IsBuiltIn,
    bool IsActive,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> DangerousPermissions);

/// <summary>Обліковий запис у переліку.</summary>
/// <remarks>⛔ Ні хеша пароля, ні солі, ні <c>SecurityStamp</c> тут немає (ФВ-6.11).</remarks>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="UserName">Ім'я входу.</param>
/// <param name="DisplayName">Ім'я для показу.</param>
/// <param name="Provider">Провайдер входу.</param>
/// <param name="IsActive">Чи діє запис.</param>
/// <param name="IsBootstrapAdmin">Технічний запис первинного налаштування.</param>
/// <param name="MustChangePassword">Пароль виданий разово.</param>
/// <param name="IsLockedOut">Заблокований після невдалих спроб.</param>
public sealed record UserView(
    int Id,
    string UserName,
    string DisplayName,
    AuthProvider Provider,
    bool IsActive,
    bool IsBootstrapAdmin,
    bool MustChangePassword,
    bool IsLockedOut);

/// <summary>Перелік ролей із правами. Право <c>Security.ManageRoles</c>.</summary>
public sealed class ListRolesHandler(IUserStore users, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право, без якого перелік ролей не віддається.</summary>
    public const string Permission = "Security.ManageRoles";

    /// <summary>Повертає ролі з їхніми правами.</summary>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<RoleView>> HandleAsync(CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException("ECR-AUTH-0401", "Потрібна автентифікація.");

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        if (!profile.Has(Permission))
        {
            throw new AccessDeniedException("ECR-AUTH-0403", $"Потрібне право {Permission}.");
        }

        return await users.ListRolesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Створення ролі. Право <c>Security.ManageRoles</c>.</summary>
public sealed class CreateRoleHandler(
    IUserStore users,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Створює роль із набором прав.</summary>
    /// <param name="code">Код ролі.</param>
    /// <param name="name">Назва мовами каталогу.</param>
    /// <param name="permissionCodes">Права ролі.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ідентифікатор створеної ролі.</returns>
    public async Task<int> HandleAsync(
        string code,
        IReadOnlyDictionary<string, string> name,
        IReadOnlyList<string> permissionCodes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(permissionCodes);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException("ECR-AUTH-0401", "Потрібна автентифікація.");

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        if (!profile.Has(ListRolesHandler.Permission))
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {ListRolesHandler.Permission}.");
        }

        // ⚠ Небезпечні права можна видати ЛИШЕ маючи їх самому. Інакше
        // `Security.ManageRoles` перетворюється на право видати собі будь-що —
        // тобто на єдине право, яке має значення (ФВ-6.12, D-40).
        //
        // ⛔ Єдиний виняток — bootstrap-адміністратор, і без нього правило
        // не суворе, а замкнене на себе (`A7-17`). У щойно розгорнутій системі
        // небезпечних прав не має НІХТО: seed не дає їх жодній
        // вбудованій ролі навмисно. Значить, першого разу їх не може видати
        // ніхто ніколи, і `Calculation.Publish`, `Integration.Manage`,
        // `Security.Simulate`, `System.RunJob` лишаються недосяжними в
        // будь-якому розгортанні.
        //
        // ⚠ Виняток дає право ВИДАТИ, а не МАТИ: bootstrap-запис сам
        // не отримує ні `Calculation.Publish`, ні `Security.Simulate` — він лише
        // називає людину, яка їх матиме. Це дослівно те, чого вимагає
        // ФВ-6.12: «видаються іменованим особам окремо». Сам виняток
        // обмежений трьома гарантіями самого запису: він один
        // (`UX_User_Bootstrap`), він міняє пароль при першому вході і він
        // вимикається, щойно з'являється доменний адміністратор (ФВ-6.18).
        var actor = await users.FindByIdAsync(userId, ct).ConfigureAwait(false);
        var isBootstrap = actor?.IsBootstrapAdmin == true;

        // ⛔ Невідомий код права до `A7-19` доходив до бази і повертався як
        // порушення зовнішнього ключа, тобто `ECR-SYS-0500` «зверніться до
        // адміністратора». Друкарська помилка в назві права — не внутрішня
        // помилка системи, і людина має побачити, ЯКЕ саме право не існує.
        var unknownPermissions = await users
            .FilterUnknownAsync(permissionCodes, ct)
            .ConfigureAwait(false);

        if (unknownPermissions.Count > 0)
        {
            throw new NotFoundException(
                "ECR-ROW-0404",
                $"Прав не існує: {string.Join(", ", unknownPermissions)}.");
        }

        var dangerous = await users.FilterDangerousAsync(permissionCodes, ct).ConfigureAwait(false);
        var notHeld = isBootstrap ? [] : dangerous.Where(p => !profile.Has(p)).ToList();
        if (notHeld.Count > 0)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403",
                "Небезпечні права не можна видати, не маючи їх самому.",
                new Dictionary<string, object?> { ["permissions"] = notHeld });
        }

        var role = new Role(EcrCode.Create(code), new LocalizedText(name.ToDictionary(StringComparer.Ordinal)));
        var roleId = await users.AddRoleAsync(role, permissionCodes, ct).ConfigureAwait(false);

        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow,
                "RoleCreated",
                TargetUserId: null,
                TargetRoleId: roleId,
                DetailsJson: JsonSerializer.Serialize(new { code, permissions = permissionCodes }),
                ChangedByUserId: userId,
                CorrelationId: currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return roleId;
    }
}

/// <summary>Перелік користувачів. Право <c>Security.ManageUsers</c>.</summary>
public sealed class ListUsersHandler(IUserStore users, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право, без якого перелік не віддається.</summary>
    public const string Permission = "Security.ManageUsers";

    /// <summary>Повертає сторінку користувачів.</summary>
    /// <param name="page">Параметри курсорної пагінації.</param>
    /// <param name="utcNow">Момент для обчислення блокування.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<PagedResult<UserView>> HandleAsync(
        CursorRequest page, DateTime utcNow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException("ECR-AUTH-0401", "Потрібна автентифікація.");

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        if (!profile.Has(Permission))
        {
            throw new AccessDeniedException("ECR-AUTH-0403", $"Потрібне право {Permission}.");
        }

        return await users.ListAsync(page, utcNow, ct).ConfigureAwait(false);
    }
}

/// <summary>Створення користувача. Право <c>Security.ManageUsers</c>.</summary>
public sealed class CreateUserHandler(
    IUserStore users,
    IPasswordHasher hasher,
    IAccessDecisionService access,
    DisableBootstrapAdminHandler disableBootstrap,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Створює локального або доменного користувача.</summary>
    /// <param name="userName">Ім'я входу.</param>
    /// <param name="displayName">Ім'я для показу.</param>
    /// <param name="provider">Провайдер входу.</param>
    /// <param name="windowsSid">SID; лише для доменного.</param>
    /// <param name="initialPassword">Разовий пароль; лише для локального.</param>
    /// <param name="roleCodes">Ролі, які призначити одразу.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<int> HandleAsync(
        string userName,
        string displayName,
        AuthProvider provider,
        string? windowsSid,
        string? initialPassword,
        IReadOnlyList<string> roleCodes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roleCodes);

        var actorId = currentUser.UserId
                      ?? throw new AccessDeniedException("ECR-AUTH-0401", "Потрібна автентифікація.");

        var profile = await access.BuildProfileAsync(actorId, ct).ConfigureAwait(false);
        if (!profile.Has(ListUsersHandler.Permission))
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {ListUsersHandler.Permission}.");
        }

        if (await users.FindByUserNameAsync(userName, ct).ConfigureAwait(false) is not null)
        {
            throw new BusinessRuleException(
                "ECR-ROW-0409", $"Користувач з іменем «{userName}» уже існує.");
        }

        // ⛔ Ролі перевіряються ТУТ і ДО створення чого-небудь. Сховище
        // виходить із того, що роль приходить із seed, і на невідому кидає
        // «виконайте seed» — вірно для старту й безглуздо для запиту людини:
        // друкарська помилка в коді ролі поверталася як `ECR-SYS-0500`
        // «зверніться до адміністратора» (`A7-18`), хоча адміністратор — це
        // якраз той, хто її щойно зробив.
        //
        // ⚠ Перевірка перед створенням, а не всередині циклу: інакше друга
        // роль із помилкою лишила б користувача створеним із першою.
        if (roleCodes.Count > 0)
        {
            var known = (await users.ListRolesAsync(ct).ConfigureAwait(false))
                .Select(r => r.Code)
                .ToHashSet(StringComparer.Ordinal);

            var unknown = roleCodes.Where(code => !known.Contains(code)).ToList();
            if (unknown.Count > 0)
            {
                // ⚠ Саме `NotFoundException`: статус відповіді береться з ТИПУ
                // винятку, а не з коду. `BusinessRuleException` дав би 422 при
                // коді `...0404` — відповідь, що суперечить сама собі.
                throw new NotFoundException(
                    "ECR-ROW-0404",
                    $"Ролей не існує: {string.Join(", ", unknown)}.");
            }
        }

        var now = clock.UtcNow;
        User user;

        if (provider == AuthProvider.Windows)
        {
            // Доменний запис — без пароля взагалі: пароль живе в каталозі, і
            // друга його копія тут була б і зайвою, і небезпечною.
            user = User.CreateDomain(
                userName, displayName,
                windowsSid ?? throw new BusinessRuleException(
                    "ECR-CELL-0422", "Для доменного запису потрібен SID."),
                now);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(initialPassword))
            {
                throw new BusinessRuleException(
                    "ECR-CELL-0422", "Для локального запису потрібен разовий пароль.");
            }

            user = new User(userName, displayName, AuthProvider.Local);
            user.SetPassword(hasher.Hash(initialPassword));

            // ⚠ Разовий пароль знає той, хто створював. Доки його не змінили,
            // доступний лише сам обмін пароля (ФВ-6.18) — інакше адміністратор
            // назавжди лишається з чинним входом у чужий обліковий запис.
            user.RequirePasswordChange();
        }

        users.Add(user);
        foreach (var roleCode in roleCodes)
        {
            await users.GrantRoleAsync(user, roleCode, ct).ConfigureAwait(false);
        }

        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                now,
                "UserCreated",
                TargetUserId: null,
                TargetRoleId: null,

                // ⛔ Пароль сюди не потрапляє ні в якому вигляді (ФВ-6.11).
                DetailsJson: JsonSerializer.Serialize(new { userName, provider = provider.ToString(), roleCodes }),
                ChangedByUserId: actorId,
                CorrelationId: currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        // ⚠ Після КОЖНОГО призначення ролі, а не за розкладом: вікно між
        // появою доменного адміністратора і вимкненням bootstrap-запису має
        // бути якомога коротшим (D-97).
        await disableBootstrap.HandleAsync(ct).ConfigureAwait(false);

        return user.Id;
    }
}
