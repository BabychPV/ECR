using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Application.Security;

/// <summary>
/// Одне призначення ролі — так, як його бачить діагностика доступу.
/// </summary>
/// <param name="RoleId">Роль.</param>
/// <param name="RoleCode">Код ролі; його називають в аудиті й у грантах.</param>
/// <param name="PrincipalSid">SID AD-групи; <c>null</c> — призначення особисте.</param>
/// <param name="ValidFrom">Початок дії; <c>null</c> — від завжди.</param>
/// <param name="ValidTo">Кінець дії; <c>null</c> — безстроково.</param>
/// <param name="IsEffective">Чи діє призначення на дату запиту.</param>
/// <remarks>
/// ⛔ Нечинні призначення теж потрапляють сюди. «Роль була, але підміна на час
/// відпустки скінчилася» — це відповідь на «чому в мене немає доступу»;
/// «ролей немає» — не відповідь, а та сама тиша, яку прибирає `H-21`.
/// </remarks>
public sealed record RoleAssignmentTrace(
    int RoleId,
    string RoleCode,
    string? PrincipalSid,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    bool IsEffective);

/// <summary>SID групи з квитка і те, що він дав.</summary>
/// <param name="Sid">SID групи безпеки.</param>
/// <param name="Matched">Чи знайшлося ЧИННЕ призначення на цей SID.</param>
/// <param name="RoleCodes">Ролі, які з нього вийшли; порожньо — жодної.</param>
public sealed record GroupSidView(string Sid, bool Matched, IReadOnlyList<string> RoleCodes);

/// <summary>Групове призначення, яке існує в системі.</summary>
/// <param name="Sid">SID AD-групи.</param>
/// <param name="RoleCodes">Ролі, які отримує член цієї групи.</param>
public sealed record GroupAssignmentView(string Sid, IReadOnlyList<string> RoleCodes);

/// <summary>
/// Відповідь на питання «чому в мене немає доступу» (`H-21`).
/// </summary>
/// <param name="UserId">Обліковий запис, про який відповідь.</param>
/// <param name="UserName">Ім'я входу.</param>
/// <param name="Provider">Провайдер входу: доменний чи локальний.</param>
/// <param name="PrincipalSid">SID самого запису в каталозі; <c>null</c> у локального.</param>
/// <param name="GroupsFromTicket">
/// Чи взято перелік груп із квитка ЦІЄЇ сесії. <c>false</c> означає «ми не
/// знаємо», а не «людина в жодній групі не перебуває».
/// </param>
/// <param name="Groups">Усі SID груп із квитка — і ті, що збіглися, і ті, що ні.</param>
/// <param name="UnmatchedSids">SID, на які немає жодного чинного призначення.</param>
/// <param name="PersonalRoleCodes">Ролі, призначені особисто (не через групу).</param>
/// <param name="EffectiveRoleCodes">Ролі, які людина має насправді.</param>
/// <param name="ExpiredRoleCodes">
/// Ролі, призначення яких існує, але вже (або ще) не діє — строкова підміна.
/// </param>
/// <param name="GroupAssignmentsInSystem">
/// Які групи взагалі щось дають. Заповнюється лише носієві
/// <c>Security.ManageUsers</c>.
/// </param>
public sealed record AccessDiagnosticsView(
    int UserId,
    string UserName,
    AuthProvider Provider,
    string? PrincipalSid,
    bool GroupsFromTicket,
    IReadOnlyList<GroupSidView> Groups,
    IReadOnlyList<string> UnmatchedSids,
    IReadOnlyList<string> PersonalRoleCodes,
    IReadOnlyList<string> EffectiveRoleCodes,
    IReadOnlyList<string> ExpiredRoleCodes,
    IReadOnlyList<GroupAssignmentView> GroupAssignmentsInSystem);

/// <summary>
/// Діагностика доступу: звідки взялися (або не взялися) ролі людини.
/// </summary>
/// <remarks>
/// ⛔ Обробник існує через те, що збій рольової моделі на живому домені
/// <b>не відрізняється від справної системи</b>. Доменної автентифікації в
/// контурі ще немає; у продуктиві більшість користувачів отримає нуль ролей,
/// і виглядатиме це рівно так, як виглядає справна система без даних: людина
/// входить, бачить порожні переліки і йде до адміністратора, а той — до
/// розробника. Екран перетворює тишу на відповідь.
///
/// ⚠ Обробник нічого не ВИРІШУЄ про доступ — він показує, як рішення вже
/// ухвалене (`AccessDecisionService.LoadAsync`). Друга реалізація правил
/// зіставлення тут була б гіршою за відсутність екрана: вона показувала б
/// доступ, якого немає.
/// </remarks>
public sealed class GetAccessDiagnosticsHandler(
    IUserStore users,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право, без якого чужа діагностика не віддається.</summary>
    public const string Permission = "Security.ManageUsers";

    /// <summary>Збирає діагностику доступу.</summary>
    /// <param name="subjectUserId">
    /// Чий доступ пояснюємо; <c>null</c> — власний.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="AccessDeniedException">Анонім, або чужий запис без права.</exception>
    /// <exception cref="NotFoundException">Запису не існує.</exception>
    public async Task<AccessDiagnosticsView> HandleAsync(int? subjectUserId, CancellationToken ct)
    {
        var actorId = currentUser.UserId
                      ?? throw new AccessDeniedException("ECR-AUTH-0401", "Потрібна автентифікація.");

        var subjectId = subjectUserId ?? actorId;
        var own = subjectId == actorId;

        // ⛔ Право потрібне рівно для ЧУЖОГО запису. Власні групи людина бачить
        // у своєму ж квитку, і вимагати на них `Security.ManageUsers` означало
        // б лишити без відповіді саме тих, заради кого екран існує: рядових
        // співробітників, які після входу бачать порожні екрани.
        var profile = own
            ? await access.BuildProfileAsync(actorId, ct).ConfigureAwait(false)
            : await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var user = await users.FindByIdAsync(subjectId, ct).ConfigureAwait(false)
                   ?? throw new NotFoundException("ECR-SEC-0404", $"Користувача {subjectId} не знайдено.");

        // ⛔ Групи беруться З КВИТКА і лише для власної сесії (`ФВ-6.15a`,
        // `P-02`). Для чужого запису перелік порожній НЕ тому, що людина ні в
        // яких групах не перебуває, — токена її сесії в нас просто немає. Саме
        // тому нижче їде `GroupsFromTicket`: без цього прапорця порожній
        // перелік брехав би впевненіше, ніж мовчання.
        var groupSids = own ? currentUser.GroupSids : [];

        var today = DateOnly.FromDateTime(clock.UtcNow);
        var assignments = await users
            .ListAssignmentsAsync(subjectId, groupSids, today, ct)
            .ConfigureAwait(false);

        var groups = groupSids
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(sid => new GroupSidView(
                sid,
                assignments.Any(a => a.IsEffective && SameSid(a.PrincipalSid, sid)),
                [.. assignments
                    .Where(a => a.IsEffective && SameSid(a.PrincipalSid, sid))
                    .Select(a => a.RoleCode)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)]))
            .ToList();

        var effective = assignments
            .Where(a => a.IsEffective)
            .Select(a => a.RoleCode)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // ⚠ Роль потрапляє сюди, лише якщо ІНШОГО, чинного призначення на неї
        // немає: інакше «строкова підміна скінчилася» стояло б поруч із тією
        // самою роллю в переліку діючих і читалося б як суперечність.
        var expired = assignments
            .Where(a => !a.IsEffective)
            .Select(a => a.RoleCode)
            .Distinct(StringComparer.Ordinal)
            .Where(code => !effective.Contains(code, StringComparer.Ordinal))
            .ToList();

        // ⚠ Каталог групових призначень — це відомість «яка AD-група дає
        // адміністративну роль». Рядовому користувачеві вона нічого не
        // пояснює, а зловмисникові називає ціль, тож їде лише носієві права.
        var catalogue = profile.Has(Permission)
            ? await GroupCatalogueAsync(today, ct).ConfigureAwait(false)
            : [];

        return new AccessDiagnosticsView(
            user.Id,
            user.UserName,
            user.Provider,
            user.WindowsSid,
            own,
            groups,
            [.. groups.Where(g => !g.Matched).Select(g => g.Sid).Order(StringComparer.Ordinal)],
            [.. assignments
                .Where(a => a.PrincipalSid is null && a.IsEffective)
                .Select(a => a.RoleCode)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)],
            [.. effective.Order(StringComparer.Ordinal)],
            [.. expired.Order(StringComparer.Ordinal)],
            catalogue);
    }

    /// <summary>Які групи взагалі щось дають.</summary>
    /// <param name="today">Дата, на яку рахується чинність.</param>
    /// <param name="ct">Токен скасування.</param>
    private async Task<IReadOnlyList<GroupAssignmentView>> GroupCatalogueAsync(
        DateOnly today, CancellationToken ct)
    {
        var rows = await users.ListGroupAssignmentsAsync(today, ct).ConfigureAwait(false);

        return [.. rows
            .Where(a => a.IsEffective && a.PrincipalSid is not null)
            .GroupBy(a => a.PrincipalSid!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GroupAssignmentView(
                g.Key,
                [.. g.Select(a => a.RoleCode).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]))
            .OrderBy(g => g.Sid, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Чи це той самий SID.
    /// </summary>
    /// <param name="assignmentSid">SID із призначення; <c>null</c> — особисте.</param>
    /// <param name="ticketSid">SID із квитка.</param>
    /// <remarks>
    /// ⛔ Без урахування регістру — рівно так, як зіставляє SQL Server із
    /// типовим порівнянням, а зіставляє саме він (`AccessDecisionService`
    /// віддає умову в запит). Ordinal тут показував би «не збіглося» там, де
    /// доступ насправді виданий, — тобто екран, заведений проти тиші,
    /// породжував би власну неправду.
    /// </remarks>
    private static bool SameSid(string? assignmentSid, string ticketSid)
        => assignmentSid is not null
           && string.Equals(assignmentSid, ticketSid, StringComparison.OrdinalIgnoreCase);
}
