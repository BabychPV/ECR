using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Security;

/// <summary>Джерела доступу до ресурсу для розрізу ФВ-6.16 (D-220).</summary>
/// <remarks>
/// ⛔ Відбір призначень — дослівно той, що в <c>AccessDecisionService.LoadAsync</c>: особисті плюс
/// адресовані SID із квитка, найновіші першими, стеля 200. Чинність рахує ДОМЕН
/// (<c>RoleAssignment.IsEffectiveOn</c>), а не копія умови в запиті (`H-23a`).
/// </remarks>
public sealed class EffectiveAccessStore(EcrDbContext db) : IEffectiveAccessStore
{
    private const int MaxAssignments = 200;

    /// <inheritdoc />
    public Task<bool> ResourceExistsAsync(ResourceKind kind, int resourceId, CancellationToken ct)
        => kind switch
        {
            ResourceKind.Registry => db.RegistryDefs.AsNoTracking().AnyAsync(r => r.Id == resourceId, ct),
            ResourceKind.Project => db.Projects.AsNoTracking().AnyAsync(p => p.Id == resourceId, ct),
            _ => Task.FromResult(false),
        };

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccessSourceRow>> ListSourcesAsync(
        int userId,
        IReadOnlyList<string> groupSids,
        ResourceKind kind,
        int resourceId,
        IReadOnlyCollection<string> permissionCodes,
        DateOnly asOf,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(groupSids);
        ArgumentNullException.ThrowIfNull(permissionCodes);

        var assignments = await db.RoleAssignments
            .AsNoTracking()
            .Where(a => a.UserId == userId || (a.PrincipalSid != null && groupSids.Contains(a.PrincipalSid)))
            .OrderByDescending(a => a.Id)
            .Take(MaxAssignments)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (assignments.Count == 0)
        {
            return [];
        }

        var roleIds = assignments.Select(a => a.RoleId).Distinct().ToList();

        var codes = await db.Roles
            .AsNoTracking()
            .Where(r => roleIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Code })
            .ToDictionaryAsync(r => r.Id, r => r.Code.ToString(), ct)
            .ConfigureAwait(false);

        var grants = (await db.ResourceGrants
                .AsNoTracking()
                .Where(g => roleIds.Contains(g.RoleId) && g.ResourceKind == kind && g.ResourceId == resourceId)
                .Select(g => new { g.RoleId, g.Level, g.IsDeny })
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToLookup(g => g.RoleId);

        var permissionRows = permissionCodes.Count == 0
            ? []
            : await db.RolePermissions
                .AsNoTracking()
                .Where(p => roleIds.Contains(p.RoleId) && permissionCodes.Contains(p.PermissionCode))
                .Select(p => new { p.RoleId, p.PermissionCode })
                .ToListAsync(ct)
                .ConfigureAwait(false);
        var permissions = permissionRows.ToLookup(p => p.RoleId, p => p.PermissionCode);

        var rows = new List<AccessSourceRow>();
        foreach (var assignment in assignments)
        {
            var roleCode = codes.TryGetValue(assignment.RoleId, out var code) ? code : assignment.RoleId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var effective = assignment.IsEffectiveOn(asOf);

            foreach (var grant in grants[assignment.RoleId])
            {
                rows.Add(new AccessSourceRow(
                    assignment.RoleId, roleCode, assignment.PrincipalSid, assignment.ValidFrom, assignment.ValidTo,
                    effective, assignment.ScopeJson, null, grant.Level, grant.IsDeny));
            }

            foreach (var permission in permissions[assignment.RoleId])
            {
                rows.Add(new AccessSourceRow(
                    assignment.RoleId, roleCode, assignment.PrincipalSid, assignment.ValidFrom, assignment.ValidTo,
                    effective, assignment.ScopeJson, permission, null, false));
            }
        }

        return rows;
    }
}
