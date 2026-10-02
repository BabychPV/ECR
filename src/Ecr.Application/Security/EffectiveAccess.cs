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
/// <param name="InheritedFrom">
/// Для аркуша, таблиці й колонки — предок, на якому стоїть грант (<c>Project:3</c>, <c>Sheet:7</c>), якщо це
/// не сам запитаний ресурс; інакше <c>null</c>.
/// </param>
/// <param name="NarrowedBy">Для <c>Narrowed</c>: чим звужено — <c>Sheets:F1,F2</c>, <c>Periods:from..to</c>; інакше <c>null</c>.</param>
/// <param name="NotCountedReason"><c>ProjectNotVisible</c> — грант нижче проєкту без видимого проєкту (S2); інакше <c>null</c>.</param>
public sealed record EffectiveAccessContribution(
    string Source,
    string RoleCode,
    string? PrincipalSid,
    string? PermissionCode,
    GrantLevel Level,
    bool IsDeny,
    string Scope,
    bool Counted,
    string? InheritedFrom = null,
    string? NarrowedBy = null,
    string? NotCountedReason = null);

/// <summary>Розріз «ресурс → підсумковий рівень → який грант якої ролі його дав» (ФВ-6.16, D-220).</summary>
/// <param name="UserId">Людина.</param>
/// <param name="UserName">Ім'я входу.</param>
/// <param name="Resource">Ресурс у формі запиту: <c>Registry:5</c>.</param>
/// <param name="Level">Підсумковий рівень — той, що дає профіль доступу (<c>AccessProfile</c>).</param>
/// <param name="IsDenied">Чи є явна заборона, що перекриває все.</param>
/// <param name="DenyReason"><c>ExplicitDeny</c> або <c>NoGrant</c>; <c>null</c> — рівень є.</param>
/// <param name="GroupsFromTicket">Чи враховані групи сесії; для чужого запису — ні (`P-02`).</param>
/// <param name="Contributions">Усі внески, включно з тими, що не порахувалися.</param>
/// <param name="Caveat">
/// <see cref="DocumentStateNotConsidered"/> для аркуша, таблиці й колонки: розріз не знає стану документа
/// й звужень області періодами; для довідника й проєкту — <c>null</c>.
/// </param>
/// <param name="ProjectId">Проєкт, у шаблоні якого розглянуто аркуш, таблицю чи колонку; інакше <c>null</c>.</param>
/// <param name="LevelMayExceedActual">
/// Є призначення, звужене аркушами чи періодами (<c>Narrowed</c>): розріз бачить лише аркушні звуження,
/// тож фактичний рівень у конкретному періоді може бути НИЖЧИМ за показаний.
/// </param>
public sealed record EffectiveAccessView(
    int UserId,
    string UserName,
    string Resource,
    GrantLevel Level,
    bool IsDenied,
    string? DenyReason,
    bool GroupsFromTicket,
    IReadOnlyList<EffectiveAccessContribution> Contributions,
    string? Caveat = null,
    int? ProjectId = null,
    bool LevelMayExceedActual = false)
{
    /// <summary>Значення <see cref="Caveat"/>: рівень без стану документа й без звужень за періодом.</summary>
    public const string DocumentStateNotConsidered = "DocumentStateNotConsidered";
}

/// <summary>
/// Розріз ефективного доступу людини до ресурсу з атрибуцією внесків (ФВ-6.16).
/// </summary>
/// <remarks>
/// ⚠ Обробник нічого не ВИРІШУЄ: підсумковий рівень бере з <see cref="IAccessDecisionService.BuildProfileAsync"/>
/// (<see cref="AccessProfile.LevelFor"/> і, для довідника, глобальні <c>Registry.View</c>/<c>Registry.EditData</c>,
/// як <c>RegistryAccess</c>). Внески лише пояснюють його — друга реалізація правил показувала б
/// доступ, якого немає. Ресурси — довідник, проєкт, а також аркуш, таблиця й колонка В ПРОЄКТІ (їхні
/// ідентифікатори — версії шаблону, спільної для проєктів, тож проєкт обов'язковий).
///
/// ⚠ Для аркуша, таблиці й колонки розріз — «призначення + гранти по ланцюжку предків Проєкт → Аркуш →
/// Таблиця → Колонка» тим самим <see cref="EditRules.Effective"/>, що й рішення про комірку, але БЕЗ стану
/// документа (подання, затвердження, закритий період) і без звужень області періодами. Тому відповідь
/// несе <see cref="EffectiveAccessView.Caveat"/>, а клієнт показує його поруч із рівнем: розріз без цього
/// застереження брехав би про те, що людина зможе зробити з коміркою зараз.
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
    /// <param name="projectId">Проєкт — лише для аркуша, таблиці й колонки (їхні id спільні для проєктів шаблону).</param>
    public async Task<EffectiveAccessView> HandleAsync(int userId, string? resource, int? projectId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var (kind, resourceId) = Parse(resource);

        if (kind is ResourceKind.Sheet or ResourceKind.Table or ResourceKind.Column)
        {
            return await HandleTemplateResourceAsync(userId, kind, resourceId, projectId, ct).ConfigureAwait(false);
        }

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
            // Permissions.Contains — те саме, що Has(code): читання вже відбудованого профілю, не нове рішення.
            if (profile.Permissions.Contains(RegistryEditData))
            {
                level = Max(level, GrantLevel.Write);
            }
            else if (profile.Permissions.Contains(RegistryView))
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

    /// <summary>Аркуш, таблиця, колонка: ланцюжок предків у проєкті, рівень — <see cref="EditRules.Effective"/>.</summary>
    private async Task<EffectiveAccessView> HandleTemplateResourceAsync(
        int userId, ResourceKind kind, int resourceId, int? projectId, CancellationToken ct)
    {
        var requested = $"{kind}:{resourceId.ToString(CultureInfo.InvariantCulture)}";
        if (projectId is not > 0)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                "Для аркуша, таблиці й колонки потрібен проєкт: projectId.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "err.ECR-REQ-0422.effectiveAccessProject",
                });
        }

        var project = projectId.Value;
        var user = await users.FindByIdAsync(userId, ct).ConfigureAwait(false)
                   ?? throw new NotFoundException(
                       ErrorCodes.SecurityPrincipalNotFound,
                       $"Користувача {userId} не знайдено.",
                       new Dictionary<string, object?>
                       {
                           ["messageKey"] = "err.ECR-SEC-0404.userNotFound",
                           ["userId"] = userId.ToString(CultureInfo.InvariantCulture),
                       });

        if (!await store.ResourceExistsAsync(ResourceKind.Project, project, ct).ConfigureAwait(false))
        {
            throw new NotFoundException(
                ErrorCodes.ProjectNotFound,
                $"Проєкт {project} не знайдено.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRJ-0404.project",
                    ["projectId"] = project.ToString(CultureInfo.InvariantCulture),
                });
        }

        var chain = await store.ResolveChainAsync(kind, resourceId, project, ct).ConfigureAwait(false);
        if (chain is null)
        {
            var exists = await store.ResourceExistsAsync(kind, resourceId, ct).ConfigureAwait(false);
            throw new NotFoundException(
                ErrorCodes.TemplateNotFound,
                exists
                    ? $"{requested} не з шаблону проєкту {project}."
                    : $"{requested} не знайдено.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = exists
                        ? "err.ECR-TMPL-0404.effectiveAccessNotInProject"
                        : "err.ECR-TMPL-0404.effectiveAccessResource",
                    ["resource"] = requested,
                    ["projectId"] = project.ToString(CultureInfo.InvariantCulture),
                });
        }

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        var own = userId == currentUser.UserId;
        var groupSids = own ? currentUser.GroupSids : [];
        var asOf = DateOnly.FromDateTime(clock.UtcNow);

        // Предки від найширшого до найдрібнішого: той самий порядок, що в EditRules.Effective.
        var path = new List<(ResourceKind Kind, int Id)> { (ResourceKind.Project, project), (ResourceKind.Sheet, chain.SheetDefId) };
        if (chain.TableDefId is { } tableId)
        {
            path.Add((ResourceKind.Table, tableId));
        }

        if (chain.ColumnDefId is { } columnId)
        {
            path.Add((ResourceKind.Column, columnId));
        }

        var contributions = new List<(int Depth, EffectiveAccessContribution Row)>();
        for (var depth = 0; depth < path.Count; depth++)
        {
            var (pathKind, pathId) = path[depth];
            var rows = await store.ListSourcesAsync(userId, groupSids, pathKind, pathId, [], asOf, ct).ConfigureAwait(false);
            var from = $"{pathKind}:{pathId.ToString(CultureInfo.InvariantCulture)}";
            foreach (var row in rows)
            {
                // Грант на предка — грант; область призначення дивиться на ПРОЄКТ, у якому питають.
                var scope = ClassifyScope(row, ResourceKind.Project, project, isGrant: true);
                string? narrowedBy = null;
                var counted = scope is "Unscoped" or "InScope";
                if (scope == "Narrowed" && RoleAssignmentScope.TryParse(row.ScopeJson!) is { } narrowedScope)
                {
                    narrowedBy = DescribeNarrowing(narrowedScope);
                    // EditRules.Effective питає шари з period = null: шар із періодами не діє, лише аркушний.
                    counted = !narrowedScope.HasPeriods && narrowedScope.IncludesSheet(chain.SheetCode);
                }

                contributions.Add((depth, new EffectiveAccessContribution(
                    "Grant", row.RoleCode, row.PrincipalSid, null, row.Level ?? GrantLevel.None, row.IsDeny,
                    scope, counted, from == requested ? null : from, narrowedBy)));
            }
        }

        // S2: грант нижче проєкту діє лише у видимому проєкті (грант ≥ Read, що сам враховано).
        var projectVisible = contributions.Any(c => c.Depth == 0 && c.Row.Counted && !c.Row.IsDeny
                                                    && c.Row.Level >= GrantLevel.Read);
        if (!projectVisible)
        {
            contributions = [.. contributions.Select(c => c.Depth > 0 && c.Row.Counted
                ? (c.Depth, c.Row with { Counted = false, NotCountedReason = "ProjectNotVisible" })
                : c)];
        }

        var context = default(CellAccessContext) with
        {
            ProjectId = project,
            SheetDefId = chain.SheetDefId,
            TableDefId = chain.TableDefId ?? 0,
            ColumnDefId = chain.ColumnDefId ?? 0,
            SheetCode = chain.SheetCode,
        };
        var level = EditRules.Effective(profile, context);
        var denied = path.Select(p => $"{p.Kind}:{p.Id.ToString(CultureInfo.InvariantCulture)}")
            .Any(key => HasDeny(profile, project, chain.SheetCode, key));

        return new EffectiveAccessView(
            user.Id,
            user.UserName,
            requested,
            level,
            denied,
            denied ? "ExplicitDeny" : level == GrantLevel.None ? "NoGrant" : null,
            own,
            [.. contributions
                .OrderBy(c => c.Depth)
                .ThenBy(c => c.Row.RoleCode, StringComparer.Ordinal)
                .ThenBy(c => c.Row.PrincipalSid, StringComparer.Ordinal)
                .Select(c => c.Row)],
            EffectiveAccessView.DocumentStateNotConsidered,
            project,
            contributions.Any(c => c.Row.Scope == "Narrowed"));
    }

    /// <summary>Заборона на ключ: ролі без області, ролі з областю проєкту, звужені шари, що діють на аркуш.</summary>
    private static bool HasDeny(AccessProfile profile, int projectId, string sheetCode, string key)
    {
        if (profile.Denies.Contains(key))
        {
            return true;
        }

        return profile.Scoped.TryGetValue(projectId, out var scoped)
               && (scoped.Denies.Contains(key)
                   || scoped.Narrowed.Any(l => l.AppliesTo(sheetCode, null) && l.Denies.Contains(key)));
    }

    private static GrantLevel Max(GrantLevel a, GrantLevel b) => a > b ? a : b;

    private static (ResourceKind Kind, int Id) Parse(string? resource)
    {
        var parts = (resource ?? string.Empty).Split(':', 2);

        if (parts.Length == 2
            && Enum.TryParse(parts[0].Trim(), ignoreCase: true, out ResourceKind kind)
            && kind is ResourceKind.Registry or ResourceKind.Project
                or ResourceKind.Sheet or ResourceKind.Table or ResourceKind.Column
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

    /// <summary>Чим звужено призначення: <c>Sheets:F1,F2</c>, <c>Periods:202401..202412</c>, обидва через <c>;</c>.</summary>
    private static string DescribeNarrowing(RoleAssignmentScope scope)
    {
        var parts = new List<string>();
        if (scope.SheetCodes.Count > 0)
        {
            parts.Add("Sheets:" + string.Join(',', scope.SheetCodes));
        }

        if (scope.HasPeriods)
        {
            parts.Add($"Periods:{scope.PeriodFrom?.Value.ToString(CultureInfo.InvariantCulture)}..{scope.PeriodTo?.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        return string.Join(';', parts);
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
