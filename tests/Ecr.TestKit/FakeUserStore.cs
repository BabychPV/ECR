using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;

namespace Ecr.TestKit;

/// <summary>Призначення ролі на SID AD-групи — з межами дії або без них.</summary>
/// <param name="Sid">SID групи.</param>
/// <param name="RoleCode">Код ролі.</param>
/// <param name="ValidFrom">Початок дії; <c>null</c> — від завжди.</param>
/// <param name="ValidTo">Кінець дії; <c>null</c> — безстроково.</param>
public sealed record GroupRoleAssignment(
    string Sid, string RoleCode, DateOnly? ValidFrom = null, DateOnly? ValidTo = null);

/// <summary>Сховище облікових записів у пам'яті.</summary>
public sealed class FakeUserStore : IUserStore
{
    private readonly List<User> _users = [];
    private int _nextId = 1;

    /// <summary>Ролі, призначені через <see cref="GrantRoleAsync"/>.</summary>
    public List<(string UserName, string RoleCode)> Grants { get; } = [];

    /// <summary>
    /// Особисті призначення з межами чинності, виставлені через
    /// <see cref="ReplaceRolesAsync"/> (`#48`, ФВ-6.16 — підміна на час
    /// відпустки).
    /// </summary>
    /// <remarks>
    /// ⚠ Окремо від <see cref="Grants"/>, бо система теж тримає їх окремо:
    /// <see cref="Grants"/> — безстроковий набір, яким керує «заміна
    /// набором», а тут — строкові підміни, які та сама заміна не чіпає, поки
    /// виклик явно не задав меж саме для цієї ролі.
    /// </remarks>
    public List<(string UserName, string RoleCode, DateOnly? ValidFrom, DateOnly? ValidTo)> DatedGrants { get; } = [];

    /// <summary>
    /// Ролі, призначені НА ГРУПУ — основний спосіб для доменних користувачів
    /// (<c>ФВ-6.15</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Фікстура тримає їх окремим переліком саме тому, що система тримає їх
    /// окремим адресатом: <c>CK_RoleAssign_Principal</c> вимагає РІВНО ОДНОГО
    /// — або особи, або групи. Звести їх тут в один список означало б, що
    /// тести не бачать різниці, заради якої існує весь `H-21`.
    /// </remarks>
    public List<GroupRoleAssignment> GroupAssignments { get; } = [];

    /// <summary>Ролі сховища.</summary>
    public List<RoleView> Roles { get; } = [];

    /// <summary>Коди прав, які вважаються небезпечними.</summary>
    public HashSet<string> Dangerous { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Каталог відомих прав.
    /// </summary>
    /// <remarks>
    /// ⚠ Порожній набір означає «тест не моделює каталог», і тоді невідомих
    /// прав немає за визначенням. Це свідома поблажливість: більшість тестів
    /// перевіряє не існування коду, а поведінку навколо нього.
    ///
    /// ⛔ Але саме такі поблажливості й приховали `A7-13`: фікстура вміла
    /// більше за справжню систему, тести були зелені, а розгортання падало.
    /// Тому тест, який перевіряє реакцію на невідоме право, зобов'язаний
    /// наповнити цей набір — інакше він нічого не перевіряє.
    /// </remarks>
    public HashSet<string> Permissions { get; } = new(StringComparer.Ordinal);

    /// <summary>Зафіксовані спроби входу.</summary>
    public List<LoginAttempt> Attempts { get; } = [];

    /// <summary>Чи є в системі активний доменний адміністратор.</summary>
    public bool HasDomainAdmin { get; set; }

    /// <summary>Політика паролів, яку віддає сховище.</summary>
    public PasswordPolicy Policy { get; set; } = new("Default", minLength: 12, maxFailedAttempts: 5);

    /// <summary>Усі записи.</summary>
    public IReadOnlyList<User> Users => _users;

    /// <summary>Кладе готовий запис і присвоює йому ідентифікатор.</summary>
    /// <param name="user">Запис.</param>
    public User Seed(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        AssignId(user);
        _users.Add(user);
        return user;
    }

    /// <inheritdoc />
    public Task<User?> FindBootstrapAdminAsync(CancellationToken ct)
        => Task.FromResult(_users.Find(u => u.IsBootstrapAdmin));

    /// <inheritdoc />
    public Task<User?> FindByUserNameAsync(string userName, CancellationToken ct)
        => Task.FromResult(_users.Find(u => string.Equals(u.UserName, userName, StringComparison.Ordinal)));

    /// <inheritdoc />
    public Task<User?> FindByIdAsync(int userId, CancellationToken ct)
        => Task.FromResult(_users.Find(u => u.Id == userId));

    /// <inheritdoc />
    public Task<bool> HasActiveDomainAdminAsync(string permissionCode, CancellationToken ct)
        => Task.FromResult(HasDomainAdmin);

    /// <inheritdoc />
    public Task<User?> FindByWindowsSidAsync(string sid, CancellationToken ct)
        => Task.FromResult(_users.Find(u => string.Equals(u.WindowsSid, sid, StringComparison.Ordinal)));

    /// <inheritdoc />
    public void Add(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        AssignId(user);
        _users.Add(user);
    }

    /// <inheritdoc />
    public Task GrantRoleAsync(User user, string roleCode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);

        Grants.Add((user.UserName, roleCode));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListUserRolesAsync(int userId, CancellationToken ct)
    {
        // Фікстура тримає лише безстрокові призначення — саме ті, якими
        // керує `ReplaceRolesAsync`.
        var user = _users.Find(u => u.Id == userId);

        return Task.FromResult<IReadOnlyList<string>>(user is null
            ? []
            : [.. Grants.Where(g => g.UserName == user.UserName).Select(g => g.RoleCode)]);
    }

    /// <inheritdoc />
    public Task<int> ReplaceRolesAsync(
        int userId,
        IReadOnlyList<string> roleCodes,
        IReadOnlyDictionary<string, RoleValidityWindow>? validity,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roleCodes);

        var user = _users.Find(u => u.Id == userId)
                   ?? throw new Application.Errors.NotFoundException(
                       "ECR-SEC-0404", $"Користувача {userId} не знайдено.");

        var unknown = roleCodes
            .Where(code => !Roles.Exists(r => r.Code == code))
            .ToList();

        if (unknown.Count > 0)
        {
            throw new Application.Errors.NotFoundException(
                "ECR-SEC-0404", $"Ролей не існує: {string.Join(", ", unknown)}.");
        }

        // Той самий поділ, що й у бойовому `UserStore.ReplaceRolesAsync`
        // (`#48`): межі задає ВИКЛИК, а не роль.
        var permanentCodes = new List<string>();
        var datedCodes = new List<(string Code, DateOnly? From, DateOnly? To)>();
        foreach (var code in roleCodes)
        {
            if (validity is not null
                && validity.TryGetValue(code, out var window)
                && (window.ValidFrom is not null || window.ValidTo is not null))
            {
                datedCodes.Add((code, window.ValidFrom, window.ValidTo));
            }
            else
            {
                permanentCodes.Add(code);
            }
        }

        Grants.RemoveAll(g => g.UserName == user.UserName);
        foreach (var code in permanentCodes)
        {
            Grants.Add((user.UserName, code));
        }

        foreach (var (code, from, to) in datedCodes)
        {
            DatedGrants.RemoveAll(g => g.UserName == user.UserName && g.RoleCode == code);
            DatedGrants.Add((user.UserName, code, from, to));
        }

        // Штамп безпеки крутиться і у фікстурі: тест, який цього не бачить,
        // не побачив би і його відсутності в бойовому коді.
        user.RefreshSecurityStamp();

        return Task.FromResult(roleCodes.Count);
    }

    /// <inheritdoc />
    public void RecordAttempt(LoginAttempt attempt) => Attempts.Add(attempt);

    /// <inheritdoc />
    public Task<PagedResult<UserView>> ListAsync(CursorRequest page, DateTime utcNow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        var items = _users
            .OrderBy(u => u.Id)
            .Take(page.Limit)
            .Select(u => new UserView(
                u.Id, u.UserName, u.DisplayName, u.Provider,
                u.IsActive, u.IsBootstrapAdmin, u.MustChangePassword, u.IsLockedOut(utcNow),
                u.Email, u.ReceivesAlerts))
            .ToList();

        return Task.FromResult(new PagedResult<UserView>(items, NextCursor: null, TotalCount: items.Count));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RoleView>> ListRolesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RoleView>>(Roles);

    /// <inheritdoc />
    public Task<int> AddRoleAsync(Role role, IReadOnlyList<string> permissionCodes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(permissionCodes);

        var id = Roles.Count + 1;
        Roles.Add(new RoleView(id, role.Code, role.IsBuiltIn, role.IsActive, [.. permissionCodes], []));
        return Task.FromResult(id);
    }

    /// <summary>Ресурсні гранти за роллю.</summary>
    /// <remarks>
    /// ⚠ Фікстура тримає гранти так само, як їх тримає система: набором на
    /// роль. Спокуса зробити «список на користувача» тут велика і хибна —
    /// саме на роль вони й лягають (`UQ_ResourceGrant`).
    /// </remarks>
    public Dictionary<int, List<Ecr.Application.Security.ResourceGrantDto>> GrantsByRole { get; } = [];

    /// <inheritdoc />
    public Task<IReadOnlyList<Ecr.Application.Security.ResourceGrantDto>> ListGrantsAsync(
        int roleId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Ecr.Application.Security.ResourceGrantDto>>(
            GrantsByRole.TryGetValue(roleId, out var list) ? list : []);

    /// <inheritdoc />
    public Task ReplaceGrantsAsync(
        int roleId, IReadOnlyList<Ecr.Application.Security.ResourceGrantDto> grants, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grants);

        GrantsByRole[roleId] = [.. grants];

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> RotateStampsForRoleAsync(int roleId, CancellationToken ct)
    {
        var affected = Grants
            .Where(g => string.Equals(g.RoleCode, RoleCodeById(roleId), StringComparison.Ordinal))
            .Select(g => g.UserName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var user in _users.Where(u => affected.Contains(u.UserName, StringComparer.Ordinal)))
        {
            user.RefreshSecurityStamp();
        }

        return Task.FromResult(affected.Count);
    }

    /// <summary>Код ролі за ідентифікатором; порожньо, якщо ролі немає.</summary>
    private string RoleCodeById(int roleId)
        => Roles.FirstOrDefault(r => r.Id == roleId)?.Code ?? string.Empty;

    /// <summary>Ідентифікатор ролі за кодом; нуль, якщо ролі немає.</summary>
    private int RoleIdByCode(string roleCode)
        => Roles.FirstOrDefault(r => string.Equals(r.Code, roleCode, StringComparison.Ordinal))?.Id ?? 0;

    /// <inheritdoc />
    public Task<IReadOnlyList<RoleAssignmentTrace>> ListAssignmentsAsync(
        int userId, IReadOnlyList<string> groupSids, DateOnly asOf, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(groupSids);

        var user = _users.Find(u => u.Id == userId);

        // Безстрокові особисті призначення — керує ними `ReplaceRolesAsync`.
        var personal = user is null
            ? new List<RoleAssignmentTrace>()
            : [.. Grants
                .Where(g => string.Equals(g.UserName, user.UserName, StringComparison.Ordinal))
                .Select(g => new RoleAssignmentTrace(
                    RoleIdByCode(g.RoleCode), g.RoleCode, null, null, null, IsEffective: true))];

        // Строкові особисті призначення (`#48`, ФВ-6.16) — та сама заміна
        // ПОРОЛЬНО, що й у бойовому сховищі; чинність рахує ДОМЕН, а не
        // друга копія умови тут.
        var personalDated = user is null
            ? new List<RoleAssignmentTrace>()
            : [.. DatedGrants
                .Where(g => string.Equals(g.UserName, user.UserName, StringComparison.Ordinal))
                .Select(g => TracePersonal(g, asOf))];

        // ⚠ Без урахування регістру — так само, як зіставляє SQL Server із
        // типовим порівнянням. Фікстура, суворіша за систему, показувала б
        // «не збіглося» там, де доступ насправді виданий.
        var byGroup = GroupAssignments
            .Where(a => groupSids.Contains(a.Sid, StringComparer.OrdinalIgnoreCase))
            .Select(a => Trace(a, asOf));

        return Task.FromResult<IReadOnlyList<RoleAssignmentTrace>>(
            [.. personal, .. personalDated, .. byGroup]);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RoleAssignmentTrace>> ListGroupAssignmentsAsync(
        DateOnly asOf, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RoleAssignmentTrace>>(
            [.. GroupAssignments.Select(a => Trace(a, asOf))]);

    /// <summary>Переводить призначення на групу у зріз для діагностики.</summary>
    /// <param name="assignment">Призначення.</param>
    /// <param name="asOf">Дата, на яку рахується чинність.</param>
    /// <remarks>
    /// ⛔ Чинність рахує ДОМЕН (<c>RoleAssignment.IsEffectiveOn</c>), а не
    /// друга копія умови у фікстурі. Копія збігалася б із доменом рівно до
    /// першої правки одного з них — і саме тому `H-23a` існує як знахідка.
    ///
    /// ⚠ Межі дії виставляються через <see cref="RoleAssignment.SetValidity"/>
    /// (`#48`) — до нього тут стояла рефлексія, бо жоден прикладний шлях
    /// сетера не мав (`D2-61`); тепер шлях є, і фікстура використовує той
    /// самий метод, що й бойове сховище.
    /// </remarks>
    private RoleAssignmentTrace Trace(GroupRoleAssignment assignment, DateOnly asOf)
    {
        var entity = new RoleAssignment(
            RoleIdByCode(assignment.RoleCode), userId: null, principalSid: assignment.Sid);

        entity.SetValidity(assignment.ValidFrom, assignment.ValidTo);

        return new RoleAssignmentTrace(
            entity.RoleId,
            assignment.RoleCode,
            assignment.Sid,
            assignment.ValidFrom,
            assignment.ValidTo,
            entity.IsEffectiveOn(asOf));
    }

    /// <summary>Переводить строкове ОСОБИСТЕ призначення у зріз для діагностики.</summary>
    /// <param name="assignment">Ім'я користувача, код ролі й межі дії.</param>
    /// <param name="asOf">Дата, на яку рахується чинність.</param>
    /// <remarks>
    /// ⚠ Дзеркало <see cref="Trace(GroupRoleAssignment,DateOnly)"/> для
    /// адресата «особа», а не «група» (`#48`): чинність так само рахує
    /// <see cref="RoleAssignment.IsEffectiveOn"/>, а не копія умови тут.
    /// </remarks>
    private RoleAssignmentTrace TracePersonal(
        (string UserName, string RoleCode, DateOnly? ValidFrom, DateOnly? ValidTo) assignment, DateOnly asOf)
    {
        var entity = new RoleAssignment(RoleIdByCode(assignment.RoleCode), userId: null, principalSid: null);
        entity.SetValidity(assignment.ValidFrom, assignment.ValidTo);

        return new RoleAssignmentTrace(
            entity.RoleId,
            assignment.RoleCode,
            PrincipalSid: null,
            assignment.ValidFrom,
            assignment.ValidTo,
            entity.IsEffectiveOn(asOf));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> FilterUnknownAsync(
        IReadOnlyList<string> permissionCodes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(permissionCodes);

        return Task.FromResult<IReadOnlyList<string>>(
            Permissions.Count == 0
                ? []
                : [.. permissionCodes.Distinct(StringComparer.Ordinal).Where(c => !Permissions.Contains(c))]);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> FilterDangerousAsync(
        IReadOnlyList<string> permissionCodes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(permissionCodes);

        return Task.FromResult<IReadOnlyList<string>>(
            [.. permissionCodes.Where(Dangerous.Contains)]);
    }

    /// <inheritdoc />
    public Task<PasswordPolicy> GetPolicyAsync(User user, CancellationToken ct) => Task.FromResult(Policy);

    /// <summary>Активний доменний користувач для сценаріїв входу.</summary>
    /// <param name="userName">Ім'я входу.</param>
    /// <param name="sid">SID у каталозі.</param>
    public static User DomainUser(string userName, string sid = "S-1-5-21-1")
        => User.CreateDomain(userName, userName, sid, DateTime.UnixEpoch);

    /// <summary>Ідентифікатор присвоюється сховищем — так само, як IDENTITY у базі.</summary>
    private void AssignId(User user)
    {
        if (user.Id == 0)
        {
            typeof(User).GetProperty(nameof(User.Id))!.SetValue(user, _nextId++);
        }
    }
}
