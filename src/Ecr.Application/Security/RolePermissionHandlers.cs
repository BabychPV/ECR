// src/Ecr.Application/Security/RolePermissionHandlers.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;

namespace Ecr.Application.Security;

/// <summary>Користувач, якому зміна прав ролі змінить підсумковий доступ (або ні).</summary>
/// <param name="UserId">Обліковий запис.</param>
/// <param name="UserName">Ім'я входу.</param>
/// <param name="DisplayName">Ім'я для показу.</param>
/// <param name="Gained">Права, яких у людини ще НЕМАЄ і які вона отримає (з урахуванням інших її ролей).</param>
/// <param name="Lost">Права, які людина ВТРАТИТЬ (жодна інша її роль їх не дає).</param>
/// <param name="EffectiveBefore">Скільки прав людина має зараз (усі її чинні особисті ролі).</param>
/// <param name="EffectiveAfter">Скільки матиме після збереження.</param>
public sealed record RoleImpactUser(
    int UserId,
    string UserName,
    string DisplayName,
    IReadOnlyList<string> Gained,
    IReadOnlyList<string> Lost,
    int EffectiveBefore,
    int EffectiveAfter);

/// <summary>Група каталогу з чинним призначенням ролі.</summary>
/// <param name="Sid">SID групи.</param>
/// <remarks>
/// ⚠ Членів групи система поіменно не знає (квиток лише власної сесії, <c>P-02</c>): для групи зміна —
/// це рівно додані й прибрані права самої ролі.
/// </remarks>
public sealed record RoleImpactGroup(string Sid);

/// <summary>Попередній перегляд зміни набору прав ролі (рішення людини 08.11, (v)).</summary>
/// <param name="RoleId">Роль.</param>
/// <param name="RoleCode">Код ролі.</param>
/// <param name="Version">Версія ЧИННОГО набору — її клієнт повертає в <c>If-Match</c> збереження.</param>
/// <param name="Current">Чинний набір прав ролі на момент перегляду — клієнт звіряє його з тим, від чого почав правку.</param>
/// <param name="Added">Права, що додаються.</param>
/// <param name="Removed">Права, що прибираються.</param>
/// <param name="AddedDangerous">Додані небезпечні права — збереження вимагає окремого підтвердження.</param>
/// <param name="Users">Прямі носії ролі (чинне особисте призначення) з підсумковим доступом до → після.</param>
/// <param name="Groups">Групи з чинним призначенням ролі.</param>
public sealed record RolePermissionsPreview(
    int RoleId,
    string RoleCode,
    string Version,
    IReadOnlyList<string> Current,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> AddedDangerous,
    IReadOnlyList<RoleImpactUser> Users,
    IReadOnlyList<RoleImpactGroup> Groups);

/// <summary>Прямий носій ролі з правами ІНШИХ його чинних ролей (для розрахунку «до → після»).</summary>
/// <param name="UserId">Обліковий запис.</param>
/// <param name="UserName">Ім'я входу.</param>
/// <param name="DisplayName">Ім'я для показу.</param>
/// <param name="OtherPermissions">Права з інших чинних особистих призначень активних ролей.</param>
public sealed record RoleCarrierUser(
    int UserId, string UserName, string DisplayName, IReadOnlyList<string> OtherPermissions);

/// <summary>Носії ролі на дату: прямі користувачі й групи.</summary>
/// <param name="Users">Користувачі з чинним особистим призначенням ролі.</param>
/// <param name="GroupSids">SID груп із чинним призначенням ролі.</param>
public sealed record RoleCarriers(IReadOnlyList<RoleCarrierUser> Users, IReadOnlyList<string> GroupSids);

/// <summary>Версія набору прав ролі — для <c>ETag</c> / <c>If-Match</c>.</summary>
/// <remarks>
/// ⚠ Хеш НАБОРУ, а не <c>rowversion</c> — той самий прийом, що й <see cref="ResourceGrantsVersion"/>: токена
/// конкурентності в <c>sec.Role</c> немає, міграція заради нього не потрібна. Звірка йде під <c>UPDLOCK</c> на
/// рядку ролі в тій самій транзакції, що й запис, тож друга з двох одночасних правок з однієї версії отримує 409.
/// Правка, що повернула рівно той самий набір (A → B → A), конфліктом не вважається — затирати нічого.
/// </remarks>
public static class RolePermissionsVersion
{
    /// <summary>Хеш відсортованого набору кодів прав (hex, 32 символи).</summary>
    /// <param name="permissionCodes">Коди прав ролі.</param>
    public static string Of(IEnumerable<string> permissionCodes)
    {
        ArgumentNullException.ThrowIfNull(permissionCodes);

        var text = string.Join('\n', permissionCodes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));

        return Convert.ToHexString(hash.AsSpan(0, 16));
    }
}

/// <summary>Спільні перевірки правки прав ролі.</summary>
internal static class RolePermissionRules
{
    /// <summary>Роль існує і НЕ вбудована; коди прав відомі каталогу.</summary>
    public static async Task<RoleView> RequireEditableAsync(
        IUserStore users, int roleId, IReadOnlyList<string> permissionCodes, CancellationToken ct)
    {
        var role = await users.FindRoleAsync(roleId, ct).ConfigureAwait(false) ?? throw RoleNotFound(roleId);

        // ⛔ Вбудовані ролі приходять із продуктом і значать одне й те саме в кожній установці (банер
        // `security.builtInReadOnlyText`). Сервер стереже це сам — замок у формі лише показує.
        if (role.IsBuiltIn)
        {
            throw new BusinessRuleException(
                ErrorCodes.SecurityConflict,
                $"Роль «{role.Code}» вбудована: її права змінюються лише з оновленням продукту.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SEC-0409.roleBuiltInPermissions",
                    ["code"] = role.Code,
                });
        }

        // ⛔ Невідомий код — відмова НАБОРОМ, як і при створенні ролі (`CreateRoleHandler`): зберегти «те, що
        // знайшлося», означало б роль, що мовчки не дає частини повноважень.
        var unknown = await users.FilterUnknownAsync(permissionCodes, ct).ConfigureAwait(false);
        if (unknown.Count > 0)
        {
            throw new NotFoundException(
                "ECR-SEC-0404", $"Прав не існує в каталозі: {string.Join(", ", unknown)}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SEC-0404.permissionsUnknown",
                    ["permissions"] = string.Join(", ", unknown),
                });
        }

        return role;
    }

    public static NotFoundException RoleNotFound(int roleId)
        => new(
            ErrorCodes.SecurityPrincipalNotFound, $"Ролі {roleId} не існує.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-SEC-0404.roleNotFound",
                ["roleId"] = roleId.ToString(CultureInfo.InvariantCulture),
            });

    public static string[] Normalize(IReadOnlyList<string> codes)
        => [.. codes.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}

/// <summary>
/// Попередній перегляд зміни прав власної ролі: хто з носіїв що отримає і що втратить. Право
/// <c>Security.ManageRoles</c>. Нічого не змінює.
/// </summary>
/// <remarks>
/// ⛔ Рішення людини 08.11 ((v), «чому я не можу редагувати роль?» → «так»): спершу вплив — користувачі й групи,
/// їхній підсумковий доступ до → після, — і лише потім збереження.
/// </remarks>
public sealed class PreviewRolePermissionsHandler(
    IUserStore users, IAccessDecisionService access, ICurrentUser currentUser, IClock clock)
{
    /// <summary>Рахує вплив нового набору прав на носіїв ролі.</summary>
    /// <param name="roleId">Роль.</param>
    /// <param name="permissionCodes">Новий повний набір прав.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<RolePermissionsPreview> HandleAsync(
        int roleId, IReadOnlyList<string> permissionCodes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(permissionCodes);

        await ListResourceGrantsHandler.RequireAsync(access, currentUser, ct).ConfigureAwait(false);

        var next = RolePermissionRules.Normalize(permissionCodes);
        var role = await RolePermissionRules.RequireEditableAsync(users, roleId, next, ct).ConfigureAwait(false);

        var current = RolePermissionRules.Normalize(role.Permissions);
        var added = next.Except(current, StringComparer.Ordinal).ToArray();
        var removed = current.Except(next, StringComparer.Ordinal).ToArray();
        var dangerous = added.Length == 0
            ? []
            : (await users.FilterDangerousAsync(added, ct).ConfigureAwait(false)).Order(StringComparer.Ordinal).ToArray();

        var carriers = await users
            .ListRoleCarriersAsync(roleId, DateOnly.FromDateTime(clock.UtcNow), ct)
            .ConfigureAwait(false);

        var impact = carriers.Users
            .Select(u =>
            {
                var before = new HashSet<string>(u.OtherPermissions, StringComparer.Ordinal);
                var after = new HashSet<string>(u.OtherPermissions, StringComparer.Ordinal);
                if (role.IsActive)
                {
                    before.UnionWith(current);
                    after.UnionWith(next);
                }

                return new RoleImpactUser(
                    u.UserId,
                    u.UserName,
                    u.DisplayName,
                    [.. after.Except(before, StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                    [.. before.Except(after, StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                    before.Count,
                    after.Count);
            })
            .OrderBy(u => u.UserName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new RolePermissionsPreview(
            role.Id,
            role.Code,
            RolePermissionsVersion.Of(current),
            current,
            added,
            removed,
            dangerous,
            impact,
            [.. carriers.GroupSids.Order(StringComparer.Ordinal).Select(sid => new RoleImpactGroup(sid))]);
    }
}

/// <summary>
/// Заміна набору прав власної ролі. Право <c>Security.ManageRoles</c>.
/// </summary>
/// <remarks>
/// ⛔ Рішення людини 08.11 ((v)): власні ролі правляться; вбудовані — ні (409). Додане небезпечне право вимагає
/// ОКРЕМОГО підтвердження (<c>confirmDangerous</c>), і сервер стереже це сам: клієнт, що забув діалог, отримує 409,
/// а не мовчазну видачу. Конкурентність — <c>If-Match</c> з версією набору (<see cref="RolePermissionsVersion"/>) під
/// <c>UPDLOCK</c> на рядку ролі: друга з двох правок з однієї версії — 409 <c>permissionsStale</c>, а не затирання.
/// Набір, штампи носіїв, аудит — одна транзакція (C4).
/// </remarks>
public sealed class ReplaceRolePermissionsHandler(
    IUserStore users,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock)
{
    /// <summary>Замінює набір прав ролі.</summary>
    /// <param name="roleId">Роль.</param>
    /// <param name="permissionCodes">Новий повний набір прав.</param>
    /// <param name="confirmDangerous">Адміністратор окремо підтвердив додані небезпечні права.</param>
    /// <param name="ifMatch">Версія, з якої почалася правка (<see cref="RolePermissionsPreview.Version"/>). Обов'язкова.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Версія щойно збереженого набору (новий <c>ETag</c>).</returns>
    public async Task<string> HandleAsync(
        int roleId, IReadOnlyList<string> permissionCodes, bool confirmDangerous, string? ifMatch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(permissionCodes);

        var actorId = await ListResourceGrantsHandler.RequireAsync(access, currentUser, ct).ConfigureAwait(false);

        var next = RolePermissionRules.Normalize(permissionCodes);
        await RolePermissionRules.RequireEditableAsync(users, roleId, next, ct).ConfigureAwait(false);

        var expected = Integration.ListCollectionSchedulesHandler.NormalizeETag(ifMatch)
                       ?? throw new BusinessRuleException(
                           ErrorCodes.RequestInvalid,
                           "Зміна прав ролі має нести заголовок If-Match з версією прочитаного набору.",
                           new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.rolePermissionsIfMatch" });

        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                // Блокування ПЕРШИМ: друга одночасна правка чекає тут і читає вже зафіксований набір першої.
                if (!await users.LockRoleForUpdateAsync(roleId, token).ConfigureAwait(false))
                {
                    throw RolePermissionRules.RoleNotFound(roleId);
                }

                var role = await users.FindRoleAsync(roleId, token).ConfigureAwait(false)
                           ?? throw RolePermissionRules.RoleNotFound(roleId);
                var current = RolePermissionRules.Normalize(role.Permissions);
                var actual = RolePermissionsVersion.Of(current);

                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ConcurrencyConflictException(
                        ErrorCodes.SecurityConflict,
                        $"Права ролі «{role.Code}» змінили після того, як їх прочитали.",
                        new Dictionary<string, object?>
                        {
                            ["messageKey"] = "err.ECR-SEC-0409.permissionsStale",
                            ["code"] = role.Code,
                            ["version"] = actual,
                        });
                }

                var added = next.Except(current, StringComparer.Ordinal).ToArray();
                var removed = current.Except(next, StringComparer.Ordinal).ToArray();
                if (added.Length == 0 && removed.Length == 0)
                {
                    return;
                }

                var dangerous = added.Length == 0
                    ? []
                    : (await users.FilterDangerousAsync(added, token).ConfigureAwait(false)).Order(StringComparer.Ordinal).ToArray();

                if (dangerous.Length > 0 && !confirmDangerous)
                {
                    throw new BusinessRuleException(
                        ErrorCodes.SecurityConflict,
                        $"Небезпечні права ({string.Join(", ", dangerous)}) потребують окремого підтвердження.",
                        new Dictionary<string, object?>
                        {
                            ["messageKey"] = "err.ECR-SEC-0409.dangerousPermissionsNeedConfirmation",
                            ["code"] = role.Code,
                            ["permissions"] = string.Join(", ", dangerous),
                        });
                }

                await users.ReplaceRolePermissionsAsync(roleId, next, token).ConfigureAwait(false);

                // ⛔ Профіль доступу кешується під «користувач + штамп» (30 хв): без прокрутки штампа нові права не
                // з'явилися б, а зняті — не зникли б (`A7-23`). Членів груп ловить відбиток груп ключа профілю.
                await users.RotateStampsForRoleAsync(roleId, token).ConfigureAwait(false);

                if (dangerous.Length > 0)
                {
                    await audit.WriteSecurityEventAsync(
                        new SecurityEventRecord(
                            clock.UtcNow,
                            "DangerousPermissionsGranted",
                            TargetUserId: null,
                            TargetRoleId: roleId,
                            DetailsJson: JsonSerializer.Serialize(new { role = role.Code, permissions = dangerous, confirmed = true }),
                            ChangedByUserId: actorId,
                            CorrelationId: currentUser.CorrelationId),
                        token).ConfigureAwait(false);
                }

                // (v4) Хто, яка роль, що додано й прибрано, коли — один запис журналу безпеки.
                await audit.WriteSecurityEventAsync(
                    new SecurityEventRecord(
                        clock.UtcNow,
                        "RolePermissionsChanged",
                        TargetUserId: null,
                        TargetRoleId: roleId,
                        DetailsJson: JsonSerializer.Serialize(new
                        {
                            role = role.Code,
                            added,
                            removed,
                            dangerousAdded = dangerous,
                            permissions = next,
                        }),
                        ChangedByUserId: actorId,
                        CorrelationId: currentUser.CorrelationId),
                    token).ConfigureAwait(false);

                await uow.SaveChangesAsync(token).ConfigureAwait(false);

                // ⛔ Не лишати систему без жодного, хто може керувати користувачами: рахуємо носіїв ПІСЛЯ запису, у
                // тій самій транзакції — відмова відкочує і набір, і аудит.
                if (removed.Contains(BootstrapAdmin.AdminPermission, StringComparer.Ordinal))
                {
                    await users.AcquireAdministratorGuardAsync(token).ConfigureAwait(false);
                    var holders = await users
                        .CountActivePermissionHoldersAsync(BootstrapAdmin.AdminPermission, null, clock.UtcNow, token)
                        .ConfigureAwait(false);
                    if (holders == 0)
                    {
                        throw new BusinessRuleException(
                            ErrorCodes.SecurityConflict,
                            $"Без права {BootstrapAdmin.AdminPermission} у ролі «{role.Code}» керувати користувачами не буде кому.",
                            new Dictionary<string, object?>
                            {
                                ["messageKey"] = "err.ECR-SEC-0409.roleKeepsLastAdministrator",
                                ["code"] = role.Code,
                            });
                    }
                }
            },
            ct).ConfigureAwait(false);

        return RolePermissionsVersion.Of(next);
    }
}
