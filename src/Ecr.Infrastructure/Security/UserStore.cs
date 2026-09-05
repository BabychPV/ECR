using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
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

    /// <summary>Стеля кількості ролей в одному зрізі.</summary>
    /// <remarks>
    /// Ролі — дані, а не enum (ФВ-6.6), тож їх кількість не обмежена схемою.
    /// П'ятсот — та сама межа, що й <c>CursorRequest.MaxLimit</c>: більше
    /// ролей на екрані все одно не читають.
    /// </remarks>
    private const int MaxRoles = 500;

    /// <summary>Стеля кількості прав; каталог закритий і не зростає сам.</summary>
    private const int MaxPermissions = 128;

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
    public Task<User?> FindByWindowsSidAsync(string sid, CancellationToken ct)
        => db.Users.FirstOrDefaultAsync(u => u.WindowsSid == sid, ct);

    /// <inheritdoc />
    public void Add(User user) => db.Users.Add(user);

    /// <inheritdoc />
    public void RecordAttempt(LoginAttempt attempt) => db.LoginAttempts.Add(attempt);

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
    public async Task<PagedResult<UserView>> ListAsync(
        CursorRequest page, DateTime utcNow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        var after = Cursor.Decode(page.Cursor);

        // Беремо на один більше за сторінку: так видно, чи є наступна, без
        // окремого COUNT по всій таблиці.
        var rows = await db.Users
            .AsNoTracking()
            .Where(u => u.Id > after)
            .OrderBy(u => u.Id)
            .Take(page.Limit + 1)
            .Select(u => new
            {
                u.Id, u.UserName, u.DisplayName, u.Provider,
                u.IsActive, u.IsBootstrapAdmin, u.MustChangePassword, u.LockedUntil,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var hasMore = rows.Count > page.Limit;
        var items = rows.Take(page.Limit)
            .Select(u => new UserView(
                u.Id, u.UserName, u.DisplayName, u.Provider,
                u.IsActive, u.IsBootstrapAdmin, u.MustChangePassword,
                u.LockedUntil is { } until && until > utcNow))
            .ToList();

        // ⛔ У проєкції немає ні PasswordHash, ні SecurityStamp — і не тому, що
        // «забули додати»: перелік користувачів бачить адміністратор, а хеш не
        // має покидати сховище взагалі (ФВ-6.11).
        return new PagedResult<UserView>(
            items, hasMore ? Cursor.Encode(items[^1].Id) : null, TotalCount: null);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RoleView>> ListRolesAsync(CancellationToken ct)
    {
        // ⚠ Take стоїть навіть тут, де набір свідомо малий: ролей десятки,
        // прав 38. Але «свідомо малий» — це властивість сьогоднішніх даних, а
        // не запиту; запит без межі рано чи пізно зустріне таблицю, яка виросла.
        var roles = await db.Roles
            .AsNoTracking()
            .OrderBy(r => r.Code)
            .Take(MaxRoles)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var permissions = await db.RolePermissions
            .AsNoTracking()
            .Join(db.Permissions, rp => rp.PermissionCode, p => p.Id,
                  (rp, p) => new { rp.RoleId, Code = p.Id, p.IsDangerous })
            .Take(MaxRoles * MaxPermissions)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return roles
            .Select(r =>
            {
                var mine = permissions.Where(p => p.RoleId == r.Id).ToList();

                // Небезпечні показуються ОКРЕМО, а не тонуть у спільному
                // списку: їх видають поіменно, і адміністратор має бачити, що
                // саме він видає (ФВ-6.12, D-40).
                return new RoleView(
                    r.Id, r.Code, r.IsBuiltIn, r.IsActive,
                    [.. mine.Select(p => p.Code).Order(StringComparer.Ordinal)],
                    [.. mine.Where(p => p.IsDangerous).Select(p => p.Code).Order(StringComparer.Ordinal)]);
            })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<int> AddRoleAsync(Role role, IReadOnlyList<string> permissionCodes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(permissionCodes);

        db.Roles.Add(role);

        // Ідентифікатор ролі потрібен для зв'язків, а IDENTITY заповнюється
        // лише при збереженні.
        //
        // ⛔ Через це роль і її права зберігаються ДВОМА записами, і без
        // спільної транзакції невдача на другому лишає роль без жодного права
        // (`A7-19`). Порожня роль виглядає як робоча конфігурація, займає свою
        // назву — і повторити створення вже не можна.
        //
        // ⛔ Транзакцію відкриває СТРАТЕГІЯ ВИКОНАННЯ, а не `BeginTransaction`
        // напряму: з'єднання налаштоване на повтори транзієнтних збоїв, і
        // `SqlServerRetryingExecutionStrategy` відмовляється працювати з
        // транзакцією, відкритою повз неї. Ручна транзакція тут падала з
        // `InvalidOperationException` на кожному створенні ролі.
        //
        // ⚠ Стратегія повторює ВЕСЬ блок, тому обидва збереження і коміт
        // мусять бути всередині: інакше повтор дописав би права до ролі,
        // створеної попередньою спробою.
        if (db.Database.CurrentTransaction is not null)
        {
            // Обробник уже відкрив ширшу транзакцію — вкладена зламала б її
            // межі, а атомарність і так забезпечена зовнішньою.
            await SaveRoleAsync(role, permissionCodes, ct).ConfigureAwait(false);

            return role.Id;
        }

        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database
                .BeginTransactionAsync(ct)
                .ConfigureAwait(false);

            await SaveRoleAsync(role, permissionCodes, ct).ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return role.Id;
    }

    /// <summary>Зберігає роль і її права двома записами.</summary>
    /// <param name="role">Роль; після першого збереження має ідентифікатор.</param>
    /// <param name="permissionCodes">Коди прав.</param>
    /// <param name="ct">Токен скасування.</param>
    private async Task SaveRoleAsync(Role role, IReadOnlyList<string> permissionCodes, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var code in permissionCodes.Distinct(StringComparer.Ordinal))
        {
            db.RolePermissions.Add(new RolePermission(role.Id, code));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> FilterUnknownAsync(
        IReadOnlyList<string> permissionCodes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(permissionCodes);

        var known = await db.Permissions
            .AsNoTracking()
            .Where(p => permissionCodes.Contains(p.Id))
            .Select(p => p.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. permissionCodes
            .Distinct(StringComparer.Ordinal)
            .Where(code => !known.Contains(code, StringComparer.Ordinal))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> FilterDangerousAsync(
        IReadOnlyList<string> permissionCodes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(permissionCodes);

        return await db.Permissions
            .AsNoTracking()
            .Where(p => p.IsDangerous && permissionCodes.Contains(p.Id))
            .Select(p => p.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
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
