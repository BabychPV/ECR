// src/Ecr.Infrastructure/Persistence/ApprovalQueueStore.cs
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IApprovalQueueStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class ApprovalQueueStore(EcrDbContext db) : IApprovalQueueStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ApprovalQueueProject>> ListProjectsAsync(CancellationToken ct)
        => await (
                from p in db.Projects.AsNoTracking()
                join v in db.TemplateVersions.AsNoTracking() on p.TemplateVersionId equals v.Id
                join t in db.Templates.AsNoTracking() on v.TemplateId equals t.Id
                where p.Status != ProjectStatus.Archived && !p.IsArchiving
                select new ApprovalQueueProject(p.Id, p.Code, p.TemplateVersionId, t.Code, p.Status, p.IsArchiving))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<ApprovalQueueRoutes> ListActiveRoutesAsync(CancellationToken ct)
    {
        var routes = await db.ApprovalRoutes
            .AsNoTracking()
            .Include(r => r.Steps)
            .Where(r => r.IsActive)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var roleIds = routes.SelectMany(r => r.Steps).Select(s => s.RoleId).Distinct().ToList();
        var codes = roleIds.Count == 0
            ? []
            : await db.Roles
                .AsNoTracking()
                .Where(r => roleIds.Contains(r.Id))
                .Select(r => new { r.Id, r.Code })
                .ToDictionaryAsync(r => r.Id, r => r.Code, ct)
                .ConfigureAwait(false);

        return new ApprovalQueueRoutes(routes, codes);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ ОДИН запит на порцію (ім'я того, хто подав, — підзапитом у тому ж зверненні): без N+1. Порядок
    /// «момент подання, ключ стану» детермінований; курсор — keyset, а не зсув, тож нове подання не зсуває
    /// вже прочитане.
    /// </remarks>
    public async Task<IReadOnlyList<ApprovalQueueCandidate>> ListSubmittedAsync(
        IReadOnlyCollection<int> projectIds, bool requireIncludedSheet, ApprovalQueueCursor? after, int take,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(projectIds);

        if (projectIds.Count == 0)
        {
            return [];
        }

        var query =
            from a in db.ApprovalStates.AsNoTracking()
            join d in db.Documents.AsNoTracking() on a.DocumentId equals d.Id
            where a.Status == DocumentStatus.Submitted && projectIds.Contains(d.ProjectId)
            select new
            {
                a.Id,
                a.DocumentId,
                d.ProjectId,
                d.BusinessKey,
                a.SheetDefId,
                a.PeriodKey,
                SubmittedAt = a.SubmittedAt ?? DateTime.MinValue,
                a.SubmittedByUserId,
                a.CurrentStepId,
            };

        if (requireIncludedSheet)
        {
            query = query.Where(x => db.DocumentSheets.Any(
                s => s.DocumentId == x.DocumentId && s.SheetDefId == x.SheetDefId && s.IsIncluded));
        }

        if (after is not null)
        {
            var at = new DateTime(after.SubmittedAtTicks, DateTimeKind.Utc);
            var id = after.StateId;
            query = query.Where(x => x.SubmittedAt > at || (x.SubmittedAt == at && x.Id > id));
        }

        var page = await query
            .OrderBy(x => x.SubmittedAt)
            .ThenBy(x => x.Id)
            .Take(take)
            .Select(x => new
            {
                x.Id,
                x.DocumentId,
                x.ProjectId,
                x.BusinessKey,
                x.SheetDefId,
                x.PeriodKey,
                x.SubmittedAt,
                x.SubmittedByUserId,
                x.CurrentStepId,
                SubmittedByDisplayName = db.Users
                    .Where(u => u.Id == x.SubmittedByUserId)
                    .Select(u => u.DisplayName)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return
        [
            .. page.Select(x => new ApprovalQueueCandidate(
                x.Id,
                x.DocumentId,
                x.ProjectId,
                x.BusinessKey,
                x.SheetDefId,
                x.PeriodKey,
                DateTime.SpecifyKind(x.SubmittedAt, DateTimeKind.Utc),
                x.SubmittedByUserId,
                x.SubmittedByDisplayName,
                x.CurrentStepId)),
        ];
    }
}
