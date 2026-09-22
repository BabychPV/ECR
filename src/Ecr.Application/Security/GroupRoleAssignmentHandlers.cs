using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Errors;

namespace Ecr.Application.Security;

/// <summary>Призначення ролі групі каталогу — рядок екрана керування.</summary>
/// <param name="Id">Ідентифікатор призначення; ним і відкликають.</param>
/// <param name="RoleId">Роль.</param>
/// <param name="RoleCode">Код ролі.</param>
/// <param name="PrincipalSid">SID групи — зберігається завжди він.</param>
/// <param name="PrincipalName">Ім'я групи; <c>null</c> — каталог його не назвав.</param>
/// <param name="ValidFrom">Початок дії; <c>null</c> — від завжди.</param>
/// <param name="ValidTo">Кінець дії (включно); <c>null</c> — безстроково.</param>
public sealed record GroupRoleAssignmentView(
    int Id, int RoleId, string RoleCode, string PrincipalSid, string? PrincipalName,
    DateOnly? ValidFrom, DateOnly? ValidTo);

/// <summary>Наслідок призначення ролі групі.</summary>
/// <param name="Id">Ідентифікатор нового призначення.</param>
/// <param name="PrincipalSid">SID, під яким воно збережене.</param>
/// <param name="PrincipalName">Ім'я групи; <c>null</c> — не резолвиться.</param>
/// <param name="EffectiveAfterNextSignIn">
/// <c>true</c> — на цей SID досі не було жодного призначення, тож у cookie вже
/// залогінених членів групи його немає (`#419`: у квиток кладуться лише групи
/// з призначеннями), і роль подіє для них з НАСТУПНОГО входу.
/// </param>
public sealed record GroupRoleAssignedResult(
    int Id, string PrincipalSid, string? PrincipalName, bool EffectiveAfterNextSignIn);

/// <summary>Перелік групових призначень. Право <c>Security.ManageUsers</c>.</summary>
public sealed class ListGroupRoleAssignmentsHandler(
    IUserStore users, IPrincipalNameResolver resolver, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Повертає призначення з резолвленими іменами груп.</summary>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<GroupRoleAssignmentView>> HandleAsync(CancellationToken ct)
    {
        await PermissionCheck
            .RequireAsync(access, currentUser, ReplaceUserRolesHandler.Permission, ct).ConfigureAwait(false);

        var rows = await users.ListGroupRoleAssignmentsAsync(ct).ConfigureAwait(false);

        // ⚠ Резолв — раз на SID, а не на рядок: одна група зазвичай несе кілька ролей.
        var names = rows
            .Select(r => r.PrincipalSid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(sid => sid, resolver.ResolveName, StringComparer.OrdinalIgnoreCase);

        return [.. rows.Select(r => r with { PrincipalName = names[r.PrincipalSid] })];
    }
}

/// <summary>Призначення ролі групі каталогу. Право <c>Security.ManageUsers</c>.</summary>
public sealed partial class AssignGroupRoleHandler(
    IUserStore users,
    IPrincipalNameResolver resolver,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Призначає роль групі.</summary>
    /// <param name="roleId">Роль.</param>
    /// <param name="principal">SID (<c>S-1-…</c>) або ім'я <c>ДОМЕН\Група</c>.</param>
    /// <param name="validFrom">Початок дії; <c>null</c> — від завжди.</param>
    /// <param name="validTo">Кінець дії; <c>null</c> — безстроково.</param>
    /// <param name="confirmDangerous">Підтвердження видачі ролі з небезпечними правами.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<GroupRoleAssignedResult> HandleAsync(
        int roleId, string principal, DateOnly? validFrom, DateOnly? validTo, bool confirmDangerous,
        CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, ReplaceUserRolesHandler.Permission, ct).ConfigureAwait(false);

        if (validFrom is { } from && validTo is { } to && from > to)
        {
            throw Invalid("err.ECR-REQ-0422.validityOrder", $"Початок дії ({from}) пізніше за кінець ({to}).", null);
        }

        var role = await RoleLifecycle.FindAsync(users, roleId, ct).ConfigureAwait(false);
        var (sid, name) = Resolve(principal);

        // ⛔ Небезпечне право видають поіменно (`ФВ-6.12`), а група — це
        // «усім, кого туди додасть відділ AD», тобто видача без імен. Тому не
        // заборона, а явне підтвердження з переліком того, що саме роздається.
        var dangerous = await users.FilterDangerousAsync(role.Permissions, ct).ConfigureAwait(false);
        if (dangerous.Count > 0 && !confirmDangerous)
        {
            throw new BusinessRuleException(
                ErrorCodes.SecurityConflict,
                $"Роль «{role.Code}» несе небезпечні права: {string.Join(", ", dangerous)}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SEC-0409.dangerousRoleNeedsConfirmation",
                    ["code"] = role.Code,
                    ["permissions"] = string.Join(", ", dangerous),
                    ["dangerousPermissions"] = dangerous,
                });
        }

        var existing = (await users.ListGroupRoleAssignmentsAsync(ct).ConfigureAwait(false))
            .Where(a => string.Equals(a.PrincipalSid, sid, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (existing.Exists(a => a.RoleId == roleId))
        {
            throw new BusinessRuleException(
                ErrorCodes.SecurityConflict, $"Роль «{role.Code}» уже призначена групі {sid}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SEC-0409.groupAssignmentExists",
                    ["code"] = role.Code,
                    ["sid"] = sid,
                });
        }

        var assignment = new RoleAssignment(roleId, userId: null, principalSid: sid);
        assignment.SetValidity(validFrom, validTo);
        users.AddGroupAssignment(assignment);

        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow,
                "GroupRoleAssigned",
                TargetUserId: null,
                TargetRoleId: roleId,
                DetailsJson: JsonSerializer.Serialize(
                    new { role = role.Code, sid, name, validFrom, validTo, dangerous }),
                ChangedByUserId: profile.UserId,
                CorrelationId: currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return new GroupRoleAssignedResult(assignment.Id, sid, name, EffectiveAfterNextSignIn: existing.Count == 0);
    }

    /// <summary>SID і (якщо вдалося) ім'я з того, що набрав адміністратор.</summary>
    /// <remarks>
    /// ⚠ SID приймається БЕЗ резолву: домен може бути недоступний саме тоді,
    /// коли доступ треба видати. Ім'я без SID не приймається — зберігати нема чого.
    /// </remarks>
    private (string Sid, string? Name) Resolve(string principal)
    {
        var text = (principal ?? string.Empty).Trim();

        if (text.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            if (!SidPattern().IsMatch(text))
            {
                throw Invalid("err.ECR-REQ-0422.principalSidMalformed", $"«{text}» не є SID.", text);
            }

            var canonical = text.ToUpperInvariant();
            return (canonical, resolver.ResolveName(canonical));
        }

        var sid = text.Length == 0 ? null : resolver.ResolveSid(text);

        return sid is null
            ? throw Invalid("err.ECR-REQ-0422.principalNotResolved", $"Групу «{text}» не знайдено в каталозі.", text)
            : (sid.ToUpperInvariant(), text);
    }

    private static BusinessRuleException Invalid(string messageKey, string message, string? principal)
        => new(
            ErrorCodes.RequestInvalid, message,
            new Dictionary<string, object?> { ["messageKey"] = messageKey, ["principal"] = principal });

    [GeneratedRegex(@"^S-1-\d+(-\d+){1,14}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SidPattern();
}

/// <summary>Відкликання ролі в групи. Право <c>Security.ManageUsers</c>.</summary>
/// <remarks>
/// ⚠ Діє на вже залогінених НЕГАЙНО і без жодного виклику звідси: ревізія
/// групових призначень сесії входить у ключ кешу профілю
/// (<c>AccessDecisionService.BuildProfileAsync</c>), тож запис із відкликаною
/// роллю просто перестає адресуватися. Штампів крутити нема кому — членів
/// групи система поіменно не знає (<c>RotateStampsForRoleAsync</c>).
/// </remarks>
public sealed class RevokeGroupRoleHandler(
    IUserStore users,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Прибирає групове призначення.</summary>
    /// <param name="assignmentId">Ідентифікатор призначення.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task HandleAsync(int assignmentId, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, ReplaceUserRolesHandler.Permission, ct).ConfigureAwait(false);

        var assignment = await users.FindGroupAssignmentAsync(assignmentId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.SecurityPrincipalNotFound, $"Групового призначення {assignmentId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SEC-0404.groupAssignmentNotFound",
                    ["id"] = assignmentId.ToString(CultureInfo.InvariantCulture),
                });

        users.RemoveGroupAssignment(assignment);

        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow,
                "GroupRoleRevoked",
                TargetUserId: null,
                TargetRoleId: assignment.RoleId,
                DetailsJson: JsonSerializer.Serialize(new { sid = assignment.PrincipalSid }),
                ChangedByUserId: profile.UserId,
                CorrelationId: currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
