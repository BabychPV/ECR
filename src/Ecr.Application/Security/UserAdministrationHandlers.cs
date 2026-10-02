using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Security;

/// <summary>Спільні перевірки адміністративних дій над обліковим записом (BE-12).</summary>
internal static class UserAdministration
{
    /// <summary>Найдовша причина блокування/розблокування.</summary>
    public const int MaxReasonLength = 400;

    /// <summary>Перевіряє право і знаходить запис-ціль; повертає виконавця й ціль.</summary>
    public static async Task<(int ActorId, User Target)> ResolveAsync(
        IUserStore users, IAccessDecisionService access, ICurrentUser currentUser, string permission, int userId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, permission, ct).ConfigureAwait(false);

        var target = await users.FindByIdAsync(userId, ct).ConfigureAwait(false)
                     ?? throw new NotFoundException(
                         ErrorCodes.SecurityPrincipalNotFound, $"Користувача {userId} не існує.",
                         new Dictionary<string, object?>
                         {
                             ["messageKey"] = "err.ECR-SEC-0404.userNotFound",
                             ["userId"] = userId.ToString(CultureInfo.InvariantCulture),
                         });

        return (currentUser.UserId!.Value, target);
    }

    /// <summary>Над власним записом — ні блокування, ні адміністративного скидання.</summary>
    /// <remarks>Себе заблокувати — лишитися за дверима; свій пароль міняють <c>auth/change-password</c>.</remarks>
    public static void EnsureNotSelf(int actorId, User target)
    {
        if (target.Id == actorId)
        {
            throw new BusinessRuleException(
                ErrorCodes.SecurityConflict, "Над власним обліковим записом ця дія заборонена.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-SEC-0409.cannotTargetSelf" });
        }
    }

    /// <summary>Не лишати систему без жодного, хто може керувати користувачами.</summary>
    public static async Task EnsureNotLastAdministratorAsync(IUserStore users, User target, DateTime now, CancellationToken ct)
    {
        const string permission = BootstrapAdmin.AdminPermission;

        var all = await users.CountActivePermissionHoldersAsync(permission, null, now, ct).ConfigureAwait(false);
        var others = await users.CountActivePermissionHoldersAsync(permission, target.Id, now, ct).ConfigureAwait(false);

        // all > others — ціль сама тримає право; others == 0 — окрім неї нікого.
        if (all > others && others == 0)
        {
            throw new BusinessRuleException(
                ErrorCodes.SecurityConflict, $"«{target.UserName}» — останній активний адміністратор.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SEC-0409.lastAdministrator",
                    ["userName"] = target.UserName,
                });
        }
    }

    /// <summary>
    /// Заміна набору ролей не лишає систему без носія <see cref="BootstrapAdmin.AdminPermission"/>:
    /// якщо ціль — останній носій, а новий набір права не дає (або роль вимкнена/ще не чинна), — 409.
    /// </summary>
    public static async Task EnsureRoleSetKeepsAdministratorAsync(
        IUserStore users,
        int userId,
        IReadOnlyList<string> roleCodes,
        IReadOnlyDictionary<string, RoleValidityWindow>? validity,
        IReadOnlyDictionary<string, RoleAssignmentScope>? scopes,
        DateTime now,
        CancellationToken ct)
    {
        var roles = await users.ListRolesAsync(ct).ConfigureAwait(false);
        var today = DateOnly.FromDateTime(now);

        // ⛔ Роль з областю права адміністратора не дає (воно глобальне). `scopes` передано —
        // область мають лише коди з нього; не передано — зберігаються області наявних призначень.
        var existing = scopes is null
            ? await users.ListUserRoleAssignmentsAsync(userId, ct).ConfigureAwait(false)
            : [];

        var keeps = roleCodes.Any(code =>
        {
            var role = roles.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.Ordinal));
            if (role is not { IsActive: true }
                || !role.Permissions.Contains(BootstrapAdmin.AdminPermission, StringComparer.Ordinal))
            {
                return false;
            }

            var scoped = scopes is not null
                ? scopes.ContainsKey(code)
                : existing.Any(a => string.Equals(a.RoleCode, code, StringComparison.Ordinal) && a.Scope is not null);
            if (scoped)
            {
                return false;
            }

            if (validity is null || !validity.TryGetValue(code, out var window))
            {
                return true;
            }

            // Чинність рахує домен (`IsEffectiveOn`), а не друга копія умови (`H-23a`).
            var probe = new RoleAssignment(role.Id, userId, principalSid: null);
            probe.SetValidity(window.ValidFrom, window.ValidTo);
            return probe.IsEffectiveOn(today);
        });

        if (keeps)
        {
            return;
        }

        var target = await users.FindByIdAsync(userId, ct).ConfigureAwait(false);
        if (target is not null)
        {
            await EnsureNotLastAdministratorAsync(users, target, now, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Причина обов'язкова: вона — єдина відповідь журналу на «чому».</summary>
    public static string RequireReason(string? reason)
    {
        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > MaxReasonLength)
        {
            throw new BusinessRuleException(
                ErrorCodes.UserInvalid, $"Потрібна причина до {MaxReasonLength} символів.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-USR-0422.lockReasonRequired",
                    ["max"] = MaxReasonLength.ToString(CultureInfo.InvariantCulture),
                });
        }

        return trimmed;
    }
}

/// <summary>Адміністративне скидання пароля. Право <c>Security.ManageUsers</c> (BE-12).</summary>
/// <remarks>
/// ⚠ Пароль задає адміністратор і сервер його НЕ повертає — той самий шлях, що й
/// разовий пароль при створенні: <c>MustChangePassword</c>, поки власник не змінить свій.
/// </remarks>
public sealed class ResetUserPasswordHandler(
    IUserStore users,
    IPasswordHasher hasher,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право керування користувачами.</summary>
    public const string Permission = "Security.ManageUsers";

    /// <summary>Замінює пароль локального запису разовим і обриває всі його сесії.</summary>
    /// <param name="userId">Ціль.</param>
    /// <param name="newPassword">Разовий пароль; не логується і не пишеться в журнал.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task HandleAsync(int userId, string? newPassword, CancellationToken ct)
    {
        var (actorId, target) = await UserAdministration
            .ResolveAsync(users, access, currentUser, Permission, userId, ct).ConfigureAwait(false);

        UserAdministration.EnsureNotSelf(actorId, target);

        if (target.Provider != AuthProvider.Local)
        {
            throw new BusinessRuleException(
                ErrorCodes.UserInvalid, "Пароль доменного запису змінюється засобами домену.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-USR-0422.domainPasswordReset",
                    ["userName"] = target.UserName,
                });
        }

        var now = clock.UtcNow;
        await UserAdministration.EnsureNotLastAdministratorAsync(users, target, now, ct).ConfigureAwait(false);

        // Та сама політика, що й для разового пароля при створенні (CreateUserHandler).
        var policy = await users.GetPolicyAsync(target, ct).ConfigureAwait(false);
        // S15: спільна перевірка — довжина, цифра за політикою, ім'я, блок-лист.
        PasswordPolicyCheck.Ensure(policy, newPassword, target.UserName, "Разовий пароль");

        // SetPassword крутить SecurityStamp — чинні сесії цілі гаснуть на наступному запиті.
        target.SetPassword(hasher.Hash(newPassword));
        target.RequirePasswordChange();

        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                now, "PasswordReset", TargetUserId: target.Id, TargetRoleId: null,
                DetailsJson: null, // ⛔ ні пароля, ні хеша (ФВ-6.11)
                ChangedByUserId: actorId, CorrelationId: currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Блокування і розблокування запису. Право <c>Security.ManageUsers</c> (BE-12).</summary>
public sealed class SetUserLockHandler(
    IUserStore users,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право керування користувачами.</summary>
    public const string Permission = "Security.ManageUsers";

    /// <summary>Блокує (безстроково, з обривом сесій) або знімає блокування.</summary>
    /// <param name="userId">Ціль.</param>
    /// <param name="locked"><c>true</c> — заблокувати, <c>false</c> — розблокувати.</param>
    /// <param name="reason">Причина; обов'язкова, до 400 символів.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task HandleAsync(int userId, bool locked, string? reason, CancellationToken ct)
    {
        var (actorId, target) = await UserAdministration
            .ResolveAsync(users, access, currentUser, Permission, userId, ct).ConfigureAwait(false);

        var trimmed = UserAdministration.RequireReason(reason);
        var now = clock.UtcNow;

        // Перевірка «останнього адміністратора» і блокування — одна транзакція під замком.
        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                if (locked)
                {
                    UserAdministration.EnsureNotSelf(actorId, target);
                    await users.AcquireAdministratorGuardAsync(token).ConfigureAwait(false);
                    await UserAdministration.EnsureNotLastAdministratorAsync(users, target, now, token).ConfigureAwait(false);
                    target.LockByAdministrator();
                }
                else
                {
                    target.Unlock();
                }

                await audit.WriteSecurityEventAsync(
                    new SecurityEventRecord(
                        now, locked ? "UserLocked" : "UserUnlocked", TargetUserId: target.Id, TargetRoleId: null,
                        DetailsJson: JsonSerializer.Serialize(new { reason = trimmed }),
                        ChangedByUserId: actorId, CorrelationId: currentUser.CorrelationId),
                    token).ConfigureAwait(false);

                await uow.SaveChangesAsync(token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }
}
