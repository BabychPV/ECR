using Ecr.Application.Common;
using Ecr.Application.Errors;

namespace Ecr.Application.Security;

/// <summary>
/// Перевірка функціонального права в обробнику.
/// </summary>
/// <remarks>
/// ⛔ Право перевіряється через <see cref="IAccessDecisionService"/>, а не
/// атрибутом із назвою ролі: перевірка ролі поза єдиною точкою рішення
/// заборонена архітектурним правилом 7. Атрибут бачить лише ім'я політики, а
/// доступ у ECR залежить від ресурсу.
///
/// ⚠ Помічник з'явився після <c>A7-53</c>: дев'ять ендпоінтів оголошували
/// право в контракті й не перевіряли НІЧОГО, крім <c>[Authorize]</c>. Три
/// майже однакові приватні помічники вже існували — у шаблонів, документів і
/// безпеки, — і кожен новий обробник мав вибрати, який із них позичити. Той,
/// хто не вибрав жодного, не перевіряв узагалі, і жоден сторож цього не бачив.
/// </remarks>
public static class PermissionCheck
{
    /// <summary>Вимагає право; повертає профіль, щоб не будувати його двічі.</summary>
    /// <param name="access">Служба рішень про доступ.</param>
    /// <param name="currentUser">Поточний користувач запиту.</param>
    /// <param name="permission">Код права з таблиці ендпоінтів (<c>02-contracts.md</c> §9).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="AccessDeniedException">Анонім або немає права.</exception>
    public static Task<AccessProfile> RequireAsync(
        IAccessDecisionService access, ICurrentUser currentUser, string permission, CancellationToken ct)
        => RequireAnyAsync(access, currentUser, [permission], ct);

    /// <summary>Вимагає бодай одне з прав; у відмові названо перше — основне.</summary>
    /// <remarks>
    /// ⚠ Для читання, яке відкриває і право перегляду, і ширше право керування
    /// (<c>Integration.Manage</c> включає <c>Integration.View</c>): інакше
    /// користувач, що створює з'єднання, не бачить їхнього переліку.
    /// </remarks>
    /// <param name="access">Служба рішень про доступ.</param>
    /// <param name="currentUser">Поточний користувач запиту.</param>
    /// <param name="permissions">Коди прав; перше потрапляє в текст відмови.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="AccessDeniedException">Анонім або немає жодного з прав.</exception>
    public static async Task<AccessProfile> RequireAnyAsync(
        IAccessDecisionService access, ICurrentUser currentUser, IReadOnlyList<string> permissions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(permissions);
        var permission = permissions[0];
        ArgumentNullException.ThrowIfNull(currentUser);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Потрібна автентифікація.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        if (!permissions.Any(profile.Has))
        {
            // ⚠ У повідомленні — КОД ПРАВА, а не «недостатньо прав»: інакше
            // адміністратор не знає, що саме видати, і питання приходить до
            // розробника.
            //
            // ⛔ `.Message` лишається українською навмисно як сирий, запасний
            // текст для журналу — резолвер повертається до нього, коли ключа
            // немає в каталозі (`Q-341`). Клієнтську `Detail` будує
            // `err.ECR-AUTH-0403.permission` (узагальнений шлях,
            // `ResolveGenericMessageAsync`) із коду права в
            // `Details["permission"]`.
            //
            // ✎ 2026-09-25 (B-14, security): раніше тут НЕ було `messageKey` —
            // запис 2026-09-20 у `contracts/localization-debt.md` пояснював
            // це тим, що `ExceptionHandlingMiddleware` уже локалізує
            // `ECR-AUTH-0403` зі старшого точкового шляху
            // (`LocalizedDetailAsync`, гілка `permission` без `messageKey`).
            // Той шлях і досі живий, але виявився винятком, не правилом: усі
            // ІНШІ виклики того самого факту («бракує права X») — RoleAndUserHandlers,
            // DocumentQueryHandlers, GetTableSliceHandler, ProjectQueryHandlers,
            // ReopenDocumentHandler, ReopenPeriodHandler, RegistryAccess,
            // ResourceGrantHandlers.RequireAsync, StartSimulationHandler — уже
            // несуть `err.ECR-AUTH-0403.permission` явно. Лишати саме цей,
            // найстаріший виклик без ключа означало розходження, не свідоме
            // рішення: старий запис застарів, ключ додано для узгодженості.
            throw new AccessDeniedException(
                "ECR-AUTH-0403",
                $"Потрібне право {permission}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.permission",
                    ["permission"] = permission,
                });
        }

        return profile;
    }

    /// <summary>
    /// Вхід у проєктну дію (ФВ-6.14): право є бодай у якомусь проєкті.
    /// Відмова — та сама, що й у <see cref="RequireAsync"/>.
    /// </summary>
    /// <remarks>
    /// ⛔ Лише вхід. Щойно проєкт ресурсу відомий, — <see cref="RequireIn"/>
    /// (або <see cref="AccessProfile.Has(string, int)"/>). Сторож
    /// <c>ProjectPermissionCheckTests</c> вимагає пари в тому самому методі.
    /// Для користувача з роллю без області поведінка та сама, що й була.
    /// </remarks>
    /// <param name="access">Служба рішень про доступ.</param>
    /// <param name="currentUser">Поточний користувач запиту.</param>
    /// <param name="permission">Код проєктного права.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<AccessProfile> RequireInAnyProjectAsync(
        IAccessDecisionService access, ICurrentUser currentUser, string permission, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(currentUser);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Потрібна автентифікація.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        return profile.HasInAnyProject(permission) ? profile : throw Denied(permission);
    }

    /// <summary>Вимагає проєктне право в конкретному проєкті (ФВ-6.14).</summary>
    /// <param name="profile">Профіль.</param>
    /// <param name="permission">Код права.</param>
    /// <param name="projectId">Проєкт ресурсу.</param>
    /// <exception cref="AccessDeniedException">Права в цьому проєкті немає — <c>403 ECR-AUTH-0403</c>.</exception>
    public static void RequireIn(AccessProfile profile, string permission, int projectId)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!profile.Has(permission, projectId))
        {
            throw Denied(permission);
        }
    }

    /// <summary>Чи має профіль проєктне право в проєкті — для фільтра рядків переліку (ФВ-6.14).</summary>
    /// <param name="profile">Профіль.</param>
    /// <param name="permission">Код права.</param>
    /// <param name="projectId">Проєкт ресурсу.</param>
    /// <remarks>
    /// ⚠ Не кидає: елемент без права просто не потрапляє в перелік. Рішення про ОДИН ресурс —
    /// <see cref="RequireIn"/> (відмова має причину). Храповик ФВ-6.8: викликач не кличе
    /// <c>profile.Has(</c> напряму.
    /// </remarks>
    public static bool IsGrantedIn(AccessProfile profile, string permission, int projectId)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.Has(permission, projectId);
    }
    private static AccessDeniedException Denied(string permission)
        => new(
            "ECR-AUTH-0403",
            $"Потрібне право {permission}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-AUTH-0403.permission",
                ["permission"] = permission,
            });
}
