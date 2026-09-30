// src/Ecr.Application/Ports/IEffectiveAccessStore.cs
using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// Один рядок «звідки береться доступ»: призначення ролі людині плюс те, що ця роль
/// каже про конкретний ресурс (ФВ-6.16, D-220).
/// </summary>
/// <param name="RoleId">Роль.</param>
/// <param name="RoleCode">Код ролі.</param>
/// <param name="PrincipalSid">SID групи; <c>null</c> — призначення особисте.</param>
/// <param name="ValidFrom">Початок дії призначення.</param>
/// <param name="ValidTo">Кінець дії призначення.</param>
/// <param name="IsEffective">Чи чинне призначення на дату запиту (рахує домен).</param>
/// <param name="ScopeJson">Область дії призначення; <c>null</c> — без області.</param>
/// <param name="PermissionCode">Код функціонального права; <c>null</c> — рядок про ресурсний грант.</param>
/// <param name="Level">Рівень гранта; <c>null</c> для рядка про функціональне право.</param>
/// <param name="IsDeny">Явна заборона.</param>
public sealed record AccessSourceRow(
    int RoleId,
    string RoleCode,
    string? PrincipalSid,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    bool IsEffective,
    string? ScopeJson,
    string? PermissionCode,
    GrantLevel? Level,
    bool IsDeny);

/// <summary>Читання джерел доступу для розрізу «ресурс → рівень → грант ролі».</summary>
public interface IEffectiveAccessStore
{
    /// <summary>Чи існує ресурс.</summary>
    /// <param name="kind">Вид ресурсу.</param>
    /// <param name="resourceId">Ідентифікатор.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<bool> ResourceExistsAsync(ResourceKind kind, int resourceId, CancellationToken ct);

    /// <summary>
    /// Призначення людини (особисті й адресовані <paramref name="groupSids"/>) і те, що їхні
    /// ролі кажуть про ресурс.
    /// </summary>
    /// <param name="userId">Людина.</param>
    /// <param name="groupSids">Групи сесії; порожньо для чужого запису (`P-02`).</param>
    /// <param name="kind">Вид ресурсу.</param>
    /// <param name="resourceId">Ідентифікатор ресурсу.</param>
    /// <param name="permissionCodes">Функціональні права, які теж дають доступ до ресурсу.</param>
    /// <param name="asOf">Дата, на яку рахується чинність призначень.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Відбір призначень — той самий, що будує профіль (<c>AccessDecisionService.LoadAsync</c>):
    /// особисті плюс групові, найновіші першими, з тією самою стелею. Інший відбір показував би
    /// не те, за чим система вирішує доступ.
    /// </remarks>
    public Task<IReadOnlyList<AccessSourceRow>> ListSourcesAsync(
        int userId,
        IReadOnlyList<string> groupSids,
        ResourceKind kind,
        int resourceId,
        IReadOnlyCollection<string> permissionCodes,
        DateOnly asOf,
        CancellationToken ct);
}
