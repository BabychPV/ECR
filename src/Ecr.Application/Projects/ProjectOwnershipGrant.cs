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
    /// <summary>
    /// Створює проєкт (<paramref name="createProject"/>) і видає його у
    /// володіння творцю — ОДНІЄЮ транзакцією.
    /// </summary>
    /// <param name="users">Сховище ролей і грантів.</param>
    /// <param name="access">Сервіс перевірки доступу — для скидання кешованого профілю творця.</param>
    /// <param name="audit">Журнал подій безпеки.</param>
    /// <param name="uow">Одиниця роботи — транзакція і коміт.</param>
    /// <param name="currentUser">Автентифікований творець проєкту/клону.</param>
    /// <param name="clock">Годинник для часу аудит-запису.</param>
    /// <param name="profile">Профіль доступу творця — джерело складу його ролей.</param>
    /// <param name="permission">Право, яке кваліфікує роль творця для гранта (<c>"Project.Manage"</c>).</param>
    /// <param name="reason">Причина в аудит-записі: <c>"CreateProjectOwnership"</c>/<c>"CloneProjectOwnership"</c>.</param>
    /// <param name="createProject">
    /// Запис проєкту (і його власного аудиту) усередині транзакції; повертає
    /// Id — тобто мусить сам викликати <c>SaveChangesAsync</c>, щоб identity
    /// було видано. Викликається повторно, якщо стратегія повторів перезапустить
    /// транзакцію (наприклад, після дедлоку), тому сутність проєкту має
    /// будуватися ВСЕРЕДИНІ, а не захоплюватися ззовні.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Id створеного проєкту.</returns>
    /// <remarks>
    /// ⛔ Доти проєкт комітився ОКРЕМО від гранта власності: збій на видачі
    /// (роль, дедлок, мережа) лишав закомічений проєкт без жодного гранта —
    /// його не бачив ніхто, крім глобальних прав, і його не можна було
    /// навіть відкрити, щоб видалити. Тепер проєкт, аудит і грант — один
    /// коміт: або все, або нічого.
    /// </remarks>
    public static async Task<int> CreateOwnedAsync(
        IUserStore users,
        IAccessDecisionService access,
        IAuditWriter audit,
        IUnitOfWork uow,
        ICurrentUser currentUser,
        IClock clock,
        AccessProfile profile,
        string permission,
        string reason,
        Func<CancellationToken, Task<int>> createProject,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(createProject);

        var allRoles = await users.ListRolesAsync(ct).ConfigureAwait(false);

        // ⚠ Порядок за Id — частина протоколу блокувань (див. нижче).
        var qualifyingRoles = allRoles
            .Where(r => profile.RoleIds.Contains(r.Id) && r.Permissions.Contains(permission))
            .OrderBy(r => r.Id)
            .ToList();

        var grantedAny = false;
        var projectId = 0;

        // ⛔ Проєкт, його аудит, read-then-replace набору грантів ролі й аудит
        // гранта — ОДНА транзакція. Набір грантів читається під UPDLOCK на
        // рядку ролі: паралельний `PUT /roles/{id}/grants`, що зафіксувався
        // між читанням `existing` і заміною, інакше мовчки зникав би.
        //
        // ⛔ Порядок блокувань (без дедлоку з `ReplaceResourceGrantsHandler`):
        // серед ресурсів безпеки обидва шляхи беруть рядок `sec.Role` ПЕРШИМ,
        // і лише потім торкаються `sec.ResourceGrant` цієї ролі; PUT бере рівно
        // одну роль, тут — кілька, але завжди за зростанням Id. Жоден шлях не
        // чекає на роль із меншим Id, тримаючи роль із більшим, — циклу немає.
        // Рядки `sec.User` (штампи) бере лише PUT і вже під своєю роллю.
        // Новий рядок `doc.Project` (узятий тут ДО ролей) PUT не торкається
        // взагалі, а два створення, що зіткнулися на `UQ_Project_Code`, ще не
        // тримають жодної ролі — тож і він циклу не замикає.
        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                grantedAny = false;

                // ⚠ Id проєкту (identity) видається `SaveChangesAsync`
                // усередині `createProject` — до гранта, у тій самій транзакції.
                projectId = await createProject(token).ConfigureAwait(false);

                foreach (var role in qualifyingRoles)
                {
                    if (!await users.LockRoleForUpdateAsync(role.Id, token).ConfigureAwait(false))
                    {
                        // Роль видалили між читанням профілю й блокуванням —
                        // видавати грант нікому.
                        continue;
                    }

                    grantedAny |= await GrantToRoleAsync(
                        users, audit, currentUser, clock, role, projectId, reason, token).ConfigureAwait(false);
                }

                await uow.SaveChangesAsync(token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        // ⚠ Точкове скидання — лише ЦЬОГО інстансу. Носії ролі через групу AD
        // (творець теж, якщо роль у нього групова) побачать грант на
        // наступному запиті й на інших інстансах: заміна грантів рухає
        // ревізію у відбитку груп ключа профілю (`GroupsFingerprintAsync`).
        // Прямі носії на інших інстансах — після TTL; це розширення доступу,
        // не залишковий знятий доступ.
        // ⚠ Після коміту: скинутий до коміту профіль міг би перебудуватися
        // паралельним запитом ще без гранта й лягти в кеш.
        if (grantedAny)
        {
            await access.InvalidateProfileAsync(currentUser.UserId!.Value, ct).ConfigureAwait(false);
        }

        return projectId;
    }

    private static async Task<bool> GrantToRoleAsync(
        IUserStore users,
        IAuditWriter audit,
        ICurrentUser currentUser,
        IClock clock,
        RoleView role,
        int projectId,
        string reason,
        CancellationToken ct)
    {
        // ⚠ Читання ПІСЛЯ блокування: під RCSI оператор бачить заміну,
        // що зафіксувалася, поки ми чекали на UPDLOCK.
        var existing = await users.ListGrantsAsync(role.Id, ct).ConfigureAwait(false);

        // ⚠ Проєкт щойно створений/клонований — дубліката бути не може за
        // побудовою (`projectId` ще не існував ні для кого), але
        // перевірка тут коштує дешевше за мовчазний `UQ_ResourceGrant`
        // виняток, якби це припущення колись перестало виконуватися.
        if (existing.Any(g => g.ResourceKind == ResourceKind.Project && g.ResourceId == projectId))
        {
            return false;
        }

        var updated = existing
            .Append(new ResourceGrantDto(ResourceKind.Project, projectId, GrantLevel.Manage, IsDeny: false))
            .ToList();

        await users.ReplaceGrantsAsync(role.Id, updated, ct).ConfigureAwait(false);

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

        return true;
    }
}
