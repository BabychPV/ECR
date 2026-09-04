using Ecr.Application.Ports;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Security;

/// <summary>Реалізація <see cref="IUserStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class UserStore(EcrDbContext db) : IUserStore
{
    /// <summary>Код типової політики паролів із seed.</summary>
    private const string DefaultPolicyCode = "Default";

    /// <inheritdoc />
    public Task<User?> FindBootstrapAdminAsync(CancellationToken ct)
        => db.Users.FirstOrDefaultAsync(u => u.IsBootstrapAdmin, ct);

    /// <inheritdoc />
    public Task<User?> FindByUserNameAsync(string userName, CancellationToken ct)
        => db.Users.FirstOrDefaultAsync(u => u.UserName == userName, ct);

    /// <inheritdoc />
    public Task<User?> FindByIdAsync(int userId, CancellationToken ct)
        => db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

    /// <inheritdoc />
    public Task<bool> HasActiveDomainAdminAsync(string permissionCode, CancellationToken ct)
        => (from user in db.Users
            join assignment in db.RoleAssignments on user.Id equals assignment.UserId
            join permission in db.RolePermissions on assignment.RoleId equals permission.RoleId
            where user.IsActive
                  && user.Provider == AuthProvider.Windows

                  // ⚠ Bootstrap виключений явно: інакше він рахував би сам себе
                  // адміністратором і вимикав би себе на першому ж старті.
                  && !user.IsBootstrapAdmin
                  && permission.PermissionCode == permissionCode
            select user.Id).AnyAsync(ct);

    /// <inheritdoc />
    public void Add(User user) => db.Users.Add(user);

    /// <inheritdoc />
    public async Task GrantRoleAsync(User user, string roleCode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);

        var roleId = await db.Roles
            .Where(r => r.Code == roleCode && r.IsActive)
            .Select(r => r.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (roleId == 0)
        {
            // Роль приходить із seed. Її відсутність означає, що базу підняли
            // без seed, — і мовчазне «не призначили» дало б адміністратора без
            // жодного права, а це гірше, ніж явна зупинка.
            throw new InvalidOperationException(
                $"Роль '{roleCode}' відсутня. Виконайте seed перед першим стартом.");
        }

        // Навігація, а не UserId: користувача могли ще не зберегти, і тоді
        // ключ дорівнює нулю. EF підставить його сам при SaveChanges.
        db.RoleAssignments.Add(new RoleAssignment(roleId, user));
    }

    /// <inheritdoc />
    public async Task<PasswordPolicy> GetPolicyAsync(User user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);

        var policy = user.PasswordPolicyId is { } id
            ? await db.PasswordPolicies.FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false)
            : null;

        policy ??= await db.PasswordPolicies
            .FirstOrDefaultAsync(p => p.Code == DefaultPolicyCode, ct).ConfigureAwait(false);

        // ⛔ Відсутня політика — це НЕ «без обмежень». Порожнє поле не має бути
        // тихим способом вимкнути перевірку довжини пароля.
        return policy ?? new PasswordPolicy(DefaultPolicyCode, minLength: 12, maxFailedAttempts: 5);
    }
}
