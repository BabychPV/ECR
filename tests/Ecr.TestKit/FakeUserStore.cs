using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;

namespace Ecr.TestKit;

/// <summary>Сховище облікових записів у пам'яті.</summary>
public sealed class FakeUserStore : IUserStore
{
    private readonly List<User> _users = [];
    private int _nextId = 1;

    /// <summary>Ролі, призначені через <see cref="GrantRoleAsync"/>.</summary>
    public List<(string UserName, string RoleCode)> Grants { get; } = [];

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
        var user = _users.Find(u => u.Id == userId);

        return Task.FromResult<IReadOnlyList<string>>(user is null
            ? []
            : [.. Grants.Where(g => g.UserName == user.UserName).Select(g => g.RoleCode)]);
    }

    /// <inheritdoc />
    public Task<int> ReplaceRolesAsync(int userId, IReadOnlyList<string> roleCodes, CancellationToken ct)
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

        Grants.RemoveAll(g => g.UserName == user.UserName);
        foreach (var code in roleCodes)
        {
            Grants.Add((user.UserName, code));
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
