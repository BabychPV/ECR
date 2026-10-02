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
            ResourceKind.Sheet => db.SheetDefs.AsNoTracking().AnyAsync(s => s.Id == resourceId, ct),
            ResourceKind.Table => db.TableDefs.AsNoTracking().AnyAsync(t => t.Id == resourceId, ct),
            ResourceKind.Column => db.ColumnDefs.AsNoTracking().AnyAsync(c => c.Id == resourceId, ct),
            _ => Task.FromResult(false),
        };

    /// <inheritdoc />
    public async Task<ResourceChain?> ResolveChainAsync(ResourceKind kind, int resourceId, int projectId, CancellationToken ct)
    {
        var version = await db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => (int?)p.TemplateVersionId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (version is not { } versionId)
        {
            return null;
        }

        switch (kind)
        {
            case ResourceKind.Sheet:
                var sheet = await db.SheetDefs.AsNoTracking()
                    .Where(s => s.Id == resourceId && s.TemplateVersionId == versionId)
                    .Select(s => new { s.Id, s.Code })
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);
                return sheet is null ? null : new ResourceChain(sheet.Id, sheet.Code, null, null);

            case ResourceKind.Table:
                var table = await (from t in db.TableDefs.AsNoTracking()
                                   join s in db.SheetDefs.AsNoTracking() on t.SheetDefId equals s.Id
                                   where t.Id == resourceId && s.TemplateVersionId == versionId
                                   select new { SheetId = s.Id, s.Code, TableId = t.Id })
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);
                return table is null ? null : new ResourceChain(table.SheetId, table.Code, table.TableId, null);

            case ResourceKind.Column:
                var column = await (from c in db.ColumnDefs.AsNoTracking()
                                    join t in db.TableDefs.AsNoTracking() on c.TableDefId equals t.Id
                                    join s in db.SheetDefs.AsNoTracking() on t.SheetDefId equals s.Id
                                    where c.Id == resourceId && s.TemplateVersionId == versionId
                                    select new { SheetId = s.Id, s.Code, TableId = t.Id, ColumnId = c.Id })
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);
                return column is null ? null : new ResourceChain(column.SheetId, column.Code, column.TableId, column.ColumnId);

            default:
                return null;
        }
    }

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
