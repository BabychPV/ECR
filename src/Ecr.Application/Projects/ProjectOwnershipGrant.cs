// src/Ecr.Application/Projects/ProjectOwnershipGrant.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Application.Projects;

/// <summary>
/// Видає щойно створений/клонований проєкт у володіння творцю (`Q-179` +
/// аудит-пас 5).
/// </summary>
/// <remarks>
/// ⛔ Спільний механізм для <c>CreateProjectHandler</c> і
/// <c>CloneProjectHandler</c>: обидва створюють проєкт, якого раніше не
/// існувало, і обидва відмовляли б власному творцю в
/// <c>Activate</c>/<c>Archive</c>/маршруті погодження (усі перевіряють
/// <see cref="GrantLevel.Manage"/> на конкретний <c>projectId</c>) доти,
/// доки хтось не видасть грант окремим кроком — <c>CreateProjectHandler</c>
/// це вже враховував, <c>CloneProjectHandler</c> — ні, знайдено аудитом.
///
/// ⛔ Грант прив'язаний до РОЛІ (<c>sec.ResourceGrant.RoleId</c>), не до
/// користувача. Рішення людини: видати грант КОЖНІЙ ролі творця, яка сама
/// несе <c>Project.Manage</c> — тій самій «сумі ролей», яку вже застосовує
/// <c>AccessDecisionService.LoadAsync</c> для читання грантів. Побічний
/// наслідок, свідомо прийнятий: усі, хто поділяє цю роль із творцем, теж
/// отримують доступ до нового проєкту.
///
/// ⚠ НЕ <c>RotateStampsForRoleAsync</c> — лише точкове скидання кешованого
/// профілю ЛИШЕ творця (<c>InvalidateProfileAsync</c>), без зміни штампа:
/// ротація розлоговувала б творця його ж власною дією (детальний розбір —
/// первісний коментар у <c>CreateProjectHandler</c>).
/// </remarks>
internal static class ProjectOwnershipGrant
{
    /// <summary>Видає грант <see cref="GrantLevel.Manage"/> на <paramref name="projectId"/>.</summary>
    /// <param name="users">Сховище ролей і грантів.</param>
    /// <param name="access">Сервіс перевірки доступу — для скидання кешованого профілю творця.</param>
    /// <param name="audit">Журнал подій безпеки.</param>
    /// <param name="uow">Одиниця роботи — коміт заміни грантів.</param>
    /// <param name="currentUser">Автентифікований творець проєкту/клону.</param>
    /// <param name="clock">Годинник для часу аудит-запису.</param>
    /// <param name="profile">Профіль доступу творця — джерело складу його ролей.</param>
    /// <param name="projectId">Проєкт (щойно створений або клонований), на який видається грант.</param>
    /// <param name="permission">Право, яке кваліфікує роль творця для гранта (<c>"Project.Manage"</c>).</param>
    /// <param name="reason">Причина в аудит-записі: <c>"CreateProjectOwnership"</c>/<c>"CloneProjectOwnership"</c>.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task GrantAsync(
        IUserStore users,
        IAccessDecisionService access,
        IAuditWriter audit,
        IUnitOfWork uow,
        ICurrentUser currentUser,
        IClock clock,
        AccessProfile profile,
        int projectId,
        string permission,
        string reason,
        CancellationToken ct)
    {
        var allRoles = await users.ListRolesAsync(ct).ConfigureAwait(false);
        var qualifyingRoles = allRoles
            .Where(r => profile.RoleIds.Contains(r.Id) && r.Permissions.Contains(permission))
            .ToList();

        var grantedAny = false;

        foreach (var role in qualifyingRoles)
        {
            var existing = await users.ListGrantsAsync(role.Id, ct).ConfigureAwait(false);

            // ⚠ Проєкт щойно створений/клонований — дубліката бути не може за
            // побудовою (`projectId` ще не існував ні для кого), але
            // перевірка тут коштує дешевше за мовчазний `UQ_ResourceGrant`
            // виняток, якби це припущення колись перестало виконуватися.
            if (existing.Any(g => g.ResourceKind == ResourceKind.Project && g.ResourceId == projectId))
            {
                continue;
            }

            var updated = existing
                .Append(new ResourceGrantDto(ResourceKind.Project, projectId, GrantLevel.Manage, IsDeny: false))
                .ToList();

            await users.ReplaceGrantsAsync(role.Id, updated, ct).ConfigureAwait(false);
            grantedAny = true;

            await audit.WriteSecurityEventAsync(
                new SecurityEventRecord(
                    clock.UtcNow,
                    "ResourceGrantsReplaced",
                    TargetUserId: null,
                    TargetRoleId: role.Id,
                    DetailsJson: JsonSerializer.Serialize(new
                    {
                        role = role.Code,
                        reason,
                        projectId,
                    }),
                    // ⚠ `!.Value`, не повторна перевірка: виклик відбувається
                    // лише після того, як `PermissionCheck.RequireAsync` уже
                    // вимагав автентифікованого користувача.
                    ChangedByUserId: currentUser.UserId!.Value,
                    CorrelationId: currentUser.CorrelationId),
                ct).ConfigureAwait(false);
        }

        if (grantedAny)
        {
            await access.InvalidateProfileAsync(currentUser.UserId!.Value, ct).ConfigureAwait(false);
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
