using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Security;

/// <summary>Що тримається на ролі — відповідь на «чому її не можна видалити».</summary>
/// <param name="Assignments">Призначення особам і групам, включно з простроченими.</param>
/// <param name="Grants">Ресурсні гранти.</param>
public sealed record RoleUsage(int Assignments, int Grants);

/// <summary>Спільні кроки трьох дій над роллю (директива №15, BE-14).</summary>
internal static class RoleLifecycle
{
    /// <summary>Знаходить роль або відповідає <c>ECR-SEC-0404</c>.</summary>
    public static async Task<RoleView> FindAsync(IUserStore users, int roleId, CancellationToken ct)
    {
        var roles = await users.ListRolesAsync(ct).ConfigureAwait(false);

        return roles.FirstOrDefault(r => r.Id == roleId)
               ?? throw new NotFoundException(
                   ErrorCodes.SecurityPrincipalNotFound, $"Ролі {roleId} не існує.",
                   new Dictionary<string, object?>
                   {
                       ["messageKey"] = "err.ECR-SEC-0404.roleNotFound",
                       ["roleId"] = roleId.ToString(CultureInfo.InvariantCulture),
                   });
    }

    /// <summary>Вбудована роль із сіду не перейменовується й не видаляється.</summary>
    /// <remarks>
    /// ⚠ <c>409</c>, а не <c>422</c>: запит складено правильно, відмовляє СТАН
    /// ролі, і виправити його повторним введенням нема чим. Статус дає
    /// чинний арм <c>ECR-SEC-0409</c> у <c>ExceptionHandlingMiddleware</c>.
    /// </remarks>
    public static void EnsureNotBuiltIn(RoleView role)
    {
        if (role.IsBuiltIn)
        {
            throw new BusinessRuleException(
                ErrorCodes.RoleDuplicate, $"Роль «{role.Code}» вбудована: її не перейменовують і не видаляють.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SEC-0409.roleBuiltIn",
                    ["code"] = role.Code,
                });
        }
    }

    /// <summary>Зайнятий код — той самий <c>roleCodeTaken</c>, що й при створенні.</summary>
    /// <remarks>
    /// ⚠ Перевірка наперед, бо зміна коду зберігається спільним комітом
    /// обробника, а не власним збереженням сховища, де <c>UQ_Role</c> ловить
    /// дублікат при створенні. Гонка двох перейменувань лишається за базою.
    /// </remarks>
    public static async Task EnsureCodeFreeAsync(IUserStore users, string code, int exceptRoleId, CancellationToken ct)
    {
        var roles = await users.ListRolesAsync(ct).ConfigureAwait(false);

        if (roles.Any(r => r.Id != exceptRoleId && string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase)))
        {
            throw new BusinessRuleException(
                ErrorCodes.RoleDuplicate, $"Роль із кодом «{code}» уже існує.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-SEC-0409.roleCodeTaken", ["code"] = code });
        }
    }
}

/// <summary>Перейменування ролі. Право <c>Security.ManageRoles</c>.</summary>
public sealed class RenameRoleHandler(
    IUserStore users,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Змінює код ролі та, за потреби, назву.</summary>
    /// <param name="roleId">Роль.</param>
    /// <param name="code">Новий код.</param>
    /// <param name="name">Нова назва; <c>null</c> — лишити чинну.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task HandleAsync(
        int roleId, string code, IReadOnlyDictionary<string, string>? name, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, ListRolesHandler.Permission, ct).ConfigureAwait(false);

        var role = await RoleLifecycle.FindAsync(users, roleId, ct).ConfigureAwait(false);
        RoleLifecycle.EnsureNotBuiltIn(role);

        var newCode = EcrCode.Create(code).Value;
        await RoleLifecycle.EnsureCodeFreeAsync(users, newCode, roleId, ct).ConfigureAwait(false);

        await users.RenameRoleAsync(roleId, newCode, name, ct).ConfigureAwait(false);

        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow,
                "RoleRenamed",
                TargetUserId: null,
                TargetRoleId: roleId,
                DetailsJson: JsonSerializer.Serialize(new { from = role.Code, to = newCode }),
                ChangedByUserId: profile.UserId,
                CorrelationId: currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Видалення ролі. Право <c>Security.ManageRoles</c>.</summary>
public sealed class DeleteRoleHandler(
    IUserStore users,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Видаляє роль, на якій нічого не тримається.</summary>
    /// <param name="roleId">Роль.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-SEC-0409</c>: роль вбудована або має призначення чи гранти —
    /// кількості їдуть у <c>details</c>.
    /// </exception>
    public async Task HandleAsync(int roleId, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, ListRolesHandler.Permission, ct).ConfigureAwait(false);

        var role = await RoleLifecycle.FindAsync(users, roleId, ct).ConfigureAwait(false);
        RoleLifecycle.EnsureNotBuiltIn(role);

        // ⛔ Роль із носіями не видаляється мовчки: це відібрало б доступ у
        // людей, яких адміністратор у цю мить не бачить. Спершу зняти
        // призначення й гранти — свідомо, по одному екрану на кожне.
        var usage = await users.CountRoleUsageAsync(roleId, ct).ConfigureAwait(false);
        if (usage.Assignments > 0 || usage.Grants > 0)
        {
            throw new BusinessRuleException(
                ErrorCodes.RoleDuplicate,
                $"Роль «{role.Code}» використовується: призначень {usage.Assignments}, грантів {usage.Grants}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SEC-0409.roleInUse",
                    ["code"] = role.Code,
                    ["assignments"] = usage.Assignments.ToString(CultureInfo.InvariantCulture),
                    ["grants"] = usage.Grants.ToString(CultureInfo.InvariantCulture),
                });
        }

        await users.RemoveRoleAsync(roleId, ct).ConfigureAwait(false);

        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow,
                "RoleDeleted",
                TargetUserId: null,
                TargetRoleId: roleId,
                DetailsJson: JsonSerializer.Serialize(new { code = role.Code, permissions = role.Permissions }),
                ChangedByUserId: profile.UserId,
                CorrelationId: currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Клонування ролі. Право <c>Security.ManageRoles</c>.</summary>
/// <remarks>
/// ⚠ Копіюється набір ПРАВ, а не гранти й не призначення: клон — заготовка
/// нового обов'язку, і «до чого саме» він має доступ, адміністратор вирішує
/// окремо. Створення йде через <see cref="CreateRoleHandler"/>, тож слід про
/// небезпечні права й подія <c>RoleCreated</c> пишуться тим самим шляхом.
/// </remarks>
public sealed class CloneRoleHandler(
    IUserStore users,
    IAccessDecisionService access,
    CreateRoleHandler createRole,
    ICurrentUser currentUser)
{
    /// <summary>Створює роль із новим кодом і правами ролі-джерела.</summary>
    /// <param name="sourceRoleId">Роль-джерело.</param>
    /// <param name="code">Код нової ролі.</param>
    /// <param name="name">Назва; <c>null</c> — код як назва <c>en</c>.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ідентифікатор нової ролі.</returns>
    public async Task<int> HandleAsync(
        int sourceRoleId, string code, IReadOnlyDictionary<string, string>? name, CancellationToken ct)
    {
        await PermissionCheck
            .RequireAsync(access, currentUser, ListRolesHandler.Permission, ct).ConfigureAwait(false);

        var source = await RoleLifecycle.FindAsync(users, sourceRoleId, ct).ConfigureAwait(false);

        return await createRole
            .HandleAsync(
                code,
                name ?? new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = code },
                source.Permissions,
                ct)
            .ConfigureAwait(false);
    }
}
