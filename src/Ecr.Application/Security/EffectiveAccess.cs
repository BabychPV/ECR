// src/Ecr.Application/Security/EffectiveAccess.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Security;

/// <summary>Один внесок у підсумковий рівень: яка роль, яким призначенням і що саме дала.</summary>
/// <param name="Source"><c>Grant</c> — ресурсний грант ролі; <c>Permission</c> — глобальне функціональне право ролі.</param>
/// <param name="RoleCode">Код ролі.</param>
/// <param name="PrincipalSid">SID групи, через яку прийшла роль; <c>null</c> — призначена особисто.</param>
/// <param name="PermissionCode">Код права для <c>Permission</c>; для <c>Grant</c> — <c>null</c>.</param>
/// <param name="Level">Рівень: у гранта — його рівень, у права — той, який воно відкриває для довідника.</param>
/// <param name="IsDeny">Явна заборона.</param>
/// <param name="Scope">
/// <c>Unscoped</c> — призначення без області; <c>InScope</c> — область містить проєкт; <c>Narrowed</c> —
/// область звужена аркушами чи періодами (D-214) і рівня проєкту не піднімає; <c>OutOfScope</c> — область
/// ресурсу не містить (чи для цього виду ресурсу не діє); <c>Expired</c> — призначення не чинне на дату.
/// </param>
/// <param name="Counted">Чи бере участь внесок у підсумковий рівень профілю.</param>
public sealed record EffectiveAccessContribution(
    string Source,
    string RoleCode,
    string? PrincipalSid,
    string? PermissionCode,
    GrantLevel Level,
    bool IsDeny,
    string Scope,
    bool Counted);

/// <summary>Розріз «ресурс → підсумковий рівень → який грант якої ролі його дав» (ФВ-6.16, D-220).</summary>
/// <param name="UserId">Людина.</param>
/// <param name="UserName">Ім'я входу.</param>
/// <param name="Resource">Ресурс у формі запиту: <c>Registry:5</c>.</param>
/// <param name="Level">Підсумковий рівень — той, що дає профіль доступу (<c>AccessProfile</c>).</param>
/// <param name="IsDenied">Чи є явна заборона, що перекриває все.</param>
/// <param name="DenyReason"><c>ExplicitDeny</c> або <c>NoGrant</c>; <c>null</c> — рівень є.</param>
/// <param name="GroupsFromTicket">Чи враховані групи сесії; для чужого запису — ні (`P-02`).</param>
/// <param name="Contributions">Усі внески, включно з тими, що не порахувалися.</param>
public sealed record EffectiveAccessView(
    int UserId,
    string UserName,
    string Resource,
    GrantLevel Level,
    bool IsDenied,
    string? DenyReason,
    bool GroupsFromTicket,
    IReadOnlyList<EffectiveAccessContribution> Contributions);

/// <summary>
/// Розріз ефективного доступу людини до ресурсу з атрибуцією внесків (ФВ-6.16).
/// </summary>
/// <remarks>
/// ⚠ Обробник нічого не ВИРІШУЄ: підсумковий рівень бере з <see cref="IAccessDecisionService.BuildProfileAsync"/>
/// (<see cref="AccessProfile.LevelFor"/> і, для довідника, глобальні <c>Registry.View</c>/<c>Registry.EditData</c>,
/// як <c>RegistryAccess</c>). Внески лише пояснюють його — друга реалізація правил показувала б
/// доступ, якого немає. Підтримані ресурси — довідник і проєкт: для аркуша, таблиці й колонки рівень
/// залежить від успадкування й стану документа, і розріз без документа збрехав би.
/// </remarks>
public sealed class GetEffectiveAccessHandler(
    IUserStore users,
    IEffectiveAccessStore store,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право, без якого чужі внески не віддаються.</summary>
    public const string Permission = "Security.ManageUsers";

    private const string RegistryView = "Registry.View";
    private const string RegistryEditData = "Registry.EditData";

    /// <summary>Збирає розріз.</summary>
    /// <param name="userId">Чий доступ пояснюємо.</param>
    /// <param name="resource">Ресурс у формі <c>Registry:5</c> чи <c>Project:3</c>.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="AccessDeniedException">Анонім або немає <c>Security.ManageUsers</c>.</exception>
    /// <exception cref="NotFoundException">Немає користувача чи ресурсу.</exception>
    /// <exception cref="BusinessRuleException">Ресурс задано не у формі <c>тип:ідентифікатор</c>.</exception>
    public async Task<EffectiveAccessView> HandleAsync(int userId, string? resource, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var (kind, resourceId) = Parse(resource);

        var user = await users.FindByIdAsync(userId, ct).ConfigureAwait(false)
                   ?? throw new NotFoundException(
                       ErrorCodes.SecurityPrincipalNotFound,
                       $"Користувача {userId} не знайдено.",
                       new Dictionary<string, object?>
                       {
                           ["messageKey"] = "err.ECR-SEC-0404.userNotFound",
                           ["userId"] = userId.ToString(CultureInfo.InvariantCulture),
                       });

        if (!await store.ResourceExistsAsync(kind, resourceId, ct).ConfigureAwait(false))
        {
            throw kind == ResourceKind.Registry
                ? RegistryAccess.NotFound(resourceId)
                : new NotFoundException(
                    ErrorCodes.ProjectNotFound,
                    $"Проєкт {resourceId} не знайдено.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-PRJ-0404.project",
                        ["projectId"] = resourceId.ToString(CultureInfo.InvariantCulture),
                    });
        }

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        // ⛔ Групи — з квитка й лише для власної сесії (`P-02`): та сама умова, що в профілі й у діагностиці.
        var own = userId == currentUser.UserId;
        var groupSids = own ? currentUser.GroupSids : [];

        var rows = await store
            .ListSourcesAsync(
                userId, groupSids, kind, resourceId,
                kind == ResourceKind.Registry ? [RegistryView, RegistryEditData] : [],
                DateOnly.FromDateTime(clock.UtcNow), ct)
            .ConfigureAwait(false);

        var denied = profile.Denies.Contains($"{kind}:{resourceId.ToString(CultureInfo.InvariantCulture)}");
        var level = profile.LevelFor(kind, resourceId);

        if (kind == ResourceKind.Registry && !denied)
        {
            // Глобальне право відкриває довідник без гранта (RegistryAccess.RequireAsync), заборона — ні.
            if (profile.Has(RegistryEditData))
            {
                level = Max(level, GrantLevel.Write);
            }
            else if (profile.Has(RegistryView))
            {
                level = Max(level, GrantLevel.Read);
            }
        }

        var contributions = rows
            .Select(row => Attribute(row, kind, resourceId))
            .OrderBy(c => c.Source, StringComparer.Ordinal)
            .ThenBy(c => c.RoleCode, StringComparer.Ordinal)
            .ThenBy(c => c.PrincipalSid, StringComparer.Ordinal)
            .ToList();

        return new EffectiveAccessView(
            user.Id,
            user.UserName,
            $"{kind}:{resourceId.ToString(CultureInfo.InvariantCulture)}",
            level,
            denied,
            denied ? "ExplicitDeny" : level == GrantLevel.None ? "NoGrant" : null,
            own,
            contributions);
    }

    private static GrantLevel Max(GrantLevel a, GrantLevel b) => a > b ? a : b;

    private static (ResourceKind Kind, int Id) Parse(string? resource)
    {
        var parts = (resource ?? string.Empty).Split(':', 2);

        if (parts.Length == 2
            && Enum.TryParse(parts[0].Trim(), ignoreCase: true, out ResourceKind kind)
            && kind is ResourceKind.Registry or ResourceKind.Project
            && int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && id > 0)
        {
            return (kind, id);
        }

        throw new BusinessRuleException(
            ErrorCodes.RequestInvalid,
            "Ресурс задається як «Registry:{id}» або «Project:{id}».",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "err.ECR-REQ-0422.effectiveAccessResource",
            });
    }

    /// <summary>Класифікує рядок так само, як <c>AccessDecisionService.LoadAsync</c> вирішує, чи брати його.</summary>
    private static EffectiveAccessContribution Attribute(AccessSourceRow row, ResourceKind kind, int resourceId)
    {
        var isGrant = row.PermissionCode is null;
        var scope = ClassifyScope(row, kind, resourceId, isGrant);

        // Глобальне право відкриває довідник рівнем, який воно дає, а не рівнем гранта.
        var level = row.Level
                    ?? (row.PermissionCode == RegistryEditData ? GrantLevel.Write : GrantLevel.Read);

        return new EffectiveAccessContribution(
            isGrant ? "Grant" : "Permission",
            row.RoleCode,
            row.PrincipalSid,
            row.PermissionCode,
            level,
            row.IsDeny,
            scope,
            scope is "Unscoped" or "InScope");
    }

    private static string ClassifyScope(AccessSourceRow row, ResourceKind kind, int resourceId, bool isGrant)
    {
        if (!row.IsEffective)
        {
            return "Expired";
        }

        if (row.ScopeJson is null)
        {
            return "Unscoped";
        }

        // Функціональні права ролі з областю глобальні (Registry.*) — не діють; гранти на довідник — теж.
        if (!isGrant || kind != ResourceKind.Project)
        {
            return "OutOfScope";
        }

        var scope = RoleAssignmentScope.TryParse(row.ScopeJson);
        if (scope is null || !scope.ProjectIds.Contains(resourceId))
        {
            return "OutOfScope";
        }

        return scope.IsNarrowed ? "Narrowed" : "InScope";
    }
}
