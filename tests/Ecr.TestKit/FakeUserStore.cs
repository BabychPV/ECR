using Ecr.Application.Ports;
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
    public Task<PasswordPolicy> GetPolicyAsync(User user, CancellationToken ct) => Task.FromResult(Policy);

    /// <summary>Активний доменний користувач для сценаріїв входу.</summary>
    /// <param name="userName">Ім'я входу.</param>
    /// <param name="sid">SID у каталозі.</param>
    public static User DomainUser(string userName, string sid = "S-1-5-21-1")
    {
        var user = new User(userName, userName, AuthProvider.Windows);
        typeof(User).GetProperty(nameof(User.WindowsSid))!.SetValue(user, sid);
        return user;
    }

    /// <summary>Ідентифікатор присвоюється сховищем — так само, як IDENTITY у базі.</summary>
    private void AssignId(User user)
    {
        if (user.Id == 0)
        {
            typeof(User).GetProperty(nameof(User.Id))!.SetValue(user, _nextId++);
        }
    }
}
