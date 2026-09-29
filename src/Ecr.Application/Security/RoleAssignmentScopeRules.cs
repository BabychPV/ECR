// src/Ecr.Application/Security/RoleAssignmentScopeRules.cs
using System.Globalization;
using System.Text.Json.Serialization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Security;

/// <summary>Область дії призначення ролі в тілі запиту й у відповіді (ФВ-6.14, D-214).</summary>
/// <param name="Projects">Проєкти, у яких роль діє; непорожньо, без повторів.</param>
/// <param name="Sheets">
/// Коди аркушів (<c>SheetDef.Code</c>) — роль діє лише на них; <c>null</c> чи
/// порожньо — на всіх аркушах.
/// </param>
/// <param name="Periods">Проміжок звітних періодів; <c>null</c> — усі періоди.</param>
/// <remarks>
/// Та сама форма, що й збережений <c>sec.RoleAssignment.ScopeJson</c> — див.
/// <see cref="RoleAssignmentScope"/>. ⛔ Невідоме поле в тілі запиту — не
/// «проігнорувати», а <c>422</c> (<c>JsonUnmappedMemberHandling.Disallow</c>):
/// поле, яке надіслали й не застосували, розширило б права мовчки.
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RoleScopeDto(
    IReadOnlyList<int> Projects,
    IReadOnlyList<string>? Sheets = null,
    RoleScopePeriodsDto? Periods = null)
{
    /// <summary>Збережена область у формі відповіді.</summary>
    /// <param name="scopeJson">Вміст <c>ScopeJson</c>; <c>null</c> — області немає.</param>
    /// <returns>
    /// <c>null</c> — роль діє скрізь. Зіпсована область — порожній перелік
    /// проєктів (роль не діє ніде), а не <c>null</c>.
    /// </returns>
    public static RoleScopeDto? FromStored(string? scopeJson)
    {
        if (scopeJson is null)
        {
            return null;
        }

        var scope = RoleAssignmentScope.TryParse(scopeJson);
        if (scope is null)
        {
            return new RoleScopeDto([]);
        }

        return new RoleScopeDto(
            scope.ProjectIds,
            scope.SheetCodes.Count == 0 ? null : scope.SheetCodes,
            scope.HasPeriods ? new RoleScopePeriodsDto(scope.PeriodFrom?.Value, scope.PeriodTo?.Value) : null);
    }
}

/// <summary>Проміжок звітних періодів області (D-214): межі включні, будь-яка може бути відкритою.</summary>
/// <param name="From">Перший період (<c>Рік*100+Номер</c>, напр. <c>202601</c>); <c>null</c> — від початку.</param>
/// <param name="To">Останній період; <c>null</c> — без кінця.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RoleScopePeriodsDto(int? From = null, int? To = null);

/// <summary>Перевірка області дії, яку адміністратор задає призначенню.</summary>
internal static class RoleAssignmentScopeRules
{
    /// <summary>
    /// Перевіряє область і повертає її доменну форму.
    /// </summary>
    /// <param name="scope">Область із запиту.</param>
    /// <param name="roleCode">Роль, якій область задається, — для подробиць відмови.</param>
    /// <param name="actor">Профіль того, хто призначає.</param>
    /// <param name="documents">Сховище — для перевірки існування проєкту.</param>
    /// <param name="catalog">Довідник аркушів проєктів — для перевірки кодів аркушів.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException">Порожньо, повтор, проєкту чи аркуша немає, некоректний період — <c>422 ECR-REQ-0422</c>.</exception>
    /// <exception cref="AccessDeniedException">Немає <c>Manage</c> на проєкт — <c>403 ECR-AUTH-0403</c>.</exception>
    /// <remarks>
    /// ⛔ Обмежити роль проєктом може лише той, хто цим проєктом КЕРУЄ
    /// (<c>Manage</c>) — на додачу до <c>Security.ManageUsers</c>, яке вже
    /// перевірив обробник. Інакше область стала б способом розпоряджатися
    /// чужим проєктом.
    ///
    /// ⚠ Проєкт, якого той, хто призначає, не бачить (<c>None</c>), дає ту
    /// саму відповідь, що й неіснуючий (<c>ФВ-14.2</c>): різниця між 422 і 403
    /// сама розкривала б, що проєкт існує.
    ///
    /// ⚠ D-214: код аркуша має існувати в чинній версії шаблону бодай одного
    /// проєкту області; записується в тому регістрі, в якому він у шаблоні
    /// (профіль порівнює коди ординально). Ключ періоду — <c>Рік*100+Номер</c>.
    /// </remarks>
    public static async Task<RoleAssignmentScope> ValidateAsync(
        RoleScopeDto scope, string roleCode, AccessProfile actor, IDocumentStore documents,
        IResourceNameResolver catalog, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(catalog);

        var projects = scope.Projects ?? [];
        if (projects.Count == 0 || projects.Any(id => id <= 0) || projects.Distinct().Count() != projects.Count)
        {
            throw Invalid(roleCode, $"Роль «{roleCode}»: область дії — непорожній перелік проєктів без повторів.");
        }

        foreach (var projectId in projects)
        {
            var level = actor.LevelFor(ResourceKind.Project, projectId);
            var exists = await documents.FindProjectStatusAsync(projectId, ct).ConfigureAwait(false) is not null;
            var id = projectId.ToString(CultureInfo.InvariantCulture);

            if (!exists || level == GrantLevel.None)
            {
                throw new BusinessRuleException(
                    ErrorCodes.RequestInvalid,
                    $"Роль «{roleCode}»: проєкту {id} в області дії не існує.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-REQ-0422",
                        ["code"] = roleCode,
                        ["projectId"] = id,
                    });
            }

            if (level < GrantLevel.Manage)
            {
                throw new AccessDeniedException(
                    "ECR-AUTH-0403",
                    $"Обмежити роль проєктом {id} може лише той, хто має на нього Manage.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-AUTH-0403.noProjectManageGrant",
                        ["projectId"] = id,
                    });
            }
        }

        var sheets = await CanonicalSheetsAsync(scope.Sheets ?? [], projects, roleCode, catalog, ct).ConfigureAwait(false);
        var from = PeriodBound(scope.Periods?.From, roleCode);
        var to = PeriodBound(scope.Periods?.To, roleCode);

        try
        {
            return RoleAssignmentScope.Create(projects, sheets, from, to);
        }
        catch (Domain.Abstractions.DomainException)
        {
            // Повтор кодів без огляду на регістр чи «з» пізніше «по».
            throw Invalid(roleCode, $"Роль «{roleCode}»: аркуші без повторів, початок періодів не пізніше кінця.");
        }
    }

    /// <summary>Коди аркушів у регістрі шаблону; невідомий код — 422.</summary>
    private static async Task<List<string>> CanonicalSheetsAsync(
        IReadOnlyList<string> requested, IReadOnlyList<int> projects, string roleCode,
        IResourceNameResolver catalog, CancellationToken ct)
    {
        if (requested.Count == 0)
        {
            return [];
        }

        var known = (await catalog.ListProjectSheetsAsync(projects, ct).ConfigureAwait(false))
            .Select(s => s.Code)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var result = new List<string>(requested.Count);
        foreach (var code in requested)
        {
            var canonical = code is null ? null : known.Find(k => string.Equals(k, code.Trim(), StringComparison.OrdinalIgnoreCase));
            if (canonical is null)
            {
                throw new BusinessRuleException(
                    ErrorCodes.RequestInvalid,
                    $"Роль «{roleCode}»: аркуша «{code}» немає в шаблонах проєктів області.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-REQ-0422",
                        ["code"] = roleCode,
                        ["sheetCode"] = code,
                    });
            }

            result.Add(canonical);
        }

        return result;
    }

    /// <summary>Межа проміжку періодів; некоректний ключ — 422.</summary>
    private static PeriodKey? PeriodBound(int? value, string roleCode)
    {
        if (value is not { } raw)
        {
            return null;
        }

        var key = new PeriodKey(raw);
        return key.IsValid
            ? key
            : throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Роль «{roleCode}»: {raw.ToString(CultureInfo.InvariantCulture)} не є ключем періоду (Рік*100+Номер).",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422",
                    ["code"] = roleCode,
                    ["periodKey"] = raw.ToString(CultureInfo.InvariantCulture),
                });
    }

    private static BusinessRuleException Invalid(string roleCode, string message)
        => new(
            ErrorCodes.RequestInvalid,
            message,
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-REQ-0422",
                ["code"] = roleCode,
            });
}
